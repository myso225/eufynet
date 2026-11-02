using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace EufyNet.P2P;

/// <summary>Why the alarm went off or stopped (p2p/types.js AlarmEvent).</summary>
public enum AlarmEvent
{
    HubStop = 0,
    DevStop = 1,
    GSensor = 2,
    Pir = 3,
    App = 4,
    Hot = 5,
    Door = 6,
    CameraPir = 7,
    MotionSensor = 8,
    CameraGSensor = 9,
    CameraApp = 10,
    CameraLinkage = 11,
    HubKeypad = 13,
    HubStopByKeypad = 15,
    HubStopByApp = 16,
    HubStopByHand = 17,
    AppLight = 22,
    AppLightSound = 23,
    MotionAppLight = 24,
    MotionAppLightAlarm = 25,
}

/// <summary>Storage card state (p2p/types.js TFCardStatus).</summary>
public enum StorageStatus
{
    Normal = 0,
    NonOriginal = 1,
    MountFail = 2,
    FormatFail = 3,
    Removed = 4,
    Formatting = 5,
    Busy = 6,
}

public enum DetectionKind
{
    Motion,
    Person,
    Doorbell,
    Crying,
    Sound,
    Pet,
    Vehicle,
    Other,
}

/// <summary>Something the HomeBase reported on its own. Transport-neutral, so MQTT can publish it as is.</summary>
public abstract record StationEvent(string StationSn, DateTimeOffset Time);

public sealed record AlarmModeChangedEvent(string StationSn, DateTimeOffset Time, GuardMode Mode) : StationEvent(StationSn, Time);

public sealed record AlarmArmedEvent(string StationSn, DateTimeOffset Time) : StationEvent(StationSn, Time);

public sealed record AlarmDelayEvent(string StationSn, DateTimeOffset Time, AlarmEvent Reason, int DelaySeconds) : StationEvent(StationSn, Time);

public sealed record AlarmTriggeredEvent(string StationSn, DateTimeOffset Time, AlarmEvent Reason) : StationEvent(StationSn, Time);

public enum DetectionSource
{
    /// <summary>CMD_CAMERA_PUSH_NOTIFY over P2P (HomeBase 3).</summary>
    PushNotify,
    /// <summary>The light/IR switch message the station sends when a camera starts recording (<see cref="LocalMotionDetector"/>).</summary>
    LightSignal,
}

/// <summary>
/// Motion / person / … from a camera. <paramref name="EventType"/> is Eufy's raw number (-1 when unknown), <paramref name="Raw"/>
/// the JSON. <paramref name="EventTime"/> is when the camera saw it, when known (from the crop picture name for local records).
/// </summary>
public sealed record DetectionEvent(string StationSn, DateTimeOffset Time, string? DeviceSn, int Channel, DetectionKind Kind,
    int EventType, string? PersonName, string Raw, DetectionSource Source = DetectionSource.PushNotify, DateTimeOffset? EventTime = null)
    : StationEvent(StationSn, Time);

/// <summary>
/// CMD_SET_FLOODLIGHT_MANUAL_SWITCH sent by the station. On a HomeBase 2 with eufyCam 2C (no spotlight) it arrives right as a
/// camera starts recording, so <see cref="LocalMotionDetector"/> uses it as a trigger.
/// </summary>
public sealed record LightSwitchEvent(string StationSn, DateTimeOffset Time, int Channel, bool On) : StationEvent(StationSn, Time);

/// <summary>
/// CMD_SET_DEVS_TONE_FILE sent by the station: a camera's siren started (reason, e.g. <see cref="AlarmEvent.CameraPir"/>, and
/// duration) or stopped (all zero). Seen on HomeBase 2 when an armed mode has the camera alarm enabled.
/// </summary>
public sealed record CameraSirenEvent(string StationSn, DateTimeOffset Time, int Channel, bool On, AlarmEvent Reason, int Seconds)
    : StationEvent(StationSn, Time);

/// <summary>
/// The station's recording count for a camera went up (<see cref="LocalMotionDetector"/>). <paramref name="CropTime"/> and
/// <paramref name="CropPath"/> are the newest crop picture's when it is recent; the station does not make one for every recording.
/// </summary>
public sealed record RecordingSavedEvent(string StationSn, DateTimeOffset Time, string DeviceSn, int Channel, int EventCount, DateTimeOffset? CropTime,
    string? CropPath = null)
    : StationEvent(StationSn, Time);

