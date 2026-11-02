using System.Security.Cryptography;
using System.Text;

namespace EufyNet.Cloud;

/// <summary>
/// Crypto used by the legacy Eufy cloud API.
/// Port of eufy-security-client http/utils.js (encryptAPIData / decryptAPIData) and the ECDH usage in http/api.js.
/// </summary>
public static class EufyCrypto
{
    /// <summary>Static server key used to encrypt the login password (uncompressed P-256 point, hex).</summary>
    public const string StaticServerPublicKeyHex =
        "04c5c00c4f8d1197cc7c3167c52bf7acb054d722f0ef08dcd7e0883236e0d72a3868d9750cb47fa4619248f3d83f0f662671dadc6e2d31c2f41db0161651c7c076";

    public static ECDiffieHellman CreateKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Imports a key pair from raw hex (D = 32 bytes, public = 04 || X || Y).</summary>
    public static ECDiffieHellman ImportKey(string privateKeyHex, string publicKeyHex)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(privateKeyHex),
            Q = ParsePublicPoint(publicKeyHex),
        };
        return ECDiffieHellman.Create(parameters);
    }

    public static string ExportPrivateKeyHex(ECDiffieHellman key) =>
        Convert.ToHexStringLower(key.ExportParameters(includePrivateParameters: true).D!);

    public static string ExportPublicKeyHex(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return "04" + Convert.ToHexStringLower(q.X!) + Convert.ToHexStringLower(q.Y!);
    }

    /// <summary>Raw ECDH shared secret (X coordinate, 32 bytes) — identical to Node's ecdh.computeSecret().</summary>
    public static byte[] DeriveSharedSecret(ECDiffieHellman own, string peerPublicKeyHex)
    {
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = ParsePublicPoint(peerPublicKeyHex),
        });
        return own.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>AES-256-CBC, key = shared secret, IV = first 16 bytes of the key, PKCS#7, base64 output.</summary>
    public static string EncryptApiData(string plainText, byte[] key)
    {
        using var aes = CreateAes(key);
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plainText), key.AsSpan(0, 16)));
    }

    public static byte[] DecryptApiData(string base64, byte[] key)
    {
        using var aes = CreateAes(key);
        return aes.DecryptCbc(Convert.FromBase64String(base64), key.AsSpan(0, 16));
    }

    /// <summary>Decrypts and returns the UTF-8 text up to the first NUL (Eufy pads some payloads with NULs).</summary>
    public static string DecryptApiString(string base64, byte[] key)
    {
        var bytes = DecryptApiData(base64, key);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    public static string Md5Hex(string value) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    public static string RandomHex(int byteCount) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    private static ECPoint ParsePublicPoint(string publicKeyHex)
    {
        var bytes = Convert.FromHexString(publicKeyHex);
        if (bytes.Length != 65 || bytes[0] != 0x04)
            throw new ArgumentException("Expected an uncompressed P-256 public key (65 bytes, 0x04 prefix).", nameof(publicKeyHex));
        return new ECPoint { X = bytes[1..33], Y = bytes[33..65] };
    }
}
