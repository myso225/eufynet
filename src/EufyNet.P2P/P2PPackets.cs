using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace EufyNet.P2P;

/// <summary>Outer packet types: <c>F1 &lt;type&gt; &lt;len:u16 BE&gt; &lt;payload&gt;</c>. From eufy-security-client p2p/types.js.</summary>
public enum P2PMessageType : byte
{
    LocalLookup = 0x30,
    /// <summary>Request: CHECK_CAM. Response: LOCAL_LOOKUP_RESP (same code, the direction tells them apart).</summary>
    CheckCam = 0x41,
    LocalLookupResponse = 0x41,
    LookupAddr = 0x40,
    CamId = 0x42,
    LookupWithKey = 0x26,
    LookupWithKey2 = 0x6a,
    Data = 0xd0,
    Ack = 0xd1,
    Ping = 0xe0,
    Pong = 0xe1,
    End = 0xf0,
}

/// <summary>Channel inside DATA/ACK packets (second byte of the <c>D1 xx</c> header).</summary>
public enum P2PDataType : byte
{
    Data = 0,
    Video = 1,
    Control = 2,
    Binary = 3,
}

/// <summary>A DATA packet as received: channel, per-channel sequence number and the inner bytes.</summary>
public sealed record P2PDataPacket(P2PDataType DataType, ushort Sequence, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The command id when this packet starts a new inner message ("XZYH" header), otherwise null.</summary>
    public ushort? CommandId =>
        Payload.Length >= 6 && Payload.Span[..4].SequenceEqual(P2PPackets.MagicWord)
            ? BinaryPrimitives.ReadUInt16LittleEndian(Payload.Span[4..6])
            : null;
}

/// <summary>Builders and parsers for the UDP packets. Port of the packet helpers in eufy-security-client p2p/utils.js.</summary>
public static class P2PPackets
{
    public const byte Magic = 0xf1;
    public const int LocalLookupPort = 32108;
    public const int CloudLookupPort = 32100;

    public static ReadOnlySpan<byte> MagicWord => "XZYH"u8;

    private static readonly byte[] LookupWithKeyMagic = [0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0x04, 0, 0];

    public static byte[] Build(P2PMessageType type, ReadOnlySpan<byte> payload = default)
    {
        var packet = new byte[4 + payload.Length];
        packet[0] = Magic;
        packet[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(4));
        return packet;
    }

    public static bool TryGetType(ReadOnlySpan<byte> packet, out P2PMessageType type)
    {
        type = default;
        if (packet.Length < 4 || packet[0] != Magic)
            return false;
        type = (P2PMessageType)packet[1];
        return true;
    }

