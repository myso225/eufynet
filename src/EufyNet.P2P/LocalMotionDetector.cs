using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.P2P;

public sealed class LocalMotionOptions
{
    /// <summary>Light signals from the same camera within this time are one motion.</summary>
    public TimeSpan MotionCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the recording counts are read while a camera's recording is awaited.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long after a motion its recording is awaited.</summary>
    public TimeSpan RecordingTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>A crop picture younger than this is reported with the recording.</summary>
    public TimeSpan CropMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Also read the counts this often without a motion (still local), to catch missed signals. Null = off.</summary>
    public TimeSpan? PollInterval { get; set; }
}

/// <summary>One camera's entry in CMD_DATABASE_QUERY_LATEST_INFO.</summary>
public sealed record LatestRecordInfo(string DeviceSn, int EventCount, string? CropPath)
{
    private static readonly Regex CropName = new(@"(?<time>\d{14})_c(?<channel>\d+)\.jpg$", RegexOptions.CultureInvariant);

    /// <summary>When the crop was taken (station local time) and the camera channel, from names like 20261101115719_c00.jpg.</summary>
    public (DateTimeOffset Time, int Channel)? ParseCropName()
    {
        var match = CropPath is null ? null : CropName.Match(CropPath);
        if (match is not { Success: true }
            || !DateTime.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
            return null;
        return (new DateTimeOffset(time), int.Parse(match.Groups["channel"].Value, CultureInfo.InvariantCulture));
    }

    public static IReadOnlyList<LatestRecordInfo> Parse(JsonElement reply)
    {
        var result = new List<LatestRecordInfo>();
        if (!reply.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var record in data.EnumerateArray())
        {
            if (!record.TryGetProperty("device_sn", out var sn) || sn.GetString() is not { Length: > 0 } deviceSn
                || !record.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("event_count", out var count)
                || !count.TryGetInt32(out var eventCount))
                continue;
            var crop = payload.TryGetProperty("crop_hb3_path", out var path) ? path.GetString() : null;
            result.Add(new LatestRecordInfo(deviceSn, eventCount, string.IsNullOrEmpty(crop) ? null : crop));
        }
        return result;
    }
}

/// <summary>
/// Cloud-free motion detection for stations that do not push detections over P2P (HomeBase 2, seen live on 3.4.2.6h):
/// <list type="bullet">
/// <item>the light/IR switch message on a camera's channel is the motion → <see cref="DetectionEvent"/> right away;</item>
/// <item>the station's recording count for that camera (CMD_DATABASE_QUERY_LATEST_INFO, read-only) going up afterwards
/// → <see cref="RecordingSavedEvent"/>. Saving lags the motion by seconds, so the two are not matched one to one.</item>
/// </list>
/// </summary>
public sealed class LocalMotionDetector : IAsyncDisposable
{
    private readonly HomeBaseClient _client;
    private readonly IReadOnlyDictionary<int, string> _cameras;
    private readonly LocalMotionOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastMotion = new();
    /// <summary>Cameras whose recording is awaited → until when.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _awaiting = new();
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<string, int> _counts = [];
    private Task? _loop;

    /// <param name="cameras">Camera serial by channel (the cloud device list's device_channel).</param>
    public LocalMotionDetector(HomeBaseClient client, IReadOnlyDictionary<int, string> cameras, LocalMotionOptions? options = null,
        ILogger<LocalMotionDetector>? logger = null)
    {
        _client = client;
        _cameras = cameras;
        _options = options ?? new LocalMotionOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _client.Event += OnStationEvent;
    }

    /// <summary><see cref="DetectionEvent"/> and <see cref="RecordingSavedEvent"/>.</summary>
    public event Action<StationEvent>? Event;

    /// <summary>Reads the current counts as the baseline, then watches.</summary>
    /// <summary>The station's answer at <see cref="StartAsync"/> (counts and newest crop picture per camera).</summary>
    public IReadOnlyList<LatestRecordInfo> Baseline { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Baseline = await QueryAsync(cancellationToken);
        _counts = Baseline.ToDictionary(r => r.DeviceSn, r => r.EventCount);
        _logger.LogInformation("{Station} recording counts: {Counts}", _client.StationSn, string.Join(", ", _counts.Select(c => $"{c.Key}={c.Value}")));
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _client.Event -= OnStationEvent;
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }

    private void OnStationEvent(StationEvent e)
    {
        switch (e)
        {
            case LightSwitchEvent light when _cameras.TryGetValue(light.Channel, out var deviceSn):
                var last = _lastMotion.GetValueOrDefault(light.Channel);
                _lastMotion[light.Channel] = light.Time;
                _awaiting[deviceSn] = light.Time + _options.RecordingTimeout;
                _wake.Writer.TryWrite(true);
                if (light.Time - last >= _options.MotionCooldown)
                    Event?.Invoke(new DetectionEvent(_client.StationSn, light.Time, deviceSn, light.Channel, DetectionKind.Motion, -1, null,
                        JsonSerializer.Serialize(light), DetectionSource.LightSignal, light.Time));
                break;

            case StorageInfoEvent:
                _wake.Writer.TryWrite(true); // a clip was just written
                break;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Read the counts every CheckInterval while recordings are awaited, otherwise on a wake or poll.
            await WaitAsync(_awaiting.IsEmpty ? _options.PollInterval : _options.CheckInterval, ct);
            await CheckAsync(ct);

            var now = DateTimeOffset.Now;
            foreach (var (deviceSn, until) in _awaiting)
            {
                // Normal: the camera does not record every PIR trigger (it filters short ones, e.g. walking by quickly).
                if (until < now && _awaiting.TryRemove(KeyValuePair.Create(deviceSn, until)))
                    _logger.LogDebug("{Station}: no new recording from {Device} within {Timeout}", _client.StationSn, deviceSn, _options.RecordingTimeout);
            }
        }
    }

    /// <summary>True when woken, false when the time passed. A null time waits for a wake only.</summary>
    private async Task<bool> WaitAsync(TimeSpan? time, CancellationToken ct)
    {
        if (time is not { } delay)
            return await _wake.Reader.ReadAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try
        {
            return await _wake.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Raises <see cref="RecordingSavedEvent"/> for each camera whose count went up.</summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        IReadOnlyList<LatestRecordInfo> records;
        try
        {
            records = await QueryAsync(ct);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException or P2PCommandTimeoutException)
        {
            _logger.LogWarning("{Station} recording count check failed: {Message}", _client.StationSn, ex.Message);
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var record in records)
        {
            var known = _counts.TryGetValue(record.DeviceSn, out var previous);
            _counts[record.DeviceSn] = record.EventCount;
            // The station has its own entry (alarm records?); only cameras record clips.
            if (!known || record.EventCount <= previous || record.DeviceSn == _client.StationSn)
                continue;

            _awaiting.TryRemove(record.DeviceSn, out _);
            var crop = record.ParseCropName();
            var channel = _cameras.FirstOrDefault(c => c.Value == record.DeviceSn) is { Value: not null } camera ? camera.Key : crop?.Channel ?? -1;
            var fresh = crop is { } c && now - c.Time <= _options.CropMaxAge;
            Event?.Invoke(new RecordingSavedEvent(_client.StationSn, now, record.DeviceSn, channel, record.EventCount,
                fresh ? crop!.Value.Time : null, fresh ? record.CropPath : null));
        }
    }

    private async Task<IReadOnlyList<LatestRecordInfo>> QueryAsync(CancellationToken ct) =>
        LatestRecordInfo.Parse(await _client.QueryLatestRecordInfoAsync(ct));
}
