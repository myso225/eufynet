using System.Security.Cryptography;
using System.Text;
using EufyNet.P2P;

namespace EufyNet.Tests;

/// <summary>Vectors from copies of the p2p/utils.js command builders run under Node.</summary>
public class P2PCommandsTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private const string GuardJson = """{"account_id":"abc","cmd":1224,"mValue3":0,"payload":{"mode_type":1,"user_name":"me"}}""";

    [Fact]
    public void Level1_key_matches_node() =>
        Assert.Equal("0000001-555123-F", Encoding.ASCII.GetString(P2PCrypto.Level1Key("T8010P0000000001", "HXEUCAM-555123-FAKED")));

    [Fact]
    public void Gateway_info_command_matches_node()
    {
        var bytes = P2PCommandBuilder.CommandHeader(0, CommandType.CmdGatewayinfo).Concat(P2PCommandBuilder.VoidPayload()).ToArray();
        Assert.Equal("d1000000585a59484c04000000000100ff000000", Hex(bytes));
    }

    [Fact]
    public void Int_command_plain_matches_node()
    {
        var bytes = P2PCommandBuilder.CommandHeader(3, CommandType.CmdSetArming)
            .Concat(P2PCommandBuilder.IntPayload(EncryptionLevel.None, null, CommandType.CmdSetArming, 1, "user-1")).ToArray();
        Assert.Equal("d1000003585a5948c804840000000100ff00000001000000757365722d31" + new string('0', 244), Hex(bytes));
    }

    [Fact]
    public void Int_command_level2_matches_node()
    {
        var payload = P2PCommandBuilder.IntPayload(EncryptionLevel.Level2, Key, CommandType.CmdSetArming, 1, "user-1");
        Assert.Equal(
            "900000000100ff02000000df1a9ad19ec0669246e02d6150b360fde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832bfde4fbae4a09e020eff722969f83832b",
            Hex(payload));
    }

    [Fact]
    public void String_payload_level2_matches_node()
    {
        Assert.Equal(
            "600000000100ff0200001c86fbf1def826678a149e1bbcd447b515a6ed977ac255e84290621ea2b0ebecc01f616c411b7ea5610da7f5cb736b7533f1510e05628daf876d4848c571c9059051db82d5adf6b3c78d5ae83c4a3c0f375b4f1801a9847b4312a8ca0c45cc76",
            Hex(P2PCommandBuilder.StringPayload(EncryptionLevel.Level2, Key, CommandType.CmdSetPayload, GuardJson)));
    }

    [Fact]
    public void Commands_outside_the_encrypted_list_stay_plain() =>
        Assert.Equal("020000000100ff0000006869", Hex(P2PCommandBuilder.StringPayload(EncryptionLevel.Level2, Key, CommandType.CmdGatewayinfo, "hi")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gateway_key_unwraps_with_pkcs8_labelled_as_rsa_key(bool base64Ciphertext)
    {
        using var rsa = RSA.Create(1024);
        var pem = "-----BEGIN RSA PRIVATE KEY-----\n" + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks) +
                  "\n-----END RSA PRIVATE KEY-----";
        var sessionKey = RandomNumberGenerator.GetBytes(16);
        var cipher = rsa.Encrypt(sessionKey, RSAEncryptionPadding.Pkcs1);
        byte[] wire = base64Ciphertext ? [.. Encoding.ASCII.GetBytes(Convert.ToBase64String(cipher)), 0, 0] : [.. cipher, 0, 0];

        Assert.Equal(sessionKey, P2PCrypto.DecryptGatewayKey(pem, wire));
    }

    [Theory]
    [InlineData("3.4.2.6h", true)]
    [InlineData("2.0.7.9", true)]
    [InlineData("2.0.7.8", false)]
    [InlineData("1.9", false)]
    [InlineData(null, false)]
    public void Guard_mode_command_style_depends_on_firmware(string? version, bool payload) =>
        Assert.Equal(payload, HomeBaseClient.UsesPayloadGuardMode(version));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

public class P2PMessageAssemblerTests
{
    [Fact]
    public void Single_packet_message()
    {
        var assembler = new P2PMessageAssembler();
        var message = Assert.Single(assembler.Add(Packet(P2PDataType.Control, 0, StationWire.Message(CommandType.CmdGetAlarmMode, [1], type: 0))));
        Assert.Equal(CommandType.CmdGetAlarmMode, message.CommandId);
        Assert.Equal([1], message.Data);
        Assert.Equal(255, message.Channel);
    }

    [Fact]
    public void Message_split_over_packets_and_out_of_order()
    {
        var body = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var wire = StationWire.Message(CommandType.CmdDatabase, body, type: 1);
        var assembler = new P2PMessageAssembler();

        Assert.Empty(assembler.Add(Packet(P2PDataType.Data, 0, wire[..1000])));
        Assert.Empty(assembler.Add(Packet(P2PDataType.Data, 2, wire[2000..])));
        Assert.Empty(assembler.Add(Packet(P2PDataType.Data, 0, wire[..1000]))); // duplicate resend
        var message = Assert.Single(assembler.Add(Packet(P2PDataType.Data, 1, wire[1000..2000])));
        Assert.Equal(body, message.Data);
        Assert.True(message.IsResult);
    }

    [Fact]
    public void Two_messages_in_one_packet_and_sequence_wraparound()
    {
        var assembler = new P2PMessageAssembler();
        for (ushort s = 0; s < 65535; s++)
            assembler.Add(Packet(P2PDataType.Control, s, StationWire.Message(CommandType.CmdPing, [], type: 0)));

        var both = StationWire.Message(CommandType.CmdGetAlarmMode, [0], 0).Concat(StationWire.Message(CommandType.CmdGetAlarmMode, [1], 0)).ToArray();
        Assert.Equal(2, assembler.Add(Packet(P2PDataType.Control, 65535, both)).Count);
        Assert.Single(assembler.Add(Packet(P2PDataType.Control, 0, StationWire.Message(CommandType.CmdPing, [], 0))));
    }

    [Fact]
    public void Garbage_before_header_is_skipped()
    {
        var assembler = new P2PMessageAssembler();
        byte[] wire = [1, 2, 3, .. StationWire.Message(CommandType.CmdPing, [9], 0)];
        Assert.Single(assembler.Add(Packet(P2PDataType.Control, 0, wire)));
        Assert.Equal(3, assembler.DiscardedBytes);
    }

    [Theory]
    [InlineData(10, 9, true)]
    [InlineData(10, 10, false)]
    [InlineData(10, 11, false)]
    [InlineData(2, 65530, true)]
    public void Already_processed_window(int expected, int received, bool processed) =>
        Assert.Equal(processed, P2PMessageAssembler.IsAlreadyProcessed((ushort)expected, (ushort)received));

    private static P2PDataPacket Packet(P2PDataType type, ushort seq, byte[] payload) => new(type, seq, payload);
}

/// <summary>Builds what the station sends.</summary>
internal static class StationWire
{
    /// <summary>Inner message: "XZYH" cmd:u16 LE len:u32 LE 00 00 channel signCode type 00 + body.</summary>
    public static byte[] Message(CommandType command, byte[] body, byte type, byte signCode = 0, byte channel = 255)
    {
        var header = new byte[16];
        "XZYH"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(4), (ushort)command);
        BitConverter.TryWriteBytes(header.AsSpan(6), (uint)body.Length);
        header[12] = channel;
        header[13] = signCode;
        header[14] = type;
        return [.. header, .. body];
    }

    /// <summary>Outer DATA packet: F1 D0 len | D1 dataType seq:u16 BE | inner.</summary>
    public static byte[] Data(P2PDataType dataType, ushort seq, byte[] inner) =>
        P2PPackets.Build(P2PMessageType.Data, [0xd1, (byte)dataType, (byte)(seq >> 8), (byte)seq, .. inner]);

    /// <summary>A command result: return code (i32 LE) + data, optionally AES-encrypted with signCode set.</summary>
    public static byte[] Result(ushort seq, CommandType command, int returnCode, byte[]? data = null, byte[]? key = null)
    {
        byte[] body = [.. BitConverter.GetBytes(returnCode), .. data ?? []];
        if (key is not null)
            body = P2PCrypto.EncryptEcb(P2PCrypto.PadTo16(body), key);
        return Data(P2PDataType.Data, seq, Message(command, body, type: 1, signCode: key is null ? (byte)0 : (byte)1));
    }
}