    /// <summary>"PREFIX-123456-SUFFIX" → prefix (8 bytes, NUL padded) + number (u32 BE) + suffix (8 bytes).</summary>
    public static byte[] DidToBytes(string p2pDid)
    {
        var parts = p2pDid.Split('-');
        if (parts.Length != 3 || !uint.TryParse(parts[1], out var number))
            throw new FormatException($"Unexpected P2P DID format: {p2pDid}");

        var result = new byte[20];
        WritePadded(result.AsSpan(0, 8), parts[0]);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), number);
        WritePadded(result.AsSpan(12, 8), parts[2]);
        return result;
    }

    /// <summary>Reverse of <see cref="DidToBytes"/>; the number is zero-padded to 6 digits.</summary>
    public static string DidFromBytes(ReadOnlySpan<byte> did)
    {
        var prefix = Encoding.ASCII.GetString(did[..8]).TrimEnd('\0');
        var number = BinaryPrimitives.ReadUInt32BigEndian(did[8..12]);
        var suffix = Encoding.ASCII.GetString(did[12..20]).TrimEnd('\0');
        return $"{prefix}-{number:000000}-{suffix}";
    }

    public static byte[] LocalLookup() => Build(P2PMessageType.LocalLookup, [0, 0]);

    public static byte[] CheckCam(string p2pDid) => Build(P2PMessageType.CheckCam, [.. DidToBytes(p2pDid), 0, 0, 0]);

    /// <summary>Cloud lookup: DID + 00 02 + our port (LE) + our IPv4 (reversed) + magic + DSK key + 00000000.</summary>
    public static byte[] LookupWithKey(string p2pDid, IPEndPoint local, string dskKey)
    {
        var port = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(port, (ushort)local.Port);
        var ip = local.Address.GetAddressBytes();
        Array.Reverse(ip);
        return Build(P2PMessageType.LookupWithKey,
            [.. DidToBytes(p2pDid), 0, 2, .. port, .. ip, .. LookupWithKeyMagic, .. Encoding.ASCII.GetBytes(dskKey), 0, 0, 0, 0]);
    }

    public static byte[] LookupWithKey2(string p2pDid, string dskKey) =>
        Build(P2PMessageType.LookupWithKey2, [.. DidToBytes(p2pDid), .. Encoding.ASCII.GetBytes(dskKey), 0, 0, 0, 0]);

    public static byte[] Ack(P2PDataType dataType, ushort sequence)
    {
        Span<byte> payload = [0xd1, (byte)dataType, 0, 1, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(payload[4..], sequence);
        return Build(P2PMessageType.Ack, payload);
    }

    public static byte[] Ping(ReadOnlySpan<byte> lastPongData = default) => Build(P2PMessageType.Ping, lastPongData);

    public static byte[] Pong() => Build(P2PMessageType.Pong);

    public static byte[] End() => Build(P2PMessageType.End);

    /// <summary>LOCAL_LOOKUP_RESP: the station's DID sits at bytes 4..24.</summary>
    public static string? ParseLocalLookupResponse(ReadOnlySpan<byte> packet) =>
        packet.Length >= 24 ? DidFromBytes(packet[4..24]) : null;

    /// <summary>LOOKUP_ADDR from a cloud server: port at 6..8 (LE), IPv4 reversed at 8..12.</summary>
    public static IPEndPoint? ParseLookupAddr(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12)
            return null;
        var port = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..8]);
        var address = new IPAddress([packet[11], packet[10], packet[9], packet[8]]);
        return address.Equals(IPAddress.Any) ? null : new IPEndPoint(address, port);
    }

    /// <summary>DATA: <c>F1 D0 len | D1 dataType seq:u16 BE | payload</c>.</summary>
    public static P2PDataPacket? ParseData(ReadOnlyMemory<byte> packet)
    {
        var span = packet.Span;
        if (packet.Length < 8 || span[4] != 0xd1 || span[5] > (byte)P2PDataType.Binary)
            return null;
        return new P2PDataPacket((P2PDataType)span[5], BinaryPrimitives.ReadUInt16BigEndian(span[6..8]), packet[8..]);
    }

    /// <summary>ACK from the station: data type at 4..6, count at 6..8 (BE), then the acked sequence numbers.</summary>
    public static (P2PDataType DataType, ushort[] Sequences)? ParseAck(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[4] != 0xd1)
            return null;
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[6..8]);
        var sequences = new List<ushort>(count);
        for (var i = 0; i < count && 8 + i * 2 + 2 <= packet.Length; i++)
            sequences.Add(BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8 + i * 2, 2)));
        return ((P2PDataType)packet[5], sequences.ToArray());
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
            return true;
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                                 (b[0] == 169 && b[1] == 254));
    }

    private static void WritePadded(Span<byte> target, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }
}

/// <summary>Decodes a station's <c>app_conn</c> string into the P2P cloud lookup servers (port 32100).</summary>
public static class CloudIpDecoder
{
    private static readonly byte[] LookupTable =
        Convert.FromHexString("4959433db5bf6da347534f6165e371e9677f02030badb3892b2f35c16b8b959711e5a70deff1050783fb9d3bc5c713171d1f2529d3df");

    public static IReadOnlyList<IPEndPoint> Decode(string? appConn)
    {
        if (string.IsNullOrEmpty(appConn))
            return [];
        var encoded = appConn.Split(':')[0];
        var output = new byte[encoded.Length / 2];
        byte z = 0x39;
        for (var i = 0; i < output.Length; i++)
        {
            var value = (encoded[i * 2] - 'A') * 0x10 + (encoded[i * 2 + 1] - 'A');
            output[i] = (byte)(z ^ LookupTable[i % LookupTable.Length] ^ value);
            z ^= output[i];
        }

        var result = new List<IPEndPoint>();
        foreach (var ip in Encoding.UTF8.GetString(output).Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (IPAddress.TryParse(ip, out var address))
                result.Add(new IPEndPoint(address, P2PPackets.CloudLookupPort));
        return result;
    }
}
