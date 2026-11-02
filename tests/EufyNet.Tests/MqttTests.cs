using System.Buffers;
using System.Text.Json;
using EufyNet.Core;
using EufyNet.P2P;
using EufyNet.Transport.Mqtt;
using MQTTnet;

namespace EufyNet.Tests;

public class ActivityStoreTests
{
    private const string Camera = "T8140P0000000A01";

    [Fact]
    public void Counts_stay_within_the_window_and_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eufynet-activity-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.Now;
            var store = new ActivityStore(path, TimeSpan.FromHours(8));
            store.AddMotion(Camera, now.AddHours(-9));
            store.AddMotion(Camera, now.AddMinutes(-5));
            store.AddRecording(Camera, now.AddMinutes(-4));

            Assert.True(store.Prune(now));
            Assert.False(store.Prune(now));

            var reloaded = new ActivityStore(path, TimeSpan.FromHours(8)).Get(Camera);
            Assert.Single(reloaded.Motions);
            Assert.Single(reloaded.Recordings);
            Assert.Equal(now.AddMinutes(-5), reloaded.LastMotion);
            Assert.Equal(now.AddMinutes(-4), reloaded.LastRecording);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Battery_change_is_reported_once()
    {
        var store = new ActivityStore(null, TimeSpan.FromHours(8));
        Assert.True(store.SetBattery(Camera, 56, DateTimeOffset.Now));
        Assert.False(store.SetBattery(Camera, 56, DateTimeOffset.Now));
        Assert.True(store.SetBattery(Camera, 55, null));
        Assert.Equal(55, store.Get(Camera).Battery);
    }
}

public class MqttTests
{
    private const string StationSn = "T8010P0000000001";
    private const string FrontDoor = "T8140P0000000A01";

    private static readonly StationState State = new(StationSn, "HomeBase", true, GuardMode.Custom1, GuardMode.Home, EncryptionLevel.Level1,
        [new CameraState(FrontDoor, "Front Door", 0, DateTimeOffset.Now, null, 3, 2, 56, DateTimeOffset.Now, null)], TimeSpan.FromHours(8));

    [Fact]
    public void Default_guard_modes_are_the_homebase_2_list()
    {
        var labels = new GuardModeLabels(new Dictionary<string, string>());
        Assert.Equal(["Away", "Home", "Schedule", "Custom 1", "Custom 2", "Custom 3", "Geofencing", "Disarmed"], labels.Options);
        Assert.Equal(GuardMode.Geo, labels.Parse("geofencing"));
    }

    [Fact]
    public void Configured_guard_modes_keep_mode_order_and_map_both_ways()
    {
        var labels = new GuardModeLabels(new Dictionary<string, string> { ["Disarmed"] = "Off", ["Custom1"] = "Night", ["Away"] = "Away" });

        Assert.Equal(["Away", "Night", "Off"], labels.Options);
        Assert.Equal(GuardMode.Custom1, labels.Parse("night"));
        Assert.Equal(GuardMode.Custom2, labels.Parse("Custom2")); // a mode name works too
        Assert.Null(labels.Parse("Party"));
        Assert.Equal("Night", labels.Label(GuardMode.Custom1));
        Assert.Equal("Schedule", labels.Label(GuardMode.Schedule)); // not configured: its own name
    }

    [Fact]
    public void Unknown_configured_guard_mode_is_an_error() =>
        Assert.Throws<ArgumentException>(() => new GuardModeLabels(new Dictionary<string, string> { ["Night"] = "Night" }));