/// <summary>
/// A camera's newest crop picture changed after its <see cref="RecordingSavedEvent"/> (<see cref="LocalMotionDetector"/>): the station
/// writes it later sometimes (seen with the camera siren on).
/// </summary>
public sealed record PictureSavedEvent(string StationSn, DateTimeOffset Time, string DeviceSn, int Channel, DateTimeOffset CropTime, string CropPath)
    : StationEvent(StationSn, Time);

/// <summary>A camera's preview picture downloaded from the station (<see cref="HomeBaseClient.DownloadImageAsync"/>).</summary>
public sealed record CameraImageEvent(string StationSn, DateTimeOffset Time, string DeviceSn, int Channel, DateTimeOffset? TakenAt, string File, byte[] Jpeg)
    : StationEvent(StationSn, Time);

/// <summary>CMD_NOTIFY_PAYLOAD cmd 55000: the station's storage after a recording was written (MB).</summary>
public sealed record StorageInfoEvent(string StationSn, DateTimeOffset Time, int Channel, int? EmmcTotalMb, int? EmmcFreeMb, bool HddExists)
    : StationEvent(StationSn, Time);

public sealed record SensorStatusEvent(string StationSn, DateTimeOffset Time, int Channel, bool Open) : StationEvent(StationSn, Time);

public sealed record StorageStatusEvent(string StationSn, DateTimeOffset Time, int Channel, StorageStatus Status) : StationEvent(StationSn, Time);

public sealed record HubNotifyUpdateEvent(string StationSn, DateTimeOffset Time) : StationEvent(StationSn, Time);

/// <summary>A control message no parser handles yet. <paramref name="JsonCmd"/> is the inner cmd of CMD_NOTIFY_PAYLOAD.</summary>
public sealed record UnknownEvent(string StationSn, DateTimeOffset Time, CommandType Command, int Channel, int? JsonCmd, string Payload)
    : StationEvent(StationSn, Time);

/// <summary>Turns CONTROL messages into <see cref="StationEvent"/>s. Port of the event part of handleDataControl in p2p/session.js.</summary>
public static class HomeBaseEventParser
{
    /// <summary>CusPushEvent.SECURITY: the classic HomeBase 2 motion / person push.</summary>
    private const int SecurityPushEvent = 1;

    /// <summary>Storage report after a recording, seen live on HomeBase 2 (not in eufy-security-client).</summary>
    private const int StorageInfoCommand = 55000;

    /// <summary>Returns null for messages that are replies rather than events (gateway info, camera info, ping).</summary>
    public static StationEvent? Parse(string stationSn, P2PMessage message, DateTimeOffset time)
    {
        var data = message.Data;
        switch (message.CommandId)
        {
            case CommandType.CmdGatewayinfo or CommandType.CmdCameraInfo or CommandType.CmdDatabase or CommandType.CmdDatabaseImage or CommandType.CmdPing:
                return null;

            case CommandType.CmdGetAlarmMode when data.Length >= 1:
                return new AlarmModeChangedEvent(stationSn, time, (GuardMode)data[0]);

            case CommandType.CmdGetDelayAlarm when data.Length >= 8:
                // Arming also sends it, with event 0.
                var alarmEvent = BinaryPrimitives.ReadInt32LittleEndian(data);
                return alarmEvent == 0
                    ? new AlarmArmedEvent(stationSn, time)
                    : new AlarmDelayEvent(stationSn, time, (AlarmEvent)alarmEvent, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)));

            case CommandType.CmdSetToneFile when data.Length >= 4:
                return new AlarmTriggeredEvent(stationSn, time, (AlarmEvent)BinaryPrimitives.ReadInt32LittleEndian(data));

            case CommandType.CmdGetTfcardStatus when data.Length >= 4:
                return new StorageStatusEvent(stationSn, time, message.Channel, (StorageStatus)BinaryPrimitives.ReadInt32LittleEndian(data));

            case CommandType.CmdSetDevsToneFile when data.Length >= 8:
                var reason = BinaryPrimitives.ReadInt32LittleEndian(data);
                var seconds = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                return new CameraSirenEvent(stationSn, time, message.Channel, reason != 0 || seconds != 0, (AlarmEvent)reason, seconds);

