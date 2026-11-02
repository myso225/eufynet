using System.Text.Json;
using System.Text.Json.Nodes;
using EufyNet.Core;
using EufyNet.P2P;

namespace EufyNet.Transport.Mqtt;

/// <summary>Topic names under <see cref="MqttTransportOptions.BaseTopic"/>.</summary>
public sealed class MqttTopics(string baseTopic)
{
    /// <summary><c>online</c> / <c>offline</c> (last will), retained.</summary>
    public string Status => $"{baseTopic}/status";

    /// <summary>Login state JSON, retained.</summary>
    public string Login => $"{baseTopic}/login";

    /// <summary>Command JSON in: <c>{"id":"1","name":"picture","station":null,"args":{"camera":"Front Door"}}</c>.</summary>
    public string Command => $"{baseTopic}/command";

    /// <summary>Command results out.</summary>
    public string Result => $"{baseTopic}/result";

    /// <summary>Station or camera state JSON, retained.</summary>
    public string State(string sn) => $"{baseTopic}/{sn}/state";

    /// <summary>Events of a station or camera, not retained (Node-RED).</summary>
    public string Event(string sn) => $"{baseTopic}/{sn}/event";

    /// <summary><c>ON</c> per motion, not retained; Home Assistant turns it off after <c>off_delay</c>.</summary>
    public string Motion(string deviceSn) => $"{baseTopic}/{deviceSn}/motion";

    /// <summary>The last picture as JPEG bytes, retained.</summary>
    public string Image(string deviceSn) => $"{baseTopic}/{deviceSn}/image";

    /// <summary>Guard mode label or name in.</summary>
    public string GuardModeSet(string stationSn) => $"{baseTopic}/{stationSn}/guard_mode/set";

    public string GuardModeSetFilter => $"{baseTopic}/+/guard_mode/set";
}

/// <summary>JSON payloads: state, events, results, and Home Assistant discovery configs.</summary>
public static class MqttMessages
{
    public static string StationState(StationState s, GuardModeLabels labels) => new JsonObject
    {
        ["name"] = s.Name,
        ["connected"] = s.Connected,
        ["guard_mode"] = s.GuardMode is { } g ? labels.Label(g) : null,
        ["guard_mode_code"] = s.GuardMode?.ToString(),
        ["alarm_mode"] = s.AlarmMode is { } a ? labels.Label(a) : null,
        ["alarm_mode_code"] = s.AlarmMode?.ToString(),
        ["encryption"] = s.Encryption?.ToString(),
    }.ToJsonString();

    public static string CameraState(CameraState c, StationState s) => new JsonObject
    {
        ["name"] = c.Name,
        ["station"] = s.StationSn,
        ["last_motion"] = Time(c.LastMotion),
        ["last_recording"] = Time(c.LastRecording),
        ["motions"] = c.Motions,
        ["recordings"] = c.Recordings,
        ["window_hours"] = s.ActivityWindow.TotalHours,
        ["battery"] = c.Battery,
        ["battery_updated"] = Time(c.BatteryUpdated),
        ["picture_taken"] = Time(c.ImageTakenAt),
    }.ToJsonString();

    public static string Login(LoginState login) => new JsonObject
    {
        ["status"] = login.Status?.ToString() ?? "LoggingIn",
        ["message"] = login.Message,
        ["needs_code"] = login.NeedsVerifyCode,
        ["needs_captcha"] = login.NeedsCaptcha,
        ["captcha_id"] = login.Captcha?.CaptchaId,
    }.ToJsonString();

    public static string Result(BridgeCommandResult r) => new JsonObject
    {
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["success"] = r.Success,
        ["message"] = r.Message,
        ["data"] = r.Data is { } data ? JsonNode.Parse(data.GetRawText()) : null,
    }.ToJsonString();

    public static string GuardModeChanged(StationState s, GuardModeLabels labels) => new JsonObject
    {
        ["type"] = "guard_mode",
        ["station"] = s.StationSn,
        ["time"] = Time(DateTimeOffset.Now),
        ["mode"] = s.GuardMode is { } g ? labels.Label(g) : null,
        ["mode_code"] = s.GuardMode?.ToString(),
    }.ToJsonString();

    /// <summary>An event for Node-RED: <c>type</c>, station, camera, time, and the event's own fields.</summary>
    public static string Event(BridgeEvent e, GuardModeLabels labels)
    {
        var ev = e.Event;
        var json = new JsonObject
        {
            ["type"] = EventType(ev),
            ["station"] = ev.StationSn,
            ["device"] = e.DeviceSn,
            ["name"] = e.DeviceName,
            ["time"] = Time(ev.Time),
        };
        switch (ev)
        {
            case DetectionEvent d:
                json["kind"] = d.Kind.ToString().ToLowerInvariant();
                json["source"] = d.Source.ToString();
                json["event_time"] = Time(d.EventTime);
                if (d.PersonName is not null)
                    json["person"] = d.PersonName;
                break;
            case RecordingSavedEvent r:
                json["event_count"] = r.EventCount;
                json["crop_time"] = Time(r.CropTime);
                break;
            case CameraImageEvent i:
                json["file"] = i.File;
                json["taken_at"] = Time(i.TakenAt);
                json["size"] = i.Jpeg.Length;
                break;
            case CameraSirenEvent s:
                json["on"] = s.On;
                json["reason"] = s.Reason.ToString();
                json["seconds"] = s.Seconds;
                break;
            case AlarmModeChangedEvent m:
                json["mode"] = labels.Label(m.Mode);
                json["mode_code"] = m.Mode.ToString();
                break;
            default:
                // Remaining records are small; their fields as they are.
                foreach (var (key, value) in JsonSerializer.SerializeToNode(ev, ev.GetType())!.AsObject().ToList())
                {
                    if (key is not ("StationSn" or "Time"))
                        json[ToSnake(key)] = value?.DeepClone();
                }
                break;
        }
        return json.ToJsonString();
    }

