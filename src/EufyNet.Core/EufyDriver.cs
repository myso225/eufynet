using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using EufyNet.Cloud;
using EufyNet.P2P;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.Core;

/// <summary>Bound from the <c>Eufy</c> configuration section, next to <see cref="EufyCloudOptions"/>.</summary>
public sealed class EufyDriverOptions
{
    /// <summary>Session, dumps and (optional) event capture. Relative to the working directory.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Ignore the saved cloud session and log in again.</summary>
    public bool ForceLogin { get; set; }

    /// <summary>Stop after logging in and listing stations and devices.</summary>
    public bool ListOnly { get; set; }

    public P2PConnectionMode P2PConnectionSetup { get; set; } = P2PConnectionMode.OnlyLocal;

    /// <summary>Station LAN IP by serial. The cloud only knows the public IP.</summary>
    public Dictionary<string, string> StationIpAddresses { get; set; } = [];

    public LocalMotionOptions LocalMotion { get; set; } = new();

    /// <summary>Append every station message to <c>events.jsonl</c> in <see cref="DataDir"/>.</summary>
    public bool CaptureEvents { get; set; }

    /// <summary>How often a connection status line is logged.</summary>
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The motion and recording counters cover this much time.</summary>
    public TimeSpan ActivityWindow { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How often the camera batteries are read again (station first, then the cloud device list).</summary>
    public TimeSpan BatteryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Download the station's crop picture after a recording, and the newest one at connect.</summary>
    public bool DownloadPictures { get; set; } = true;
}

public sealed class EufyLoginFailedException(LoginResult result)
    : Exception($"Eufy login {result.Status}: code {result.Code} {result.Message}")
{
    public LoginResult Result { get; } = result;
}

/// <summary>
/// Login → stations and devices → one P2P connection per station with its <see cref="HomeBaseClient"/> and
/// <see cref="LocalMotionDetector"/>. Raises login, state and events; executes <see cref="BridgeCommand"/>s.
/// </summary>
public sealed class EufyDriver : IEufyDriver
{
    private const int GuardModeParam = 1224;
    private const int BatteryParam = 1101;

    private readonly EufyCloudClient _cloud;
    private readonly EufyDriverOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Channel<LoginAnswer?> _loginAnswers = Channel.CreateUnbounded<LoginAnswer?>();
    private readonly ConcurrentDictionary<string, StationRuntime> _stations = new();
    private IReadOnlyList<DeviceInfo> _devices = [];
    private EventCapture? _capture;
    private ActivityStore _activity;
    private string _dataDir;

    public EufyDriver(EufyCloudClient cloud, EufyDriverOptions options, ILoggerFactory? loggerFactory = null)
    {
        _cloud = cloud;
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<EufyDriver>();
        _activity = new ActivityStore(null, options.ActivityWindow);
        _dataDir = Path.GetFullPath(options.DataDir);
    }

    public event Action<LoginState>? LoginChanged;

    public event Action<StationState>? StateChanged;

    public event Action<BridgeEvent>? EventRaised;

    public LoginState Login { get; private set; } = new(null);

    public IReadOnlyList<StationState> States => _stations.Values.Select(s => s.Snapshot()).ToList();

    /// <summary>Runs until cancelled (stations stay connected, sessions end with END) or, with ListOnly, until listed.</summary>
    /// <exception cref="EufyLoginFailedException">Wrong password, locked account …</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var dataDir = _dataDir;
        Directory.CreateDirectory(dataDir);

        await LoginAsync(cancellationToken);
        var stations = await _cloud.GetStationsAsync(cancellationToken);
        _devices = await _cloud.GetDevicesAsync(cancellationToken);
        foreach (var s in stations)
            _logger.LogInformation("Station {Station} {Name}: model {Model}, fw {Firmware}, p2p_did {Did}", s.StationSn, s.Name, s.Model, s.SoftwareVersion, s.P2PDid);
        foreach (var d in _devices)
            _logger.LogInformation("Device {Device} {Name}: model {Model}, station {Station}, channel {Channel}", d.DeviceSn, d.Name, d.Model, d.StationSn, d.Channel);

