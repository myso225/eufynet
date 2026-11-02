using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EufyNet.P2P;

namespace EufyNet.Tests;

[Collection(LoopbackUdp.Name)]
public class HomeBaseClientTests
{
    private const string Did = "HXEUCAM-555123-FAKED";
    private static readonly StationCommandInfo Station = new("T8010P0000000001", Did, "admin-1", "me", "3.4.2.6h");

    [Fact]
    public async Task Gateway_info_sets_level2_and_guard_mode_is_sent_encrypted()
    {
        using var rsa = RSA.Create(1024);
        var pem = "-----BEGIN RSA PRIVATE KEY-----\n" + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()) + "\n-----END RSA PRIVATE KEY-----";
        var sessionKey = RandomNumberGenerator.GetBytes(16);
        var station = new CommandStation();
        string? receivedJson = null;

        station.Handler = (cmd, payload) =>
        {
            switch (cmd)
            {
                case CommandType.CmdGatewayinfo:
                    byte[] body = [0x07, 0x00, 0x00, 0x00, .. rsa.Encrypt(sessionKey, RSAEncryptionPadding.Pkcs1)];
                    return [StationWire.Data(P2PDataType.Control, station.NextControlSeq(), StationWire.Message(CommandType.CmdGatewayinfo, body, type: 0))];
                case CommandType.CmdSetPayload:
                    Assert.Equal((byte)EncryptionLevel.Level2, payload[7]);
                    var data = P2PCrypto.DecryptEcb(payload.AsSpan(10, BinaryPrimitives.ReadUInt16LittleEndian(payload)), sessionKey);
                    receivedJson = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    return [station.Result(CommandType.CmdSetPayload, 0, sessionKey)];
                default:
                    return [];
            }
        };

        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());

        int? requestedCipher = null;
        using var client = new HomeBaseClient(session, Station, (id, _) =>
        {
            requestedCipher = id;
            return Task.FromResult<string?>(pem);
        }, GuardMode.Schedule);

        Assert.Equal(EncryptionLevel.Level2, await client.InitializeAsync(Timeout()));
        Assert.Equal(7, requestedCipher);

        GuardMode? changed = null;
        client.GuardModeChanged += m => changed = m;
        var result = await client.SetGuardModeAsync(GuardMode.Home, Timeout());

        Assert.True(result.IsSuccess);
        Assert.Equal(GuardMode.Home, changed);
        using var json = JsonDocument.Parse(receivedJson!);
        Assert.Equal("admin-1", json.RootElement.GetProperty("account_id").GetString());
        Assert.Equal(1224, json.RootElement.GetProperty("cmd").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("payload").GetProperty("mode_type").GetInt32());
        Assert.Equal("me", json.RootElement.GetProperty("payload").GetProperty("user_name").GetString());
    }

    [Fact]
    public async Task No_gateway_reply_keeps_encryption_off()
    {
        var station = new CommandStation();
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var client = new HomeBaseClient(session, Station, (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(EncryptionLevel.None, await client.InitializeAsync(Timeout()));
    }

    [Fact]
    public async Task Missing_cipher_falls_back_to_level1()
    {
        var station = new CommandStation();
        station.Handler = (cmd, _) => cmd == CommandType.CmdGatewayinfo
            ? [StationWire.Data(P2PDataType.Control, station.NextControlSeq(), StationWire.Message(CommandType.CmdGatewayinfo, [1, 0, 0, 0, 5, 5], type: 0))]
            : [];
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var client = new HomeBaseClient(session, Station, (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(EncryptionLevel.Level1, await client.InitializeAsync(Timeout()));
    }

    [Fact]
    public async Task Alarm_mode_control_message_is_reported()
    {
        var station = new CommandStation();
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var client = new HomeBaseClient(session, Station, (_, _) => Task.FromResult<string?>(null));
        var changed = new TaskCompletionSource<GuardMode>();
        client.AlarmModeChanged += m => changed.TrySetResult(m);

        await fake.SendAsync(StationWire.Data(P2PDataType.Control, station.NextControlSeq(), StationWire.Message(CommandType.CmdGetAlarmMode, [63], type: 0)));

        Assert.Equal(GuardMode.Disarmed, await changed.Task.WaitAsync(Timeout()));
        Assert.Equal(GuardMode.Disarmed, client.AlarmMode);
    }

    [Fact]
    public async Task Command_is_resent_until_acked_and_failed_to_request_is_retried()
    {
        var station = new CommandStation { DropFirstPackets = 3 };
        var calls = 0;
        station.Handler = (cmd, _) => [station.Result(cmd, ++calls == 1 ? (int)P2PErrorCode.ErrorFailedToRequest : 0)];
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var channel = new P2PCommandChannel(session, Station.StationSn, Did, new P2PCommandChannelOptions { ResendInterval = TimeSpan.FromMilliseconds(50) });

        var result = await channel.SendAsync(P2PCommand.WithInt(CommandType.CmdGetTfcardStatus, 0), cancellationToken: Timeout());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Missing_result_times_out()
    {
        var station = new CommandStation();
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var channel = new P2PCommandChannel(session, Station.StationSn, Did);

        var ex = await Assert.ThrowsAsync<P2PCommandTimeoutException>(() =>
            channel.SendAsync(P2PCommand.Void(CommandType.CmdGetTfcardStatus), TimeSpan.FromMilliseconds(300), Timeout()));
        Assert.Equal("result", ex.Stage);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
}

/// <summary>Station-side command handling for <see cref="FakeStation.OnData"/>: ACKs commands, replies via <see cref="Handler"/>.</summary>
internal sealed class CommandStation
{
    private readonly HashSet<ushort> _handled = [];
    private ushort _dataSeq;
    private ushort _controlSeq;

    /// <summary>Command type + raw payload (after the 10-byte header) → packets to send back.</summary>
    public Func<CommandType, byte[], IEnumerable<byte[]>> Handler { get; set; } = (_, _) => [];

    /// <summary>Ignore this many command packets entirely (simulates loss) before answering.</summary>
    public int DropFirstPackets { get; set; }

    public ushort NextControlSeq() => _controlSeq++;

    public byte[] Result(CommandType command, int returnCode, byte[]? key = null) =>
        StationWire.Result(_dataSeq++, command, returnCode, key: key);

    public IEnumerable<byte[]> Handle(byte[] packet)
    {
        if (DropFirstPackets > 0)
        {
            DropFirstPackets--;
            return [];
        }
        var seq = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6));
        var command = (CommandType)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12));
        var ack = P2PPackets.Ack(P2PDataType.Data, seq);
        if (!_handled.Add(seq))
            return [ack]; // resend of a command already answered
        return [ack, .. Handler(command, packet[14..])];
    }
}
