using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EufyNet.P2P;

/// <summary>Command encryption level, sent in the payload's channel byte. From p2p/types.js EncryptionType.</summary>
public enum EncryptionLevel : byte
{
    None = 0,
    /// <summary>Key derived from serial number and P2P DID.</summary>
    Level1 = 1,
    /// <summary>Per-session key unwrapped with the cloud cipher (RSA) after CMD_GATEWAYINFO.</summary>
    Level2 = 2,
}

/// <summary>A command ready to queue: its type and how to build the payload once the encryption is known.</summary>
public sealed record P2PCommand(CommandType Type, Func<EncryptionLevel, byte[]?, byte[]> BuildPayload, string Description)
{
    public const int StationChannel = 255;

    public static P2PCommand Void(CommandType type, int channel = StationChannel) =>
        new(type, (_, _) => P2PCommandBuilder.VoidPayload(channel), $"{type}");

    public static P2PCommand WithInt(CommandType type, uint value, string strValue = "", int channel = StationChannel) =>
        new(type, (level, key) => P2PCommandBuilder.IntPayload(level, key, type, value, strValue, channel), $"{type}({value})");

    /// <summary>JSON (or other string) body, e.g. CMD_SET_PAYLOAD.</summary>
    public static P2PCommand WithStringPayload(CommandType type, string value, int channel = StationChannel) =>
        new(type, (level, key) => P2PCommandBuilder.StringPayload(level, key, type, value, channel), $"{type}({value})");
}

/// <summary>Command framing and payloads. Port of buildCommandHeader / build*CommandPayload in p2p/utils.js.</summary>
public static class P2PCommandBuilder
{
    /// <summary>Commands whose payload is encrypted when a key is set (isP2PCommandEncrypted).</summary>
    private static readonly HashSet<int> EncryptedCommands =
    [
        1001, 1002, 1004, 1005, 1006, 1007, 1008, 1009, 1010, 1011, 1015, 1017, 1019, 1035, 1045, 1056, 1145, 1146, 1152,
        1200, 1207, 1210, 1213, 1214, 1226, 1227, 1229, 1230, 1233, 1236, 1240, 1241, 1243, 1246, 1272, 1273, 1275, 1400,
        1401, 1402, 1403, 1408, 1409, 1410, 1412, 1413, 1506, 1507, 1607, 1609, 1610, 1611, 1702, 1703, 1704, 1705, 1706,
        1707, 1708, 1709, 1013, 1202, 1205, 1206, 1024, 1025, 1132, 1215, 1216, 1217, 1414, 1026, 1164, 1201, 1027, 1047,
        1048, 1029, 1034, 1036, 1043, 1057, 1203, 1218, 1219, 1220, 1221, 1222, 1223, 1224, 1232, 1234, 1235, 1237, 1238,
        1248, 1253, 1257, 1269, 1800, 1037, 1040, 1038, 1049, 1050, 1051, 1054, 1060, 1204, 1254, 1255, 1256, 1258, 1259,
        1260, 1261, 1262, 1264, 1271, 1350, 1404, 1101, 1106, 1108, 1110, 1111, 1112, 1113, 1114, 1116, 1117, 1118, 1119,
        1121, 1103, 1129, 1211, 1228, 1231, 1242, 1249, 1250, 1251, 1252, 1405, 1406, 1407, 1700,
    ];

    public static bool IsEncrypted(CommandType type) => EncryptedCommands.Contains((int)type);

    /// <summary><c>D1 &lt;dataType&gt; &lt;seq:u16 BE&gt; "XZYH" &lt;cmd:u16 LE&gt;</c>.</summary>
    public static byte[] CommandHeader(ushort sequence, CommandType type, P2PDataType dataType = P2PDataType.Data)
    {
        var header = new byte[10];
        header[0] = 0xd1;
        header[1] = (byte)dataType;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sequence);
        P2PPackets.MagicWord.CopyTo(header.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)type);
        return header;
    }

    /// <summary>Full DATA packet: outer F1 D0 framing + command header + payload.</summary>
    public static byte[] CommandPacket(ushort sequence, CommandType type, byte[] payload) =>
        P2PPackets.Build(P2PMessageType.Data, [.. CommandHeader(sequence, type), .. payload]);

    public static byte[] VoidPayload(int channel = P2PCommand.StationChannel) => [0, 0, 0, 0, 1, 0, (byte)channel, 0, 0, 0];

    public static byte[] IntPayload(EncryptionLevel level, byte[]? key, CommandType type, uint value, string strValue = "", int channel = P2PCommand.StationChannel)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        if (strValue.Length > 0)
            data = [.. data, .. StringWithLength(strValue)];
        return Payload(level, key, type, data, channel);
    }

    public static byte[] StringPayload(EncryptionLevel level, byte[]? key, CommandType type, string value, int channel = P2PCommand.StationChannel) =>
        Payload(level, key, type, Encoding.UTF8.GetBytes(value), channel);

    /// <summary><c>&lt;len:u16 LE&gt; 00 00 01 00 &lt;channel&gt; &lt;encLevel&gt; 00 00 &lt;data&gt;</c>, data AES-encrypted when applicable.</summary>
    private static byte[] Payload(EncryptionLevel level, byte[]? key, CommandType type, byte[] data, int channel)
    {
        var encrypted = IsEncrypted(type) && level != EncryptionLevel.None && key?.Length == 16;
        if (encrypted)
            data = P2PCrypto.EncryptEcb(P2PCrypto.PadTo16(data), key!);

        var payload = new byte[10 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)data.Length);
        payload[4] = 1;
        payload[6] = (byte)channel;
        payload[7] = encrypted ? (byte)level : (byte)0;
        data.CopyTo(payload, 10);
        return payload;
    }

    /// <summary>String NUL-padded up to a multiple of 128 bytes.</summary>
    private static byte[] StringWithLength(string value, int chunk = 128)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new byte[Math.Max(chunk, (bytes.Length + chunk - 1) / chunk * chunk)];
        bytes.CopyTo(result, 0);
        return result;
    }
}

