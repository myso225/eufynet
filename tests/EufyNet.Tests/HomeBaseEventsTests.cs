using System.Text;
using EufyNet.P2P;

namespace EufyNet.Tests;

public class HomeBaseEventsTests
{
    private const string Sn = "T8010P0000000001";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Alarm_mode() =>
        Assert.Equal(new AlarmModeChangedEvent(Sn, Now, GuardMode.Home), Parse(CommandType.CmdGetAlarmMode, [1]));

    [Fact]
    public void Delay_alarm_with_event_zero_means_armed() =>
        Assert.IsType<AlarmArmedEvent>(Parse(CommandType.CmdGetDelayAlarm, [0, 0, 0, 0, 30, 0, 0, 0]));

    [Fact]
    public void Delay_alarm_carries_reason_and_seconds() =>
        Assert.Equal(new AlarmDelayEvent(Sn, Now, AlarmEvent.Door, 30), Parse(CommandType.CmdGetDelayAlarm, [6, 0, 0, 0, 30, 0, 0, 0]));

    [Fact]
    public void Tone_file_is_the_alarm_going_off() =>
        Assert.Equal(new AlarmTriggeredEvent(Sn, Now, AlarmEvent.CameraPir), Parse(CommandType.CmdSetToneFile, [7, 0, 0, 0]));

    [Fact]
    public void Storage_status_per_channel() =>
        Assert.Equal(new StorageStatusEvent(Sn, Now, 255, StorageStatus.Removed), Parse(CommandType.CmdGetTfcardStatus, [4, 0, 0, 0]));

    [Fact]
    public void Push_notify_with_inner_json_string_is_a_detection()
    {
        const string inner = """{"device_sn":"T8113P0000000001","event_type":3102,"msg_type":18,"person_name":"Jano"}""";
        var json = $$"""{"cmd":2037,"payload":{{System.Text.Json.JsonSerializer.Serialize(inner)}}}""";

        var e = Assert.IsType<DetectionEvent>(Parse(CommandType.CmdNotifyPayload, Nul(json), channel: 2));
        Assert.Equal("T8113P0000000001", e.DeviceSn);
        Assert.Equal(DetectionKind.Person, e.Kind);
        Assert.Equal(3102, e.EventType);
        Assert.Equal("Jano", e.PersonName);
        Assert.Equal(2, e.Channel);
    }

    [Theory]
    [InlineData("""{"device_sn":"X","event_type":1}""", DetectionKind.Motion)]
    [InlineData("""{"device_sn":"X","event_type":"1","fetch_id":5}""", DetectionKind.Person)]
    [InlineData("""{"device_sn":"X","event_type":3103}""", DetectionKind.Doorbell)]
    [InlineData("""{"device_sn":"X","event_type":3107}""", DetectionKind.Vehicle)]
    [InlineData("""{"device_sn":"X","event_type":99}""", DetectionKind.Other)]
    public void Push_notify_kinds(string push, DetectionKind kind)
    {
        var e = Assert.IsType<DetectionEvent>(Parse(CommandType.CmdNotifyPayload, Nul($$"""{"cmd":2037,"payload":{{push}}}""")));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public void Entry_sensor_status() =>
        Assert.Equal(new SensorStatusEvent(Sn, Now, 3, true),
            Parse(CommandType.CmdNotifyPayload, Nul("""{"cmd":1550,"payload":{"status":1}}"""), channel: 3));

    [Fact]
    public void Camera_siren_on_and_off_seen_live_in_night_mode()
    {
        Assert.Equal(new CameraSirenEvent(Sn, Now, 1, true, AlarmEvent.CameraPir, 10),
            Parse(CommandType.CmdSetDevsToneFile, Convert.FromHexString("070000000a0000000000000000000000"), channel: 1));
        Assert.Equal(new CameraSirenEvent(Sn, Now, 1, false, AlarmEvent.HubStop, 0),
            Parse(CommandType.CmdSetDevsToneFile, new byte[16], channel: 1));
    }

    [Fact]
    public void Light_switch_from_the_station() =>
        Assert.Equal(new LightSwitchEvent(Sn, Now, 0, false), Parse(CommandType.CmdSetFloodlightManualSwitch, new byte[16], channel: 0));

    [Fact]
    public void Storage_info_seen_live_on_hb2() =>
        Assert.Equal(new StorageInfoEvent(Sn, Now, 0, 14675, 1636, false), Parse(CommandType.CmdNotifyPayload,
            Nul("""{"cmd":55000,"hdd_exist":0,"payload":{"emmc_total":14675,"emmc_free":1636},"emmc_status":0}"""), channel: 0));

    [Fact]
    public void Hub_notify_update() =>
        Assert.IsType<HubNotifyUpdateEvent>(Parse(CommandType.CmdNotifyPayload, Nul("""{"cmd":1829,"payload":{}}""")));

    [Fact]
    public void Unknown_notify_payload_keeps_the_inner_cmd_and_text()
    {
        var e = Assert.IsType<UnknownEvent>(Parse(CommandType.CmdNotifyPayload, Nul("""{"cmd":1224,"payload":{"mode":1}}""")));
        Assert.Equal(1224, e.JsonCmd);
        Assert.Equal("""{"cmd":1224,"payload":{"mode":1}}""", e.Payload);
    }

    [Fact]
    public void Unknown_binary_command_is_hex()
    {
        var e = Assert.IsType<UnknownEvent>(Parse((CommandType)4321, [1, 2, 0xff]));
        Assert.Null(e.JsonCmd);
        Assert.Equal("0102ff", e.Payload);
    }

    [Fact]
    public void Replies_are_not_events()
    {
        Assert.Null(Parse(CommandType.CmdGatewayinfo, [1, 2, 3, 4]));
        Assert.Null(Parse(CommandType.CmdCameraInfo, Nul("{}")));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'{', (byte)'}', 0, 0 }, "{}")]
    [InlineData(new byte[] { (byte)'o', (byte)'k', 0, 1, 0, 0, 0 }, "ok")]
    [InlineData(new byte[] { (byte)'a', 0, (byte)'b' }, "610062")]
    [InlineData(new byte[] { 0, 0 }, "0000")]
    [InlineData(new byte[] { (byte)'a', 1 }, "6101")]
    public void Payload_text(byte[] data, string expected) => Assert.Equal(expected, HomeBaseEventParser.PayloadText(data));

    private static StationEvent? Parse(CommandType command, byte[] data, int channel = 255) =>
        HomeBaseEventParser.Parse(Sn, new P2PMessage(P2PDataType.Control, command, channel, 0, 0, data), Now);

    private static byte[] Nul(string text) => [.. Encoding.UTF8.GetBytes(text), 0, 0, 0];
}