    private static string EventType(StationEvent e) => e switch
    {
        DetectionEvent => "motion",
        RecordingSavedEvent => "recording",
        CameraImageEvent => "picture",
        CameraSirenEvent => "siren",
        AlarmModeChangedEvent => "alarm_mode",
        AlarmArmedEvent => "alarm_armed",
        AlarmDelayEvent => "alarm_delay",
        AlarmTriggeredEvent => "alarm",
        LightSwitchEvent => "light",
        StorageInfoEvent => "storage",
        UnknownEvent => "unknown",
        _ => ToSnake(e.GetType().Name.Replace("Event", "")),
    };

    private static string? Time(DateTimeOffset? time) => time?.ToString("yyyy-MM-ddTHH:mm:sszzz");

    private static string ToSnake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    /// <summary>Home Assistant MQTT discovery: (topic, config JSON) for the station and each camera. All retained.</summary>
    public static IEnumerable<(string Topic, string Payload)> Discovery(StationState s, MqttTransportOptions options, MqttTopics topics,
        IReadOnlyList<string> guardModeOptions)
    {
        var prefix = options.DiscoveryPrefix;
        var station = new JsonObject
        {
            ["identifiers"] = new JsonArray(s.StationSn),
            ["name"] = s.Name,
            ["manufacturer"] = "Eufy",
            ["model"] = "HomeBase",
            ["serial_number"] = s.StationSn,
        };

        JsonObject Entity(JsonObject device, string sn, string key, string name, string stateTopic) => new()
        {
            ["name"] = name,
            ["unique_id"] = $"eufynet_{sn}_{key}",
            ["has_entity_name"] = true,
            ["device"] = device.DeepClone(),
            ["state_topic"] = stateTopic,
            ["availability_topic"] = topics.Status,
        };

        (string, string) Config(string component, string sn, string key, JsonObject config) =>
            ($"{prefix}/{component}/eufynet_{sn}_{key}/config", config.ToJsonString());

        var guard = Entity(station, s.StationSn, "guard_mode", "Guard mode", topics.State(s.StationSn));
        guard["value_template"] = "{{ value_json.guard_mode }}";
        guard["command_topic"] = topics.GuardModeSet(s.StationSn);
        guard["options"] = new JsonArray([.. guardModeOptions.Select(o => (JsonNode)o)]);
        guard["icon"] = "mdi:shield-home";
        yield return Config("select", s.StationSn, "guard_mode", guard);

        var active = Entity(station, s.StationSn, "alarm_mode", "Active mode", topics.State(s.StationSn));
        active["value_template"] = "{{ value_json.alarm_mode }}";
        active["icon"] = "mdi:shield-check";
        yield return Config("sensor", s.StationSn, "alarm_mode", active);

        var connected = Entity(station, s.StationSn, "connected", "Connected", topics.State(s.StationSn));
        connected["value_template"] = "{{ 'ON' if value_json.connected else 'OFF' }}";
        connected["device_class"] = "connectivity";
        connected["entity_category"] = "diagnostic";
        yield return Config("binary_sensor", s.StationSn, "connected", connected);

        var hours = $"{s.ActivityWindow.TotalHours:0.#} h";
        foreach (var c in s.Cameras)
        {
            var camera = new JsonObject
            {
                ["identifiers"] = new JsonArray(c.DeviceSn),
                ["name"] = c.Name,
                ["manufacturer"] = "Eufy",
                ["model"] = "Camera",
                ["serial_number"] = c.DeviceSn,
                ["via_device"] = s.StationSn,
            };
            var state = topics.State(c.DeviceSn);

            var motion = Entity(camera, c.DeviceSn, "motion", "Motion", topics.Motion(c.DeviceSn));
            motion["device_class"] = "motion";
            motion["off_delay"] = (int)options.MotionOffDelay.TotalSeconds;
            yield return Config("binary_sensor", c.DeviceSn, "motion", motion);

            foreach (var (key, name, field) in new[]
            {
                ("last_motion", "Last motion", "last_motion"),
                ("last_recording", "Last recording", "last_recording"),
                ("picture_taken", "Picture taken", "picture_taken"),
            })
            {
                var time = Entity(camera, c.DeviceSn, key, name, state);
                time["device_class"] = "timestamp";
                time["value_template"] = $"{{{{ value_json.{field} }}}}";
                yield return Config("sensor", c.DeviceSn, key, time);
            }

            foreach (var (key, name, icon) in new[]
            {
                ("motions", $"Motions ({hours})", "mdi:motion-sensor"),
                ("recordings", $"Recordings ({hours})", "mdi:record-rec"),
            })
            {
                var count = Entity(camera, c.DeviceSn, key, name, state);
                count["value_template"] = $"{{{{ value_json.{key} }}}}";
                count["state_class"] = "measurement";
                count["icon"] = icon;
                yield return Config("sensor", c.DeviceSn, key, count);
            }

            var battery = Entity(camera, c.DeviceSn, "battery", "Battery", state);
            battery["value_template"] = "{{ value_json.battery }}";
            battery["device_class"] = "battery";
            battery["unit_of_measurement"] = "%";
            battery["state_class"] = "measurement";
            yield return Config("sensor", c.DeviceSn, "battery", battery);

            var image = Entity(camera, c.DeviceSn, "picture", "Last picture", state);
            image.Remove("state_topic");
            image["image_topic"] = topics.Image(c.DeviceSn);
            image["content_type"] = "image/jpeg";
            yield return Config("image", c.DeviceSn, "picture", image);
        }
    }
}