            case CommandType.CmdSetFloodlightManualSwitch when data.Length >= 1:
                return new LightSwitchEvent(stationSn, time, message.Channel, data[0] == 1);

            case CommandType.CmdNotifyPayload:
                return ParseNotifyPayload(stationSn, message, time);

            default:
                return Unknown(stationSn, message, time, null);
        }
    }

    /// <summary>
    /// The NUL-terminated text at the start of the body when it is printable (JSON), otherwise hex. Like Node's
    /// getNullTerminatedString: the station puts binary fields after the NUL. Shared with the raw capture.
    /// </summary>
    public static string PayloadText(byte[] data)
    {
        // At least two printable bytes, no control bytes other than tab / CR / LF.
        var nul = Array.IndexOf(data, (byte)0);
        var text = data.AsSpan(0, nul < 0 ? data.Length : nul);
        var printable = text.Length > 1
            && !text.ContainsAnyInRange((byte)0x01, (byte)0x08) && !text.ContainsAnyInRange((byte)0x0b, (byte)0x0c)
            && !text.ContainsAnyInRange((byte)0x0e, (byte)0x1f);
        return printable ? Encoding.UTF8.GetString(text) : Convert.ToHexStringLower(data);
    }

    private static StationEvent ParseNotifyPayload(string stationSn, P2PMessage message, DateTimeOffset time)
    {
        var text = PayloadText(message.Data);
        JsonElement json;
        try
        {
            json = JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            return Unknown(stationSn, message, time, null);
        }
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("cmd", out var cmdElement) || !cmdElement.TryGetInt32(out var cmd))
            return Unknown(stationSn, message, time, null);
        json.TryGetProperty("payload", out var payload);

        switch ((CommandType)cmd)
        {
            case CommandType.CmdCameraPushNotify:
                // The inner notification is usually a JSON string inside the JSON.
                var inner = payload.ValueKind == JsonValueKind.String ? TryParse(payload.GetString()) : payload;
                if (inner is { ValueKind: JsonValueKind.Object } push)
                    return Detection(stationSn, message.Channel, time, push);
                break;

            case CommandType.CmdEntrySensorStatus when payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("status", out var status):
                return new SensorStatusEvent(stationSn, time, message.Channel, status.ToString() == "1");

            case CommandType.CmdHubNotifyUpdate:
                return new HubNotifyUpdateEvent(stationSn, time);

            case (CommandType)StorageInfoCommand:
                // Unlike the others, the fields sit next to cmd as well as in payload.
                return new StorageInfoEvent(stationSn, time, message.Channel,
                    payload.ValueKind == JsonValueKind.Object ? Int(payload, "emmc_total") : null,
                    payload.ValueKind == JsonValueKind.Object ? Int(payload, "emmc_free") : null,
                    Int(json, "hdd_exist") is > 0);
        }
        return Unknown(stationSn, message, time, cmd);
    }

    private static DetectionEvent Detection(string stationSn, int channel, DateTimeOffset time, JsonElement push)
    {
        var eventType = Int(push, "event_type") ?? -1;
        var personName = String(push, "person_name");
        var kind = eventType switch
        {
            SecurityPushEvent => push.TryGetProperty("fetch_id", out _) ? DetectionKind.Person : DetectionKind.Motion,
            3101 => DetectionKind.Motion,
            3102 or 3111 or 3112 => DetectionKind.Person,
            3103 => DetectionKind.Doorbell,
            3104 => DetectionKind.Crying,
            3105 => DetectionKind.Sound,
            3106 or 3108 or 3109 or 3110 => DetectionKind.Pet,
            3107 => DetectionKind.Vehicle,
            _ => DetectionKind.Other,
        };
        return new DetectionEvent(stationSn, time, String(push, "device_sn"), channel, kind, eventType,
            string.IsNullOrEmpty(personName) ? null : personName, push.GetRawText());
    }

    private static UnknownEvent Unknown(string stationSn, P2PMessage message, DateTimeOffset time, int? jsonCmd) =>
        new(stationSn, time, message.CommandId, message.Channel, jsonCmd, PayloadText(message.Data));

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Eufy sends numbers as numbers or strings, depending on firmware.</summary>
    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : null;
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
}
