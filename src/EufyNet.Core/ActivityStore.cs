using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.Core;

/// <summary>What the bridge knows about a camera beyond the station's own state.</summary>
public sealed class CameraActivity
{
    public DateTimeOffset? LastMotion { get; set; }
    public DateTimeOffset? LastRecording { get; set; }
    /// <summary>Motion times within the window (pruned by <see cref="ActivityStore.Prune"/>).</summary>
    public List<DateTimeOffset> Motions { get; set; } = [];
    public List<DateTimeOffset> Recordings { get; set; } = [];
    public int? Battery { get; set; }
    public DateTimeOffset? BatteryUpdated { get; set; }
    /// <summary>The last downloaded picture: its file on the station and when it was taken.</summary>
    public string? ImageFile { get; set; }
    public DateTimeOffset? ImageTakenAt { get; set; }

    public CameraActivity Copy() => new()
    {
        LastMotion = LastMotion,
        LastRecording = LastRecording,
        Motions = [.. Motions],
        Recordings = [.. Recordings],
        Battery = Battery,
        BatteryUpdated = BatteryUpdated,
        ImageFile = ImageFile,
        ImageTakenAt = ImageTakenAt,
    };
}

/// <summary>
/// Per-camera activity for all stations, kept in <c>activity.json</c> so the counters and times survive a restart.
/// Thread-safe; every change is saved right away (a few hundred bytes).
/// </summary>
public sealed class ActivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, CameraActivity> _cameras;
    private readonly Lock _lock = new();

    /// <param name="path">Null = memory only (tests, list-only).</param>
    public ActivityStore(string? path, TimeSpan window, ILogger? logger = null)
    {
        _path = path;
        Window = window;
        _logger = logger ?? NullLogger.Instance;
        _cameras = Load();
    }

    /// <summary>How far back <see cref="CameraActivity.Motions"/> and <see cref="CameraActivity.Recordings"/> reach.</summary>
    public TimeSpan Window { get; }

    public CameraActivity Get(string deviceSn)
    {
        lock (_lock)
            return _cameras.TryGetValue(deviceSn, out var activity) ? activity.Copy() : new CameraActivity();
    }

    public void AddMotion(string deviceSn, DateTimeOffset time) => Update(deviceSn, a =>
    {
        a.LastMotion = Max(a.LastMotion, time);
        a.Motions.Add(time);
        return true;
    });

    public void AddRecording(string deviceSn, DateTimeOffset time) => Update(deviceSn, a =>
    {
        a.LastRecording = Max(a.LastRecording, time);
        a.Recordings.Add(time);
        return true;
    });

    /// <summary>False when the value is the same (only the time would change).</summary>
    public bool SetBattery(string deviceSn, int percent, DateTimeOffset? updated) => Update(deviceSn, a =>
    {
        var changed = a.Battery != percent;
        a.Battery = percent;
        a.BatteryUpdated = updated ?? a.BatteryUpdated;
        return changed;
    });

    public void SetImage(string deviceSn, string file, DateTimeOffset? takenAt) => Update(deviceSn, a =>
    {
        a.ImageFile = file;
        a.ImageTakenAt = takenAt;
        return true;
    });

    /// <summary>Drops times older than the window. True when a count changed.</summary>
    public bool Prune(DateTimeOffset now)
    {
        var from = now - Window;
        var changed = false;
        lock (_lock)
        {
            foreach (var a in _cameras.Values)
                changed |= a.Motions.RemoveAll(t => t <= from) + a.Recordings.RemoveAll(t => t <= from) > 0;
            if (changed)
                Save();
        }
        return changed;
    }

    private bool Update(string deviceSn, Func<CameraActivity, bool> change)
    {
        lock (_lock)
        {
            if (!_cameras.TryGetValue(deviceSn, out var activity))
                _cameras[deviceSn] = activity = new CameraActivity();
            var changed = change(activity);
            Save();
            return changed;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    private Dictionary<string, CameraActivity> Load()
    {
        if (_path is null || !File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CameraActivity>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning("Ignoring {Path}: {Message}", _path, ex.Message);
            return [];
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_cameras, JsonOptions));
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Saving {Path} failed: {Message}", _path, ex.Message);
        }
    }
}
