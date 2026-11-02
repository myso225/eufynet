using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EufyNet.P2P;

/// <summary>
/// Station preview pictures (http/utils.js decodeImage): either a plain JPEG or
/// <c>eufysecurity:&lt;serial&gt;:&lt;code&gt;:</c> followed by a JPEG whose first 256 bytes are AES-128-ECB encrypted
/// with a key derived from the serial, the station DID and the code, or <c>v2_eufysecurity:</c> (HomeBase 2 3.4.2.6h),
/// see <see cref="EufyImageV2"/>.
/// </summary>
public static partial class EufyImage
{
    private const string Header = "eufysecurity";

    /// <summary>Base64 as Node's Buffer.from(…, "base64") reads it: the URL-safe alphabet (seen from HB2) and missing padding work too.</summary>
    public static byte[] FromBase64(string text)
    {
        var standard = text.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '='));
    }

    /// <summary>The JPEG; unknown formats are returned unchanged.</summary>
    public static byte[] Decode(string p2pDid, byte[] data)
    {
        if (EufyImageV2.IsV2(data))
            return EufyImageV2.Rebuild(data) ?? data;
        if (data.Length < 41 + 256 || Encoding.ASCII.GetString(data, 0, Header.Length) != Header)
            return data;

        var serialNumber = Encoding.ASCII.GetString(data, 13, 16);
        var code = Encoding.ASCII.GetString(data, 30, 10);
        var key = Encoding.UTF8.GetBytes(GetImageKey(serialNumber, p2pDid, code))[..16];
        using var aes = Aes.Create();
        aes.Key = key;
        var image = data[41..];
        aes.DecryptEcb(image.AsSpan(0, 256), PaddingMode.None).CopyTo(image, 0);
        return image;
    }

    internal static string GetImageKey(string serialNumber, string p2pDid, string code)
    {
        var data = $"01{GetImageBaseCode(serialNumber, p2pDid)}{GetImageSeed(p2pDid, code)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data)).Select(b => (int)b).ToArray();
        var startByte = hash[10];
        for (var i = 0; i < 32; i++)
        {
            var b = hash[i];
            var fixedByte = i < 31 ? hash[i + 1] : startByte;
            if (i == 31 || (i & 1) != 0)
            {
                hash[10] = fixedByte;
                if (126 < b || 126 < hash[10])
                    hash[i] = b <= hash[10] ? hash[10] - b : b - hash[10];
            }
            else if (b < 125 || fixedByte < 125)
            {
                hash[i] = fixedByte + b;
            }
        }
        // Buffer.from(number[]) keeps the low byte.
        return Convert.ToHexString(hash[16..].Select(v => (byte)v).ToArray());
    }

    internal static int GetIdSuffix(string p2pDid)
    {
        var match = DidPattern().Match(p2pDid);
        if (!match.Success || match.Groups[1].Length < 6) // DIDs have 6 digits; Node would get NaN
            return 0;
        var digits = match.Groups[1].Value;
        int Digit(int index) => digits[index] - '0';
        var result = Digit(0) + Digit(1) + Digit(3);
        if (Digit(3) < 5)
            result += Digit(3);
        return result + Digit(5);
    }

    private static string GetImageBaseCode(string serialNumber, string p2pDid)
    {
        // parseInt("0x" + last char); NaN (not hex) makes substring start at 0.
        var nr = int.TryParse(serialNumber[^1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? (hex + 10) % 10 : 0;
        return $"{serialNumber[nr..]}{GetIdSuffix(p2pDid)}";
    }

    private static string GetImageSeed(string p2pDid, string code)
    {
        // parseInt(code.substring(2)): the leading digits.
        var digits = new string(code[2..].TakeWhile(char.IsAsciiDigit).ToArray());
        var nCode = digits.Length == 0 ? "NaN" : long.Parse(digits, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{1000 - GetIdSuffix(p2pDid)}{nCode}")));
    }

    [GeneratedRegex(@"^[A-Z]+-(\d+)-[A-Z]+$")]
    private static partial Regex DidPattern();
}