    [Fact]
    public void Discovery_has_the_select_and_the_camera_entities()
    {
        var options = new MqttTransportOptions();
        var configs = MqttMessages.Discovery(State, options, new MqttTopics("eufynet"), ["Away", "Night"])
            .ToDictionary(c => c.Topic, c => JsonDocument.Parse(c.Payload).RootElement);

        var select = configs[$"homeassistant/select/eufynet_{StationSn}_guard_mode/config"];
        Assert.Equal($"eufynet/{StationSn}/guard_mode/set", select.GetProperty("command_topic").GetString());
        Assert.Equal(["Away", "Night"], select.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("eufynet/status", select.GetProperty("availability_topic").GetString());

        var image = configs[$"homeassistant/image/eufynet_{FrontDoor}_picture/config"];
        Assert.Equal($"eufynet/{FrontDoor}/image", image.GetProperty("image_topic").GetString());
        Assert.False(image.TryGetProperty("state_topic", out _));
        Assert.Equal(StationSn, image.GetProperty("device").GetProperty("via_device").GetString());

        var motion = configs[$"homeassistant/binary_sensor/eufynet_{FrontDoor}_motion/config"];
        Assert.Equal(30, motion.GetProperty("off_delay").GetInt32());
        Assert.Equal("Motions (8 h)", configs[$"homeassistant/sensor/eufynet_{FrontDoor}_motions/config"].GetProperty("name").GetString());
        Assert.Equal("battery", configs[$"homeassistant/sensor/eufynet_{FrontDoor}_battery/config"].GetProperty("device_class").GetString());
        Assert.Contains($"homeassistant/sensor/eufynet_{FrontDoor}_last_recording/config", configs.Keys);
    }

    [Fact]
    public void State_json_uses_labels_and_null_for_unknown_times()
    {
        var labels = new GuardModeLabels(new Dictionary<string, string> { ["Custom1"] = "Night", ["Home"] = "Home" });

        var station = JsonDocument.Parse(MqttMessages.StationState(State, labels)).RootElement;
        Assert.Equal("Night", station.GetProperty("guard_mode").GetString());
        Assert.Equal("Custom1", station.GetProperty("guard_mode_code").GetString());
        Assert.Equal("Home", station.GetProperty("alarm_mode").GetString());

        var camera = JsonDocument.Parse(MqttMessages.CameraState(State.Cameras[0], State)).RootElement;
        Assert.Equal(3, camera.GetProperty("motions").GetInt32());
        Assert.Equal(2, camera.GetProperty("recordings").GetInt32());
        Assert.Equal(56, camera.GetProperty("battery").GetInt32());
        Assert.Equal(JsonValueKind.Null, camera.GetProperty("last_recording").ValueKind);
        Assert.True(DateTimeOffset.TryParse(camera.GetProperty("last_motion").GetString(), out _));
    }

    [Fact]
    public void Event_json_for_motion_and_picture()
    {
        var labels = new GuardModeLabels(new Dictionary<string, string>());
        var time = DateTimeOffset.Now;
        var motion = JsonDocument.Parse(MqttMessages.Event(new BridgeEvent(
            new DetectionEvent(StationSn, time, FrontDoor, 0, DetectionKind.Motion, -1, null, "{}", DetectionSource.LightSignal, time),
            FrontDoor, "Front Door"), labels)).RootElement;
        Assert.Equal("motion", motion.GetProperty("type").GetString());
        Assert.Equal("Front Door", motion.GetProperty("name").GetString());
        Assert.Equal("LightSignal", motion.GetProperty("source").GetString());

        var picture = JsonDocument.Parse(MqttMessages.Event(new BridgeEvent(
            new CameraImageEvent(StationSn, time, FrontDoor, 0, time, "/media/x.jpg", new byte[1234]), FrontDoor, "Front Door"), labels)).RootElement;
        Assert.Equal("picture", picture.GetProperty("type").GetString());
        Assert.Equal(1234, picture.GetProperty("size").GetInt32());

        var storage = JsonDocument.Parse(MqttMessages.Event(new BridgeEvent(
            new StorageInfoEvent(StationSn, time, 0, 14675, 1636, false), FrontDoor, "Front Door"), labels)).RootElement;
        Assert.Equal("storage", storage.GetProperty("type").GetString());
        Assert.Equal(1636, storage.GetProperty("emmc_free_mb").GetInt32());
    }

    [Fact]
    public async Task Messages_become_commands()
    {
        await using var transport = new MqttTransport(new MqttTransportOptions
        {
            GuardModes = new Dictionary<string, string> { ["Custom1"] = "Night", ["Home"] = "Home" },
        });

        transport.OnMessage($"eufynet/{StationSn}/guard_mode/set", "Night");
        transport.OnMessage("eufynet/command", """{"id":"7","name":"picture","args":{"camera":"Front Door"}}""");
        transport.OnMessage("eufynet/command", """{"name":"set_guard_mode","station":"T8010P0000000001","args":{"mode":"night"}}""");
        transport.OnMessage($"eufynet/{StationSn}/guard_mode/set", "Party");
        transport.OnMessage("eufynet/command", "not json");

        Assert.True(transport.PendingCommands.TryRead(out var guard));
        Assert.Equal((BridgeCommands.SetGuardMode, StationSn, "Custom1"), (guard.Name, guard.StationSn, guard.Arg("mode")));
        Assert.True(transport.PendingCommands.TryRead(out var picture));
        Assert.Equal(("7", BridgeCommands.Picture, "Front Door"), (picture.Id, picture.Name, picture.Arg("camera")));
        Assert.True(transport.PendingCommands.TryRead(out var labelled));
        Assert.Equal("Custom1", labelled.Arg("mode"));
        Assert.False(transport.PendingCommands.TryRead(out _));
    }
}

/// <summary>The transport against a real (in-process) broker.</summary>
public class MqttBrokerTests
{
    private const string StationSn = "T8010P0000000001";
    private const string FrontDoor = "T8140P0000000A01";

