using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.P2P;

/// <summary>Station guard / alarm modes (http/types.js GuardMode).</summary>
public enum GuardMode
{
    Away = 0,
    Home = 1,
    Schedule = 2,
    Custom1 = 3,
    Custom2 = 4,
    Custom3 = 5,
    Off = 6,
    Geo = 47,
    Disarmed = 63,
}

/// <summary>Station facts the commands need, taken from the cloud station list.</summary>
public sealed record StationCommandInfo(string StationSn, string P2PDid, string? AdminUserId, string? NickName, string? SoftwareVersion);

/// <summary>Returns the RSA private key text of a cloud cipher (v2/app/cipher/get_ciphers), or null.</summary>
public delegate Task<string?> CipherProvider(int cipherId, CancellationToken cancellationToken);

/// <summary>
/// HomeBase operations on top of a connected session: encryption setup (CMD_GATEWAYINFO), guard mode,
/// and the alarm mode the station reports. Port of the relevant parts of http/station.js.
/// </summary>
public sealed class HomeBaseClient : IDisposable
{
    private static readonly TimeSpan GatewayInfoTimeout = TimeSpan.FromSeconds(5);
    private static readonly Version PayloadGuardModeMinVersion = new(2, 0, 7, 9);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly StationCommandInfo _station;
    private readonly CipherProvider _cipherProvider;
    private readonly ILogger _logger;
    private TaskCompletionSource<P2PMessage>? _gatewayInfo;

    public HomeBaseClient(P2PSession session, StationCommandInfo station, CipherProvider cipherProvider,
        GuardMode? guardMode = null, ILoggerFactory? loggerFactory = null, P2PCommandChannelOptions? channelOptions = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        _station = station;
        _cipherProvider = cipherProvider;
        _logger = loggerFactory.CreateLogger<HomeBaseClient>();
        GuardMode = guardMode;
        Channel = new P2PCommandChannel(session, station.StationSn, station.P2PDid, channelOptions, loggerFactory.CreateLogger<P2PCommandChannel>());
        Channel.ControlMessage += OnControlMessage;
    }

    public P2PCommandChannel Channel { get; }

    public string StationSn => _station.StationSn;

    /// <summary>The selected mode (e.g. Schedule). Seeded from the cloud, updated after a successful set.</summary>
    public GuardMode? GuardMode { get; private set; }

    /// <summary>The mode the station currently applies, as reported with CMD_GET_ALARM_MODE (e.g. Home while Schedule is selected).</summary>
    public GuardMode? AlarmMode { get; private set; }

    public event Action<GuardMode>? GuardModeChanged;
    public event Action<GuardMode>? AlarmModeChanged;

    /// <summary>Events the station reports on its own (alarm, detections, sensors …). Raw messages: <see cref="P2PCommandChannel.ControlMessage"/>.</summary>
    public event Action<StationEvent>? Event;

