using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using EufyNet.P2P;

namespace EufyNet.Tests;

[Collection(LoopbackUdp.Name)]
public class P2PSessionTests
{
    private const string Did = "HXEUCAM-555123-FAKED";

    [Fact]
    public async Task Connects_heartbeats_acks_data_and_handles_end()
    {
        await using var station = new FakeStation(Did);
        await using var session = station.CreateSession();
        var received = new TaskCompletionSource<P2PDataPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DataReceived += p => received.TrySetResult(p);

        await session.ConnectAsync(Timeout());
        Assert.Equal(P2PSessionState.Connected, session.State);
        Assert.Equal(station.EndPoint, session.RemoteEndPoint);

        await station.WaitForAsync(P2PMessageType.Ping);

        await station.SendAsync([0xf1, 0xd0, 0x00, 0x0e, 0xd1, 0x02, 0x00, 0x05, (byte)'X', (byte)'Z', (byte)'Y', (byte)'H', 0x90, 0x04, 0, 0, 0, 0]);
        var ack = await station.WaitForAsync(P2PMessageType.Ack);
        Assert.Equal("f1d10006d10200010005", Convert.ToHexStringLower(ack));
        // The ACK goes out before DataReceived is raised.
        Assert.Equal((ushort)1168, (await received.Task.WaitAsync(Timeout())).CommandId);

        await station.SendAsync(P2PPackets.End());
        Assert.Equal("closed by station", await session.Completion.WaitAsync(Timeout()));
        Assert.Equal(P2PSessionState.Closed, session.State);
    }

    [Fact]
    public async Task Answers_station_ping_with_pong()
    {
        await using var station = new FakeStation(Did);
        await using var session = station.CreateSession();
        await session.ConnectAsync(Timeout());

        await station.SendAsync(P2PPackets.Ping());
        await station.WaitForAsync(P2PMessageType.Pong);
    }

    [Fact]
    public async Task Missing_pongs_end_the_session()
    {
        await using var station = new FakeStation(Did);
        await using var session = station.CreateSession(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
            o.MaxMissedHeartbeats = 4;
        });
        await session.ConnectAsync(Timeout());

        station.AnswerPings = false;
        Assert.Equal("heartbeat lost", await session.Completion.WaitAsync(Timeout()));
    }

    [Fact]
    public async Task Close_sends_end_to_station()
    {
        await using var station = new FakeStation(Did);
        var session = station.CreateSession();
        await session.ConnectAsync(Timeout());

        await session.DisposeAsync();
        await station.WaitForAsync(P2PMessageType.End);
    }

    [Fact]
    public async Task Ignores_lookup_answer_for_another_station()
    {
        await using var station = new FakeStation("OTHERID-111111-AAAAA");
        await using var session = station.CreateSession(o => o.LookupTimeout = TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<TimeoutException>(() => session.ConnectAsync(Timeout()));
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
}

/// <summary>Plays the HomeBase on loopback: answers LOCAL_LOOKUP, CHECK_CAM and PING, records what it receives.</summary>
internal sealed class FakeStation : IAsyncDisposable
{
    private readonly string _did;
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private IPEndPoint? _client;

    public FakeStation(string did)
    {
        _did = did;
        if (OperatingSystem.IsWindows())
            _udp.Client.IOControl(-1744830452, [0, 0, 0, 0], null); // ignore ICMP port-unreachable resets
        _loop = Task.Run(LoopAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public bool AnswerPings { get; set; } = true;

    /// <summary>Called for each DATA packet from the client; the returned packets are sent back.</summary>
    public Func<byte[], IEnumerable<byte[]>>? OnData { get; set; }

    public P2PSession CreateSession(Action<P2PSessionOptions>? configure = null)
    {
        var options = new P2PSessionOptions { LocalLookupPort = EndPoint.Port, RetryInterval = TimeSpan.FromMilliseconds(100) };
        configure?.Invoke(options);
        return new P2PSession(new StationP2PInfo("T8010P0000000001", "HXEUCAM-555123-FAKED", IPAddress.Loopback, null), options);
    }

    public Task SendAsync(byte[] packet) => _udp.SendAsync(packet, _client!).AsTask();

    public async Task<byte[]> WaitForAsync(P2PMessageType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var packet = await _received.Reader.ReadAsync(timeout.Token);
            if (packet[1] == (byte)type)
                return packet;
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var packet = result.Buffer;
                // Only serve the session that looked this station up: parallel tests' CHECK_CAM port sweeps hit neighbours.
                if ((P2PMessageType)packet[1] == P2PMessageType.LocalLookup)
                    _client = result.RemoteEndPoint;
                else if (!result.RemoteEndPoint.Equals(_client))
                    continue;
                _received.Writer.TryWrite(packet);
                switch ((P2PMessageType)packet[1])
                {
                    case P2PMessageType.LocalLookup:
                        await _udp.SendAsync(P2PPackets.Build(P2PMessageType.LocalLookupResponse, [.. P2PPackets.DidToBytes(_did), 0, 0, 0, 0]), _client);
                        break;
                    case P2PMessageType.CheckCam:
                        await _udp.SendAsync(P2PPackets.Build(P2PMessageType.CamId, P2PPackets.DidToBytes(_did)), _client);
                        break;
                    case P2PMessageType.Ping when AnswerPings:
                        await _udp.SendAsync(P2PPackets.Pong(), _client);
                        break;
                    case P2PMessageType.Data when OnData is not null:
                        foreach (var reply in OnData(packet))
                            await _udp.SendAsync(reply, _client);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop;
        _udp.Dispose();
    }
}