    [Fact]
    public async Task Publishes_discovery_state_and_picture_and_takes_guard_mode_commands()
    {
        var port = FreePort();
        using var server = new MQTTnet.Server.MqttServerFactory().CreateMqttServer(
            new MQTTnet.Server.MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        await server.StartAsync();

        var received = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        using var observer = new MQTTnet.MqttClientFactory().CreateMqttClient();
        observer.ApplicationMessageReceivedAsync += e =>
        {
            received[e.ApplicationMessage.Topic] = e.ApplicationMessage.Payload.ToArray();
            return Task.CompletedTask;
        };
        await observer.ConnectAsync(new MQTTnet.MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId("observer").Build());
        await observer.SubscribeAsync("#");

        await using var transport = new MqttTransport(new MqttTransportOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            GuardModes = new Dictionary<string, string> { ["Home"] = "Home", ["Custom1"] = "Night" },
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var commands = System.Threading.Channels.Channel.CreateUnbounded<BridgeCommand>();
        var reading = Task.Run(async () =>
        {
            await foreach (var c in transport.ReadCommandsAsync(stop.Token))
                await commands.Writer.WriteAsync(c);
        });

        var state = new StationState(StationSn, "HomeBase", true, GuardMode.Custom1, GuardMode.Home, EncryptionLevel.Level1,
            [new CameraState(FrontDoor, "Front Door", 0, null, null, 1, 0, 54)], TimeSpan.FromHours(8));
        await transport.PublishStateAsync(state, stop.Token); // before the connection: published once connected
        await WaitForAsync(() => received.ContainsKey($"eufynet/{FrontDoor}/state"), stop.Token);

        byte[] jpeg = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];
        var now = DateTimeOffset.Now;
        await transport.PublishEventAsync(new BridgeEvent(new CameraImageEvent(StationSn, now, FrontDoor, 0, now, "/media/x.jpg", jpeg), FrontDoor, "Front Door"), stop.Token);
        await WaitForAsync(() => received.ContainsKey($"eufynet/{FrontDoor}/event"), stop.Token);

        Assert.Equal("online", Text(received["eufynet/status"]));
        Assert.Equal("Night", JsonDocument.Parse(received[$"eufynet/{StationSn}/state"]).RootElement.GetProperty("guard_mode").GetString());
        Assert.Equal(54, JsonDocument.Parse(received[$"eufynet/{FrontDoor}/state"]).RootElement.GetProperty("battery").GetInt32());
        Assert.Contains("Night", Text(received[$"homeassistant/select/eufynet_{StationSn}_guard_mode/config"]));
        Assert.Equal(jpeg, received[$"eufynet/{FrontDoor}/image"]);

        await observer.PublishStringAsync($"eufynet/{StationSn}/guard_mode/set", "Home");
        var command = await commands.Reader.ReadAsync(stop.Token);
        Assert.Equal((BridgeCommands.SetGuardMode, StationSn, "Home"), (command.Name, command.StationSn, command.Arg("mode")));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    private static string Text(byte[] payload) => System.Text.Encoding.UTF8.GetString(payload);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
