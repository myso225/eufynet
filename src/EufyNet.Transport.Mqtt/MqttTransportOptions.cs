using EufyNet.P2P;

namespace EufyNet.Transport.Mqtt;

/// <summary>Bound from the <c>Mqtt</c> configuration section. The transport is on when <see cref="Host"/> is set.</summary>
public sealed class MqttTransportOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "eufynet";

    /// <summary>Prefix of every topic of this bridge.</summary>
    public string BaseTopic { get; set; } = "eufynet";

    /// <summary>Home Assistant's MQTT discovery prefix; empty = no discovery.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>
    /// Guard mode → label for the Home Assistant select, e.g. <c>Custom1 = Night</c>. Only these modes are offered; empty = the
    /// HomeBase 2 list with the app's names. A mode the station reports that is not listed shows under its own name.
    /// </summary>
    public Dictionary<string, string> GuardModes { get; set; } = [];

    /// <summary>The motion binary sensor turns off this long after the last motion (Home Assistant <c>off_delay</c>).</summary>
    public TimeSpan MotionOffDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait between connection attempts to the broker.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>HomeBase 2 modes (http/types.js StationGuardModeProperty), in the app's order.</summary>
    internal static readonly IReadOnlyList<(GuardMode Mode, string Label)> DefaultGuardModes =
    [
        (GuardMode.Away, "Away"),
        (GuardMode.Home, "Home"),
        (GuardMode.Schedule, "Schedule"),
        (GuardMode.Custom1, "Custom 1"),
        (GuardMode.Custom2, "Custom 2"),
        (GuardMode.Custom3, "Custom 3"),
        (GuardMode.Geo, "Geofencing"),
        (GuardMode.Disarmed, "Disarmed"),
    ];
}

/// <summary>Guard mode ↔ label, from <see cref="MqttTransportOptions.GuardModes"/>.</summary>
public sealed class GuardModeLabels
{
    private readonly List<(GuardMode Mode, string Label)> _modes;

    public GuardModeLabels(IReadOnlyDictionary<string, string> configured)
    {
        _modes = configured.Count == 0
            ? [.. MqttTransportOptions.DefaultGuardModes]
            : configured
                .Select(c => Enum.TryParse<GuardMode>(c.Key, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                    ? (mode, string.IsNullOrWhiteSpace(c.Value) ? mode.ToString() : c.Value.Trim())
                    : throw new ArgumentException($"Mqtt:GuardModes: unknown guard mode {c.Key}; one of {string.Join(", ", Enum.GetNames<GuardMode>())}"))
                .OrderBy(m => (int)m.Item1)
                .ToList();
    }

    /// <summary>The select's options, in guard mode order.</summary>
    public IReadOnlyList<string> Options => _modes.Select(m => m.Label).ToList();

    public string Label(GuardMode mode) => _modes.FirstOrDefault(m => m.Mode == mode).Label ?? mode.ToString();

    /// <summary>A label, or a mode name (Away, custom1 …).</summary>
    public GuardMode? Parse(string text)
    {
        text = text.Trim();
        foreach (var (mode, label) in _modes)
        {
            if (string.Equals(label, text, StringComparison.OrdinalIgnoreCase))
                return mode;
        }
        return Enum.TryParse<GuardMode>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
    }
}
