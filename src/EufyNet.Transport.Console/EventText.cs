using EufyNet.P2P;

namespace EufyNet.Transport.Console;

/// <summary>One-line, human-readable text for a station event (the device is logged next to it).</summary>
internal static class EventText
{
    public static string Describe(StationEvent e) => e switch
    {
        DetectionEvent d => $"{d.Kind.ToString().ToLowerInvariant()} detected{(d.PersonName is { } person ? $" ({person})" : "")}"
            + $"{(d.EventTime is { } at ? $" at {at:HH:mm:ss}" : "")} [{d.Source}{(d.EventType >= 0 ? $", event_type {d.EventType}" : "")}]",
        RecordingSavedEvent r => $"recording saved (#{r.EventCount}){(r.CropTime is { } at ? $", crop at {at:HH:mm:ss}" : "")}",
        CameraImageEvent i => $"picture {i.File} ({i.Jpeg.Length / 1024} KB{(i.TakenAt is { } at ? $", taken {at:yyyy-MM-dd HH:mm:ss}" : "")})",
        CameraSirenEvent s => s.On ? $"siren on for {s.Seconds}s ({s.Reason})" : "siren off",
        LightSwitchEvent l => $"light/IR {(l.On ? "on" : "off")}",
        StorageInfoEvent s => $"storage: {s.EmmcFreeMb} of {s.EmmcTotalMb} MB free{(s.HddExists ? ", HDD present" : "")}",
        AlarmModeChangedEvent m => $"alarm mode {m.Mode}",
        AlarmArmedEvent => "alarm armed",
        AlarmDelayEvent a => $"alarm in {a.DelaySeconds}s ({a.Reason})",
        AlarmTriggeredEvent a => $"ALARM ({a.Reason})",
        SensorStatusEvent s => $"sensor on channel {s.Channel} {(s.Open ? "open" : "closed")}",
        StorageStatusEvent s => $"storage on channel {s.Channel}: {s.Status}",
        HubNotifyUpdateEvent => "hub update",
        UnknownEvent u => $"unhandled {u.Command}{(u.JsonCmd is { } cmd ? $" cmd {cmd}" : "")} channel {u.Channel}: {Truncate(u.Payload, 300)}",
        _ => e.ToString(),
    };

    public static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