        // Raw dumps: app_conn, member, params … for debugging and new features.
        var indented = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(dataDir, "stations.json"), JsonSerializer.Serialize(stations.Select(s => s.Raw), indented), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "devices.json"), JsonSerializer.Serialize(_devices.Select(d => d.Raw), indented), cancellationToken);
        if (_options.ListOnly)
            return;

        _capture = _options.CaptureEvents ? new EventCapture(Path.Combine(dataDir, "events.jsonl")) : null;
        _activity = new ActivityStore(Path.Combine(dataDir, "activity.json"), _options.ActivityWindow, _logger);
        _activity.Prune(DateTimeOffset.Now);
        ApplyCloudBattery(_devices);
        foreach (var station in stations.Where(s => !string.IsNullOrEmpty(s.P2PDid)))
        {
            var runtime = new StationRuntime(this, station, _devices.Where(d => d.StationSn == station.StationSn).ToList());
            _stations[station.StationSn] = runtime;
            runtime.PublishState();
        }

        var runs = _stations.Values.Select(s => s.RunAsync(cancellationToken)).ToList();
        await Task.WhenAll(LogStatusAsync(cancellationToken), PruneActivityAsync(cancellationToken), RefreshBatteryAsync(cancellationToken));
        await Task.WhenAll(runs);
    }

    /// <summary>Battery % from the cloud device list, unless the station reported a newer value.</summary>
    private void ApplyCloudBattery(IEnumerable<DeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            var (value, updated) = device.GetParam(BatteryParam);
            if (!int.TryParse(value, out var percent))
                continue;
            if (_activity.Get(device.DeviceSn).BatteryUpdated is { } known && updated is { } cloudTime && cloudTime <= known)
                continue;
            if (_activity.SetBattery(device.DeviceSn, percent, updated))
                _logger.LogInformation("{Device} battery {Percent}% (cloud, {Updated:yyyy-MM-dd HH:mm})", device.Name ?? device.DeviceSn, percent, updated);
        }
    }

    /// <summary>The counters fall as motions leave the window, so the state is republished then.</summary>
    private async Task PruneActivityAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_activity.Prune(DateTimeOffset.Now))
                    PublishAll();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RefreshBatteryAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.BatteryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var station in _stations.Values)
                    await station.ReadBatteryAsync(ct);
                try
                {
                    ApplyCloudBattery(await _cloud.GetDevicesAsync(ct));
                }
                catch (Exception ex) when (ex is EufyApiException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Reading the batteries from the cloud failed: {Message}", ex.Message);
                }
                PublishAll();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void PublishAll()
    {
        foreach (var station in _stations.Values)
            station.PublishState();
    }

    public async Task<BridgeCommandResult> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                BridgeCommands.Status => Ok(command, data: JsonSerializer.SerializeToElement(States)),
                BridgeCommands.VerifyCode when command.Arg("code") is { Length: > 0 } code => Answer(command, LoginAnswer.WithVerifyCode(code)),
                BridgeCommands.ResendCode => Answer(command, null),
                BridgeCommands.Captcha when command.Arg("id") is { } id && command.Arg("answer") is { Length: > 0 } answer =>
                    Answer(command, LoginAnswer.WithCaptcha(id, answer)),
                BridgeCommands.SetGuardMode or BridgeCommands.Info or BridgeCommands.Clips or BridgeCommands.Picture =>
                    await ExecuteStationCommandAsync(command, cancellationToken),
                _ => Fail(command, $"Unknown command or missing arguments: {command.Name}"),
            };
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException or P2PCommandTimeoutException)
        {
            return Fail(command, ex.Message);
        }
    }

    private async Task<BridgeCommandResult> ExecuteStationCommandAsync(BridgeCommand command, CancellationToken ct)
    {
        var station = command.StationSn is { } sn ? _stations.GetValueOrDefault(sn) : _stations.Values.FirstOrDefault(s => s.Client is not null);
        if (station?.Client is not { } client)
            return Fail(command, command.StationSn is null ? "No station is connected" : $"Station {command.StationSn} is not connected");

        switch (command.Name)
        {
            case BridgeCommands.SetGuardMode:
                if (!Enum.TryParse<GuardMode>(command.Arg("mode"), ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
                    return Fail(command, $"Unknown guard mode {command.Arg("mode")}; one of {string.Join(", ", Enum.GetNames<GuardMode>())}");
                var result = await client.SetGuardModeAsync(mode, ct);
                return result.IsSuccess ? Ok(command, $"{station.StationSn} guard mode {mode}") : Fail(command, result.ReturnCodeName);

            case BridgeCommands.Info:
                var info = await client.GetCameraInfoAsync(ct);
                return Ok(command, data: JsonDocument.Parse(info).RootElement.Clone());

            case BridgeCommands.Picture:
                var wanted = Cameras(command, station);
                if (wanted.Count == 0)
                    return Fail(command, $"No camera {command.Arg("camera")} on {station.StationSn}");
                var downloaded = new List<string>();
                foreach (var record in LatestRecordInfo.Parse(await client.QueryLatestRecordInfoAsync(ct)).Where(r => wanted.Contains(r.DeviceSn)))
                {
                    if (record.CropPath is { } path && await station.DownloadImageAsync(client, record.DeviceSn, path, ct))
                        downloaded.Add(path);
                }
                return downloaded.Count > 0
                    ? Ok(command, $"Downloaded {string.Join(", ", downloaded)}")
                    : Fail(command, "No picture downloaded (no crop on the station, or the download failed; see the log)");

            default: // clips
                var cameras = Cameras(command, station);
                if (cameras.Count == 0)
                    return Fail(command, $"No camera {command.Arg("camera")} on {station.StationSn}");
                var latest = await client.QueryLatestRecordInfoAsync(ct);
                // HB2 3.4.2.6h answers QUERY_LOCAL with an empty list; kept for M7 (clip download).
                var today = DateOnly.FromDateTime(DateTime.Now);
                var local = await client.QueryRecordsAsync(CommandType.CmdDatabaseQueryLocal, cameras, today.AddDays(-1), today, cancellationToken: ct);
                return Ok(command, data: JsonSerializer.SerializeToElement(new { latest, local }));
        }
    }

    /// <summary>The station's cameras matching the optional <c>camera</c> arg (name or serial).</summary>
    private static List<string> Cameras(BridgeCommand command, StationRuntime station)
    {
        var filter = command.Arg("camera");
        return station.Devices
            .Where(d => filter is null
                || string.Equals(d.Name, filter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DeviceSn, filter, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.DeviceSn)
            .ToList();
    }

    private BridgeCommandResult Answer(BridgeCommand command, LoginAnswer? answer)
    {
        if (!Login.NeedsVerifyCode && !Login.NeedsCaptcha)
            return Fail(command, "The login is not waiting for an answer");
        _loginAnswers.Writer.TryWrite(answer);
        return Ok(command);
    }

    private static BridgeCommandResult Ok(BridgeCommand command, string? message = null, JsonElement? data = null) =>
        new(command.Id, command.Name, true, message, data);

    private static BridgeCommandResult Fail(BridgeCommand command, string message) => new(command.Id, command.Name, false, message);

    private async Task LoginAsync(CancellationToken ct)
    {
        SetLogin(new LoginState(null));
        var result = await _cloud.LoginAsync(force: _options.ForceLogin, cancellationToken: ct);
        while (result.Status != LoginStatus.Ok)
        {
            var state = new LoginState(result.Status, result.Message, result.Captcha);
            SetLogin(state);
            if (!state.NeedsVerifyCode && !state.NeedsCaptcha)
                throw new EufyLoginFailedException(result);

            // Null = send a new code.
            var answer = await _loginAnswers.Reader.ReadAsync(ct);
            result = answer is null
                ? await _cloud.LoginAsync(force: true, cancellationToken: ct)
                : await _cloud.LoginAsync(answer, cancellationToken: ct);
        }
        SetLogin(new LoginState(LoginStatus.Ok));
    }

    private void SetLogin(LoginState state)
    {
        Login = state;
        LoginChanged?.Invoke(state);
    }

    private async Task LogStatusAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var timer = new PeriodicTimer(_options.StatusInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var session in _stations.Values.Select(s => s.Connector.Current).OfType<P2PSession>())
                    _logger.LogInformation("Status: {State} to {Endpoint}, up {Uptime:hh\\:mm}, last pong {Pong:0.0}s ago, {Packets} DATA packets",
                        session.State, session.RemoteEndPoint, DateTime.UtcNow - started, session.SinceLastPong?.TotalSeconds, session.DataPacketsReceived);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One station: its connector, the client of the current session, and the state the transport sees.</summary>
    private sealed class StationRuntime
    {
        private readonly EufyDriver _driver;
        private readonly StationInfo _station;
        private readonly StationCommandInfo _info;
        private readonly IReadOnlyDictionary<int, string> _cameras;
        private GuardMode? _guardMode;
        private HomeBaseClient? _client;

        public StationRuntime(EufyDriver driver, StationInfo station, IReadOnlyList<DeviceInfo> devices)
        {
            _driver = driver;
            _station = station;
            Devices = devices;
            _info = new StationCommandInfo(station.StationSn, station.P2PDid!, station.AdminUserId, station.MemberNickName, station.SoftwareVersion);
            _cameras = devices.Where(d => d.Channel is not null).DistinctBy(d => d.Channel).ToDictionary(d => d.Channel!.Value, d => d.DeviceSn);
            if (int.TryParse(station.GetParam(GuardModeParam), out var cloudMode))
                _guardMode = (GuardMode)cloudMode;

            var options = driver._options;
            var localIp = options.StationIpAddresses.TryGetValue(station.StationSn, out var ip) ? IPAddress.Parse(ip) : null;
            var sessionOptions = new P2PSessionOptions
            {
                Mode = options.P2PConnectionSetup,
                DskKeyProvider = async ct => (await driver._cloud.GetDskKeyAsync(station.StationSn, ct))?.Key,
            };
            driver._logger.LogInformation("Connecting to {Station} ({Mode}, local IP {Ip})", station.StationSn, options.P2PConnectionSetup, localIp?.ToString() ?? "broadcast");
            Connector = new P2PStationConnector(new StationP2PInfo(station.StationSn, station.P2PDid!, localIp, station.AppConn), sessionOptions, driver._loggerFactory);
        }

        public string StationSn => _station.StationSn;

        public IReadOnlyList<DeviceInfo> Devices { get; }

        public P2PStationConnector Connector { get; }

        /// <summary>Set while a session is connected and initialised.</summary>
        public HomeBaseClient? Client => Volatile.Read(ref _client);

        public async Task RunAsync(CancellationToken ct)
        {
            var setups = new List<Task>();
            Connector.Connected += session =>
            {
                lock (setups)
                {
                    setups.RemoveAll(t => t.IsCompleted);
                    setups.Add(SetUpAsync(session, ct));
                }
            };
            await Connector.RunAsync(ct);
            Task[] pending;
            lock (setups)
                pending = [.. setups];
            await Task.WhenAll(pending);
        }

        public StationState Snapshot()
        {
            var client = Client;
            var store = _driver._activity;
            var from = DateTimeOffset.Now - store.Window;
            return new StationState(StationSn, _station.Name ?? StationSn, client is not null, client?.GuardMode ?? _guardMode, client?.AlarmMode,
                client?.Channel.Encryption,
                Devices.Select(d =>
                {
                    var a = store.Get(d.DeviceSn);
                    return new CameraState(d.DeviceSn, d.Name ?? d.DeviceSn, d.Channel, a.LastMotion, a.LastRecording,
                        a.Motions.Count(t => t > from), a.Recordings.Count(t => t > from), a.Battery, a.BatteryUpdated, a.ImageTakenAt);
                }).ToList(),
                store.Window);
        }

        /// <summary>Battery % per channel from CMD_CAMERA_INFO, when the station includes it (params with dev_type = channel).</summary>
        public async Task ReadBatteryAsync(CancellationToken ct)
        {
            if (Client is not { } client)
                return;
            var logger = _driver._logger;
            try
            {
                using var info = JsonDocument.Parse(await client.GetCameraInfoAsync(ct));
                var found = false;
                if (info.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in parameters.EnumerateArray())
                    {
                        if (Int(p, "param_type") != BatteryParam || Int(p, "dev_type") is not { } channel || Int(p, "param_value") is not { } percent
                            || !_cameras.TryGetValue(channel, out var deviceSn))
                            continue;
                        found = true;
                        if (_driver._activity.SetBattery(deviceSn, percent, DateTimeOffset.Now))
                            logger.LogInformation("{Device} battery {Percent}% (station)", NameOf(deviceSn), percent);
                    }
                }
                if (!found)
                    logger.LogDebug("{Station} camera info has no battery; using the cloud's", StationSn);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException or P2PCommandTimeoutException)
            {
                logger.LogWarning("{Station}: reading the batteries failed: {Message}", StationSn, ex.Message);
            }
        }

        private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            }
            : null;

        /// <summary>Downloads a crop picture, keeps it as <c>&lt;camera&gt;.jpg</c> in the data folder and raises <see cref="CameraImageEvent"/>.</summary>
        public async Task<bool> DownloadImageAsync(HomeBaseClient client, string deviceSn, string path, CancellationToken ct)
        {
            var logger = _driver._logger;
            try
            {
                var jpeg = await client.DownloadImageAsync(path, ct);
                if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
                {
                    var raw = Path.Combine(_driver._dataDir, $"{deviceSn}.raw");
                    await File.WriteAllBytesAsync(raw, jpeg, ct);
                    logger.LogWarning("{Device}: {Path} could not be decoded to a JPEG ({Length} bytes, starts {Start}); saved as {Raw}", NameOf(deviceSn), path,
                        jpeg.Length, Convert.ToHexString(jpeg.AsSpan(0, Math.Min(16, jpeg.Length))), raw);
                    return false;
                }
                var takenAt = new LatestRecordInfo(deviceSn, 0, path).ParseCropName() is { } crop ? crop.Time : (DateTimeOffset?)null;
                await File.WriteAllBytesAsync(Path.Combine(_driver._dataDir, $"{deviceSn}.jpg"), jpeg, ct);
                _driver._activity.SetImage(deviceSn, path, takenAt);
                var channel = Devices.FirstOrDefault(d => d.DeviceSn == deviceSn)?.Channel ?? -1;
                Raise(new CameraImageEvent(StationSn, DateTimeOffset.Now, deviceSn, channel, takenAt, path, jpeg));
                PublishState();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException or FormatException
                or P2PCommandTimeoutException or IOException or System.Security.Cryptography.CryptographicException or ObjectDisposedException)
            {
                logger.LogWarning("{Device}: downloading {Path} failed: {Message}", NameOf(deviceSn), path, ex.Message);
                return false;
            }
        }

        private string NameOf(string deviceSn) => Devices.FirstOrDefault(d => d.DeviceSn == deviceSn)?.Name ?? deviceSn;

        /// <summary>At connect: the newest crop of each camera, unless it is the picture already downloaded.</summary>
        private async Task DownloadNewPicturesAsync(HomeBaseClient client, IReadOnlyList<LatestRecordInfo> latest, CancellationToken ct)
        {
            foreach (var record in latest)
            {
                if (record.CropPath is { } path && _cameras.Values.Contains(record.DeviceSn) && _driver._activity.Get(record.DeviceSn).ImageFile != path)
                    await DownloadImageAsync(client, record.DeviceSn, path, ct);
            }
        }

        public void PublishState() => _driver.StateChanged?.Invoke(Snapshot());

        /// <summary>Per session: client, detector, encryption, baseline; torn down when the session ends.</summary>
        private async Task SetUpAsync(P2PSession session, CancellationToken ct)
        {
            var driver = _driver;
            var client = new HomeBaseClient(session, _info, (id, c) => driver._cloud.GetCipherPrivateKeyAsync(id, _info.AdminUserId ?? "", c),
                _guardMode, driver._loggerFactory);
            var motion = new LocalMotionDetector(client, _cameras, driver._options.LocalMotion, driver._loggerFactory.CreateLogger<LocalMotionDetector>());
            client.GuardModeChanged += m =>
            {
                _guardMode = m;
                PublishState();
            };
            client.AlarmModeChanged += _ => PublishState();
            client.Event += Raise;
            motion.Event += Raise;
            if (driver._capture is { } capture)
            {
                client.Channel.ControlMessage += m => capture.Write(StationSn, m);
                client.Channel.UnmatchedMessage += m => capture.Write(StationSn, m);
            }

            try
            {
                var encryption = await client.InitializeAsync(ct);
                try
                {
                    await motion.StartAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    driver._logger.LogWarning("{Station}: local motion detection is off, reading the recording counts failed: {Message}", StationSn, ex.Message);
                }
                Volatile.Write(ref _client, client);
                driver._logger.LogDebug("{Station} ready: guard mode {GuardMode}, encryption {Encryption}", StationSn, client.GuardMode?.ToString() ?? "unknown", encryption);
                await ReadBatteryAsync(ct);
                PublishState();
                if (driver._options.DownloadPictures)
                    await DownloadNewPicturesAsync(client, motion.Baseline, ct);
                await session.Completion.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopping; the connector closes the session.
            }
            catch (Exception ex)
            {
                driver._logger.LogWarning(ex, "Setting up {Station} failed", StationSn);
            }
            finally
            {
                Interlocked.CompareExchange(ref _client, null, client);
                await motion.DisposeAsync();
                client.Dispose();
                PublishState();
            }
        }

        private void Raise(StationEvent e)
        {
            var deviceSn = e switch
            {
                DetectionEvent d => d.DeviceSn,
                RecordingSavedEvent r => r.DeviceSn,
                PictureSavedEvent p => p.DeviceSn,
                CameraImageEvent i => i.DeviceSn,
                _ => ChannelOf(e) is { } channel ? _cameras.GetValueOrDefault(channel) : null,
            };
            var name = deviceSn is null ? null : NameOf(deviceSn);
            _driver.EventRaised?.Invoke(new BridgeEvent(e, deviceSn, name));

            if (deviceSn is null)
                return;
            switch (e)
            {
                case DetectionEvent d:
                    _driver._activity.AddMotion(deviceSn, d.EventTime ?? d.Time);
                    PublishState();
                    break;
                case RecordingSavedEvent r:
                    _driver._activity.AddRecording(deviceSn, r.Time);
                    PublishState();
                    if (r.CropPath is { } path && _driver._options.DownloadPictures && Client is { } client)
                        _ = DownloadImageAsync(client, deviceSn, path, CancellationToken.None);
                    break;
                case PictureSavedEvent p when _driver._options.DownloadPictures && Client is { } current:
                    _ = DownloadImageAsync(current, deviceSn, p.CropPath, CancellationToken.None);
                    break;
            }
        }

        private static int? ChannelOf(StationEvent e) => e switch
        {
            LightSwitchEvent l => l.Channel,
            CameraSirenEvent s => s.Channel,
            SensorStatusEvent s => s.Channel,
            StorageStatusEvent s => s.Channel,
            _ => null,
        };
    }
}