public static class P2PCrypto
{
    /// <summary>Level 1 key: last 7 chars of the serial + 9 chars of the DID starting at the first '-'.</summary>
    public static byte[] Level1Key(string stationSn, string p2pDid)
    {
        var dash = p2pDid.IndexOf('-');
        return Encoding.ASCII.GetBytes(stationSn[^7..] + p2pDid.Substring(dash, Math.Min(9, p2pDid.Length - dash)));
    }

    public static byte[] PadTo16(byte[] data)
    {
        var size = Math.Max(16, (data.Length + 15) / 16 * 16);
        if (size == data.Length)
            return data;
        var result = new byte[size];
        data.CopyTo(result, 0);
        return result;
    }

    public static byte[] EncryptEcb(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, PaddingMode.None);
    }

    public static byte[] DecryptEcb(ReadOnlySpan<byte> data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    /// <summary>
    /// Unwraps the Level 2 session key from the CMD_GATEWAYINFO reply with the cloud cipher's RSA private key
    /// (PKCS#1 v1.5). Node truncates the ciphertext at the first NUL; the raw key-sized block is tried too.
    /// </summary>
    public static byte[] DecryptGatewayKey(string privateKey, ReadOnlySpan<byte> encryptedKey)
    {
        using var rsa = ImportRsaPrivateKey(privateKey);
        var size = rsa.KeySize / 8;

        var candidates = new List<byte[]>();
        var nul = encryptedKey.IndexOf((byte)0);
        var untilNul = (nul < 0 ? encryptedKey : encryptedKey[..nul]).ToArray();
        candidates.Add(untilNul);
        if (encryptedKey.Length >= size)
            candidates.Add(encryptedKey[..size].ToArray());
        if (TryDecodeBase64(untilNul, out var decoded))
            candidates.Add(decoded);

        foreach (var candidate in candidates.Where(c => c.Length == size))
        {
            try
            {
                return rsa.Decrypt(candidate, RSAEncryptionPadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                // Try the next interpretation.
            }
        }
        throw new CryptographicException($"Could not decrypt the gateway key ({encryptedKey.Length} bytes, RSA {rsa.KeySize} bit)");
    }

    /// <summary>Eufy sends the key as PEM or bare base64, PKCS#8 even when labelled "RSA PRIVATE KEY".</summary>
    public static RSA ImportRsaPrivateKey(string privateKey)
    {
        var body = string.Concat(privateKey.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));
        // Seen live (HomeBase 2, cipher from v2/app/cipher/get_ciphers): the whole PEM arrives lower-cased,
        // which destroys the base64. Node fails the same way and falls back to Level 1.
        if (body.Length > 100 && !body.Any(char.IsAsciiLetterUpper))
            throw new CryptographicException("the cloud returned the key lower-cased, so it cannot be decoded");

        var der = Convert.FromBase64String(body);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(der, out _);
        }
        catch (CryptographicException)
        {
            try
            {
                rsa.ImportRSAPrivateKey(der, out _);
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }
        return rsa;
    }

    private static bool TryDecodeBase64(byte[] ascii, out byte[] decoded)
    {
        decoded = [];
        if (ascii.Length == 0 || ascii.Any(b => b > 0x7f))
            return false;
        var buffer = new byte[ascii.Length];
        if (!Convert.TryFromBase64String(Encoding.ASCII.GetString(ascii), buffer, out var written))
            return false;
        decoded = buffer[..written];
        return true;
    }
}