    /// <summary>
    /// Asks for CMD_GATEWAYINFO and sets up command encryption: Level 2 when the cipher unwraps the session key,
    /// Level 1 otherwise, None when the station does not answer (same as Node).
    /// </summary>
    public async Task<EncryptionLevel> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _gatewayInfo = new TaskCompletionSource<P2PMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Completes on a DATA result, or early when the key arrives on the CONTROL channel (OnControlMessage).
            await Channel.SendAsync(P2PCommand.Void(CommandType.CmdGatewayinfo), GatewayInfoTimeout, cancellationToken);
        }
        catch (P2PCommandTimeoutException ex) when (ex.Stage == "result")
        {
            // The DATA result is optional.
        }

        P2PMessage reply;
        try
        {
            reply = await _gatewayInfo.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Station} sent no gateway info; commands stay unencrypted", _station.StationSn);
            return Channel.Encryption;
        }

        var (level, key) = await UnwrapKeyAsync(reply.Data, cancellationToken);
        Channel.SetEncryption(level, key);
        _logger.LogInformation("{Station} command encryption: {Level}", _station.StationSn, level);
        return level;
    }

    public async Task<P2PCommandResult> SetGuardModeAsync(GuardMode mode, CancellationToken cancellationToken = default)
    {
        var command = UsesPayloadGuardMode(_station.SoftwareVersion)
            ? P2PCommand.WithStringPayload(CommandType.CmdSetPayload, JsonSerializer.Serialize(new
            {
                account_id = _station.AdminUserId,
                cmd = (int)CommandType.CmdSetArming,
                mValue3 = 0,
                payload = new { mode_type = (int)mode, user_name = _station.NickName },
            }, JsonOptions))
            : P2PCommand.WithInt(CommandType.CmdSetArming, (uint)mode, _station.AdminUserId ?? "");

        var result = await Channel.SendAsync(command, cancellationToken: cancellationToken);
        if (result.IsSuccess && GuardMode != mode)
        {
            GuardMode = mode;
            GuardModeChanged?.Invoke(mode);
        }
        return result;
    }

    /// <summary>
    /// CMD_CAMERA_INFO (encrypted command): the station answers on CONTROL with a JSON document about itself and its
    /// cameras. Read-only, so it is also the check that the command encryption is accepted.
    /// </summary>
    public async Task<string> GetCameraInfoAsync(CancellationToken cancellationToken = default)
    {
        var message = await SendAndWaitForControlAsync(P2PCommand.WithInt(CommandType.CmdCameraInfo, 255),
            m => m.CommandId == CommandType.CmdCameraInfo, cancellationToken);
        return HomeBaseEventParser.PayloadText(message.Data);
    }

    /// <summary>Recording count per camera (CMD_DATABASE_QUERY_LATEST_INFO). Raw reply JSON. Read-only.</summary>
    public Task<JsonElement> QueryLatestRecordInfoAsync(CancellationToken cancellationToken = default) =>
        QueryDatabaseAsync(CommandType.CmdDatabaseQueryLatestInfo, new
        {
            cmd = (int)CommandType.CmdDatabaseQueryLatestInfo,
            table = "history_record_info",
            transaction = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        }, cancellationToken);

    /// <summary>
    /// The station's own recordings of the given cameras between two days. <paramref name="query"/> is
    /// CMD_DATABASE_QUERY_LOCAL (with crop pictures) or CMD_DATABASE_QUERY_BY_DATE (flat list). Raw reply JSON. Read-only.
    /// </summary>
    public Task<JsonElement> QueryRecordsAsync(CommandType query, IEnumerable<string> deviceSns, DateOnly startDate, DateOnly endDate,
        int count = 20, int storageCloud = -1, CancellationToken cancellationToken = default) =>
        QueryDatabaseAsync(query, new
        {
            cmd = (int)query,
            payload = new
            {
                count,
                detection_type = 0,
                device_info = deviceSns.Select(sn => new { device_sn = sn }).ToArray(),
                end_date = endDate.ToString("yyyyMMdd"),
                event_type = 0,
                flag = 0,
                res_unzip = 1,
                start_date = startDate.ToString("yyyyMMdd"),
                start_time = $"{startDate:yyyyMMdd}000000",
                storage_cloud = storageCloud,
                ai_type = 0,
            },
            table = "history_record_info",
            transaction = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        }, cancellationToken);

    /// <summary>CMD_SET_PAYLOAD wrapping CMD_DATABASE (station.js databaseQuery*); the answer is a CMD_DATABASE control message.</summary>
    private async Task<JsonElement> QueryDatabaseAsync(CommandType query, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new
        {
            account_id = _station.AdminUserId,
            cmd = (int)CommandType.CmdDatabase,
            mChannel = 0,
            mValue3 = 0,
            payload,
        }, JsonOptions);
        var message = await SendAndWaitForControlAsync(P2PCommand.WithStringPayload(CommandType.CmdSetPayload, json, channel: 0),
            m => m.CommandId == CommandType.CmdDatabase && DatabaseCommand(m) == (int)query, cancellationToken);
        using var document = JsonDocument.Parse(HomeBaseEventParser.PayloadText(message.Data));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A picture stored on the station, e.g. the crop path from <see cref="QueryLatestRecordInfoAsync"/>
    /// (station.js downloadImage, CMD_DATABASE_IMAGE). Returns the JPEG. Read-only.
    /// </summary>
    public async Task<byte[]> DownloadImageAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            account_id = _station.AdminUserId,
            cmd = (int)CommandType.CmdDatabaseImage,
            mChannel = P2PCommand.StationChannel,
            payload = new[] { new { file = path } },
            transaction = path,
        }, JsonOptions);
        var message = await SendAndWaitForControlAsync(P2PCommand.WithStringPayload(CommandType.CmdSetPayload, json),
            m => m.CommandId == CommandType.CmdDatabaseImage && ImageReply(m)?.File == path, cancellationToken);
        var reply = ImageReply(message)!;
        return EufyImage.Decode(_station.P2PDid, EufyImage.FromBase64(reply.Content));
    }

    private sealed record ImageReplyJson(string? File, string Content);

    private static ImageReplyJson? ImageReply(P2PMessage message)
    {
        try
        {
            using var document = JsonDocument.Parse(HomeBaseEventParser.PayloadText(message.Data));
            var root = document.RootElement;
            return root.TryGetProperty("content", out var content) && content.GetString() is { } text
                ? new ImageReplyJson(root.TryGetProperty("file", out var file) ? file.GetString() : null, text)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? DatabaseCommand(P2PMessage message)
    {
        try
        {
            using var document = JsonDocument.Parse(HomeBaseEventParser.PayloadText(message.Data));
            return document.RootElement.TryGetProperty("cmd", out var cmd) && cmd.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<P2PMessage> SendAndWaitForControlAsync(P2PCommand command, Func<P2PMessage, bool> isReply, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<P2PMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(P2PMessage m)
        {
            if (isReply(m))
                reply.TrySetResult(m);
        }

        // Node handles replies alike on CONTROL and DATA; database answers came on CONTROL.
        Channel.ControlMessage += OnReply;
        Channel.UnmatchedMessage += OnReply;
        try
        {
            var result = await Channel.SendAsync(command, cancellationToken: cancellationToken);
            if (!result.IsSuccess)
                throw new InvalidOperationException($"{command.Type} failed: {result.ReturnCodeName}");
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            Channel.ControlMessage -= OnReply;
            Channel.UnmatchedMessage -= OnReply;
        }
    }

    public void Dispose()
    {
        Channel.ControlMessage -= OnControlMessage;
        Channel.Dispose();
    }

    /// <summary>Firmware 2.0.7.9+ (all current HomeBase 2) wraps CMD_SET_ARMING in a CMD_SET_PAYLOAD JSON.</summary>
    internal static bool UsesPayloadGuardMode(string? softwareVersion) =>
        ParseVersion(softwareVersion) is { } version && version >= PayloadGuardModeMinVersion;

    internal static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split('.').Select(p => new string(p.TakeWhile(char.IsDigit).ToArray())).ToArray();
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0))
            return null;
        return new Version(string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length))));
    }

    private async Task<(EncryptionLevel, byte[]?)> UnwrapKeyAsync(byte[] data, CancellationToken ct)
    {
        if (data.Length < 4)
            return (EncryptionLevel.Level1, null);

        var cipherId = BinaryPrimitives.ReadUInt16LittleEndian(data);
        try
        {
            var privateKey = await _cipherProvider(cipherId, ct);
            if (privateKey is null)
            {
                _logger.LogWarning("No cloud cipher {CipherId} for {Station}; using Level 1", cipherId, _station.StationSn);
                return (EncryptionLevel.Level1, null);
            }
            var key = P2PCrypto.DecryptGatewayKey(privateKey, data.AsSpan(4));
            if (key.Length == 16)
                return (EncryptionLevel.Level2, key);
            _logger.LogWarning("Gateway key has {Length} bytes, expected 16; using Level 1", key.Length);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("Cannot use cloud cipher {CipherId}: {Reason}. Using Level 1 (eufy-security-client does the same)", cipherId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unwrapping the gateway key (cipher {CipherId}) failed; using Level 1", cipherId);
        }
        return (EncryptionLevel.Level1, null);
    }

    private void OnControlMessage(P2PMessage message)
    {
        switch (message.CommandId)
        {
            case CommandType.CmdCameraInfo or CommandType.CmdDatabase or CommandType.CmdDatabaseImage:
                break; // replies, answered through SendAndWaitForControlAsync

            case CommandType.CmdGatewayinfo:
                _gatewayInfo?.TrySetResult(message);
                Channel.TryCompletePending(new P2PCommandResult(CommandType.CmdGatewayinfo, 0, []));
                break;

            case CommandType.CmdGetAlarmMode when message.Data.Length > 0:
                var mode = (GuardMode)message.Data[0];
                _logger.LogInformation("{Station} alarm mode is now {Mode}", _station.StationSn, mode);
                if (AlarmMode != mode)
                {
                    AlarmMode = mode;
                    AlarmModeChanged?.Invoke(mode);
                }
                RaiseEvent(message);
                break;

            default:
                _logger.LogDebug("{Station} control {Command} ({Length} bytes)", _station.StationSn, message.CommandId, message.Data.Length);
                RaiseEvent(message);
                break;
        }
    }

    private void RaiseEvent(P2PMessage message)
    {
        if (HomeBaseEventParser.Parse(_station.StationSn, message, DateTimeOffset.Now) is { } stationEvent)
            Event?.Invoke(stationEvent);
    }
}
