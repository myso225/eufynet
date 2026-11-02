using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EufyNet.P2P;

namespace EufyNet.Tests;

[Collection(LoopbackUdp.Name)]
public class LocalMotionDetectorTests
{
    private const string Did = "HXEUCAM-555123-FAKED";
    private const string FrontDoor = "T8140P0000000A01";
    private static readonly StationCommandInfo Station = new("T8010P0000000001", Did, "admin-1", "me", "3.4.2.6h");

    private const string Backyard = "T8140P0000000B02";
    private static readonly Dictionary<int, string> Cameras = new() { [0] = FrontDoor, [1] = Backyard };

    [Fact]
    public async Task Light_signal_is_motion_on_that_channels_camera_right_away()
    {
        await using var rig = await Rig.StartAsync(new ConcurrentDictionary<string, int> { [FrontDoor] = 10, [Backyard] = 3 });

        await rig.LightSignalAsync(channel: 1);

        var e = Assert.IsType<DetectionEvent>(await rig.NextAsync());
        Assert.Equal(Backyard, e.DeviceSn);
        Assert.Equal(1, e.Channel);
        Assert.Equal(DetectionKind.Motion, e.Kind);
        Assert.Equal(DetectionSource.LightSignal, e.Source);
        Assert.True(DateTimeOffset.Now - e.EventTime < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Recording_saved_later_is_reported_for_the_camera_whose_count_went_up()
    {
        var counts = new ConcurrentDictionary<string, int> { [Station.StationSn] = 5, [FrontDoor] = 10, [Backyard] = 3 };
        var crops = new ConcurrentDictionary<string, string>();
        await using var rig = await Rig.StartAsync(counts, crops);

        await rig.LightSignalAsync(channel: 1);
        Assert.IsType<DetectionEvent>(await rig.NextAsync());
        // The clip saved next is Front Door's (an earlier motion), not Backyard's; the station's own entry is no camera.
        crops[FrontDoor] = CropNow(0);
        counts[FrontDoor] = 11;
        counts[Station.StationSn] = 6;

        var saved = Assert.IsType<RecordingSavedEvent>(await rig.NextAsync());
        Assert.Equal(FrontDoor, saved.DeviceSn);
        Assert.Equal(0, saved.Channel);
        Assert.Equal(11, saved.EventCount);
        Assert.NotNull(saved.CropTime);
        Assert.Equal(crops[FrontDoor], saved.CropPath);
    }

    [Fact]
    public async Task Crop_saved_after_the_recording_is_reported_as_a_picture()
    {
        var counts = new ConcurrentDictionary<string, int> { [FrontDoor] = 10, [Backyard] = 3 };
        var crops = new ConcurrentDictionary<string, string> { [Backyard] = "/media/mmcblk0p1/video/20261101201500_c01.jpg" };
        await using var rig = await Rig.StartAsync(counts, crops);

        await rig.LightSignalAsync(channel: 1);
        Assert.IsType<DetectionEvent>(await rig.NextAsync());
        counts[Backyard] = 4;
        var saved = Assert.IsType<RecordingSavedEvent>(await rig.NextAsync());
        Assert.Null(saved.CropPath);

        crops[Backyard] = CropNow(1);
        var picture = Assert.IsType<PictureSavedEvent>(await rig.NextAsync());
        Assert.Equal((Backyard, 1, crops[Backyard]), (picture.DeviceSn, picture.Channel, picture.CropPath));
    }

    [Fact]
    public async Task Crop_already_there_at_start_is_not_reported_again()
    {
        var counts = new ConcurrentDictionary<string, int> { [FrontDoor] = 10 };
        await using var rig = await Rig.StartAsync(counts, new ConcurrentDictionary<string, string> { [FrontDoor] = CropNow(0) });

        await rig.LightSignalAsync(channel: 0);
        Assert.IsType<DetectionEvent>(await rig.NextAsync());
        counts[FrontDoor] = 11;

        Assert.Null(Assert.IsType<RecordingSavedEvent>(await rig.NextAsync()).CropPath);
    }

    private static string CropNow(int channel) => $"/media/mmcblk0p1/video/{DateTime.Now:yyyyMMddHHmmss}_c{channel:00}.jpg";

    [Fact]
    public async Task Old_crop_time_is_not_reported()
    {
        var counts = new ConcurrentDictionary<string, int> { [FrontDoor] = 10 };
        var crops = new ConcurrentDictionary<string, string>();
        await using var rig = await Rig.StartAsync(counts, crops);

        await rig.LightSignalAsync(channel: 0);
        Assert.IsType<DetectionEvent>(await rig.NextAsync());
        crops[FrontDoor] = "/media/mmcblk0p1/video/20261101051551_c00.jpg";
        counts[FrontDoor] = 11;

        Assert.Null(Assert.IsType<RecordingSavedEvent>(await rig.NextAsync()).CropTime);
    }

    [Fact]
    public async Task Repeated_light_signals_within_the_cooldown_are_one_motion()
    {
        await using var rig = await Rig.StartAsync(new ConcurrentDictionary<string, int> { [FrontDoor] = 10 });

        await rig.LightSignalAsync(channel: 0);
        await rig.LightSignalAsync(channel: 0);
        await rig.LightSignalAsync(channel: 7); // no camera on that channel

        Assert.IsType<DetectionEvent>(await rig.NextAsync());
        await Task.Delay(300);
        Assert.True(rig.Events.IsEmpty);
    }

    /// <summary>Fake station answering CMD_DATABASE_QUERY_LATEST_INFO from <c>counts</c>, plus a started detector.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly FakeStation _fake;
        private readonly P2PSession _session;
        private readonly HomeBaseClient _client;
        private readonly LocalMotionDetector _detector;
        private readonly CommandStation _station;
        private readonly SemaphoreSlim _arrived = new(0);

        private Rig(FakeStation fake, P2PSession session, HomeBaseClient client, LocalMotionDetector detector, CommandStation station)
        {
            (_fake, _session, _client, _detector, _station) = (fake, session, client, detector, station);
            _detector.Event += e =>
            {
                Events.Enqueue(e);
                _arrived.Release();
            };
        }

        public ConcurrentQueue<StationEvent> Events { get; } = new();

        public static async Task<Rig> StartAsync(ConcurrentDictionary<string, int> counts, ConcurrentDictionary<string, string>? crops = null)
        {
            var station = new CommandStation();
            station.Handler = (cmd, _) => cmd == CommandType.CmdSetPayload
                ? [station.Result(cmd, 0), Control(station, CommandType.CmdDatabase, LatestInfo(counts, crops))]
                : [];
            var fake = new FakeStation(Did) { OnData = station.Handle };
            var session = fake.CreateSession();
            await session.ConnectAsync(Timeout());
            var client = new HomeBaseClient(session, Station, (_, _) => Task.FromResult<string?>(null));
            var detector = new LocalMotionDetector(client, Cameras, new LocalMotionOptions { CheckInterval = TimeSpan.FromMilliseconds(50) });
            var rig = new Rig(fake, session, client, detector, station);
            await detector.StartAsync(Timeout());
            return rig;
        }

        public Task LightSignalAsync(int channel) =>
            _fake.SendAsync(Control(_station, CommandType.CmdSetFloodlightManualSwitch, new byte[16], (byte)channel));

        public async Task<StationEvent> NextAsync()
        {
            await _arrived.WaitAsync(Timeout());
            Assert.True(Events.TryDequeue(out var e));
            return e;
        }

        public async ValueTask DisposeAsync()
        {
            await _detector.DisposeAsync();
            _client.Dispose();
            await _session.DisposeAsync();
            await _fake.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("/media/mmcblk0p1/video/20261101051551_c01.jpg", 1, 5, 15, 51)]
    [InlineData("20261231235959_c12.jpg", 12, 23, 59, 59)]
    public void Crop_name_gives_time_and_channel(string path, int channel, int hour, int minute, int second)
    {
        var parsed = new LatestRecordInfo("X", 1, path).ParseCropName();
        Assert.Equal(channel, parsed?.Channel);
        Assert.Equal((hour, minute, second), (parsed!.Value.Time.Hour, parsed.Value.Time.Minute, parsed.Value.Time.Second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/media/other.jpg")]
    public void Crop_name_without_timestamp(string? path) => Assert.Null(new LatestRecordInfo("X", 1, path).ParseCropName());

    [Fact]
    public void Parses_live_latest_info_reply()
    {
        // Seen on HomeBase 2 3.4.2.6h.
        const string json = """
            {"data":[{"device_sn":"T8010P0000000001","payload":{"event_count":591,"crop_hb3_path":"","crop_cloud_path":""}},
            {"device_sn":"T8140P0000000A01","payload":{"event_count":4027,"crop_hb3_path":"/media/mmcblk0p1/video/20261101115719_c00.jpg","crop_cloud_path":""}}],
            "transaction":"1793531713557","table":"history_record_info","cmd":10013,"mIntRet":0,"version":"1.1.0.1","msg":"SUCCESSFUL"}
            """;
        var records = LatestRecordInfo.Parse(JsonDocument.Parse(json).RootElement);

        Assert.Equal([new LatestRecordInfo("T8010P0000000001", 591, null),
            new LatestRecordInfo("T8140P0000000A01", 4027, "/media/mmcblk0p1/video/20261101115719_c00.jpg")], records);
    }

    private static string LatestInfo(IDictionary<string, int> counts, IReadOnlyDictionary<string, string>? crops) => JsonSerializer.Serialize(new
    {
        data = counts.Select(c => new
        {
            device_sn = c.Key,
            payload = new { event_count = c.Value, crop_hb3_path = crops?.GetValueOrDefault(c.Key) ?? "", crop_cloud_path = "" },
        }),
        cmd = (int)CommandType.CmdDatabaseQueryLatestInfo,
        mIntRet = 0,
    });

    private static byte[] Control(CommandStation station, CommandType command, string json) =>
        Control(station, command, [.. Encoding.UTF8.GetBytes(json), 0]);

    private static byte[] Control(CommandStation station, CommandType command, byte[] body, byte channel = 255) =>
        StationWire.Data(P2PDataType.Control, station.NextControlSeq(), StationWire.Message(command, body, type: 0, channel: channel));

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
}
