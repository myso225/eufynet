using System.Text;
using System.Text.Json;
using EufyNet.P2P;

namespace EufyNet.Tests;

/// <summary>Vectors from eufy-security-client 4.1.0 http/utils.js getImageKey / decodeImage (scratchpad vectors/image.js).</summary>
[Collection(LoopbackUdp.Name)]
public class EufyImageTests
{
    private const string Did = "HXEUCAM-555123-FAKED";

    private const string Encoded =
        "ZXVmeXNlY3VyaXR5OlQ4MTQwUDAwMDAwMDBBMDE6MDAxMjM0NTY3ODrChA0ih/Nemo6gcxCjHIa68P9Z9qme324DAhVGPHF2eynIwO+0BxPuFxtj1AmwwAaTdXA8dBrQ+GaWXD1ZEvPqynZt2h1B/pPUJBuFo05Bl7wLXi2t2+HJCAucBUAW5Wuj7k/Ay8pXMSOnFspNsafvWee7M8iprpGOVr7e8buSQKz8CM33UjyLWUhhe9LR8mFdmj6jzkDrqHBSUXoip3cRj9zfsm31NiJ0g+T7gc3lFURuZKZFQdvsO3IaJB0O2Zrg19otxg1IK5AJmTy+lFY8SKtoFw4XQMi5hVbpvMigDUyscZXlgUPkkv5iNBc5D2CqMcp4GdCj+aCsjjMvfi1qAwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dzj6vH4/wYNFBsiKTA=";

    private const string Decoded =
        "/9gRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dzj6vH4/wYNFBsiKTA3PkVMU1phaG92fYSLkpmgp661vMPK0djf5u30+wIJEBceJSwzOkFIT1ZdZGtyeYCHjpWco6qxuL/GzdTb4unw9/4FDBMaISgvNj1ES1JZYGdudXyDipGYn6attLvCydDX3uXs8/oBCA8WHSQrMjlAR05VXGNqcXh/ho2Um6KpsLe+xczT2uHo7/b9BAsSGSAnLjU8Q0pRWF9mbXR7gomQl56lrLO6wcjP1t3k6/L5AAcOFRwjKjE4P0ZNVFtiaXB3foWMk5qhqK+2vcTL0tng5+71/AMKERgfJi00O0JJUFdeZWxzeoGIj5adpKuyucDHztXc4+rx+P8GDRQbIikw";

    [Theory]
    [InlineData("T8140P0000000A01", Did, "0012345678", "81D4E88CD415DF6BDA6D452A4977C06B")]
    [InlineData("T8140P0000000B02", Did, "0198765432", "0F043307C0451AB1F940952D9E41172D")]
    [InlineData("T8010P0000000001", "ABCDEFG-123456-ABCDE", "0000000001", "C621C252A456FA1BAA37EC3275F3EE0B")]
    public void Image_key_matches_node(string serialNumber, string did, string code, string key) =>
        Assert.Equal(key, EufyImage.GetImageKey(serialNumber, did, code));

    [Theory]
    [InlineData(Did, 15)]
    [InlineData("ABCDEFG-123456-ABCDE", 17)]
    [InlineData("not-a-did", 0)]
    public void Id_suffix_matches_node(string did, int suffix) => Assert.Equal(suffix, EufyImage.GetIdSuffix(did));

    [Fact]
    public void Decodes_encrypted_header() =>
        Assert.Equal(Convert.FromBase64String(Decoded), EufyImage.Decode(Did, Convert.FromBase64String(Encoded)));

    [Fact]
    public void V2_picture_is_rebuilt_with_the_size_from_its_block_count()
    {
        // Like HomeBase 2 3.4.2.6h: everything before the scan is scrambled (DQT, SOF, all Huffman tables), the scan is plain.
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-320x180.jpg"));
        var scan = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 0x03 });
        var scrambled = jpeg[..scan].Select((b, i) => (byte)(b ^ (37 * i + 11))).ToArray();
        byte[] v2 = [.. Encoding.ASCII.GetBytes("v2_eufysecurity:T8010P0000000001:0123456789:"), .. scrambled, .. jpeg[scan..]];

        var rebuilt = EufyImage.Decode(Did, v2);

        Assert.Equal([0xFF, 0xD8], rebuilt[..2]);
        Assert.Equal((320, 180), (rebuilt[165] << 8 | rebuilt[166], rebuilt[163] << 8 | rebuilt[164]));
        Assert.Equal(0x22, rebuilt[169]);
        Assert.Equal(jpeg[scan..], rebuilt[^(jpeg.Length - scan)..]);
    }

    [Theory]
    [InlineData(920, 640, 360)] // 40 × 23 MCUs of 16×16
    [InlineData(240, 320, 180)]
    [InlineData(3600, 1280, 720)]
    public void Block_count_gives_the_size(int mcus, int width, int height) =>
        Assert.Equal((width, height), EufyImageV2.SizeFor(mcus, 16, 16));

    [Theory]
    [InlineData("-_-_", new byte[] { 0xFB, 0xFF, 0xBF })]
    [InlineData("-_8", new byte[] { 0xFB, 0xFF })]
    [InlineData("+/8=", new byte[] { 0xFB, 0xFF })]
    public void Base64_reads_like_node(string text, byte[] bytes) => Assert.Equal(bytes, EufyImage.FromBase64(text));

    [Fact]
    public void Plain_jpeg_is_returned_unchanged()
    {
        byte[] jpeg = [0xFF, 0xD8, 1, 2, 3];
        Assert.Same(jpeg, EufyImage.Decode(Did, jpeg));
    }

    [Fact]
    public async Task Downloads_a_picture_from_the_station()
    {
        const string path = "/media/mmcblk0p1/video/20261101115719_c00.jpg";
        var station = new CommandStation();
        string? request = null;
        station.Handler = (cmd, payload) =>
        {
            if (cmd != CommandType.CmdSetPayload)
                return [];
            request = Encoding.UTF8.GetString(payload);
            var reply = JsonSerializer.Serialize(new { file = path, content = Encoded });
            return [station.Result(cmd, 0),
                StationWire.Data(P2PDataType.Control, station.NextControlSeq(),
                    StationWire.Message(CommandType.CmdDatabaseImage, [.. Encoding.UTF8.GetBytes(reply), 0], type: 0, channel: 255))];
        };
        await using var fake = new FakeStation(Did) { OnData = station.Handle };
        await using var session = fake.CreateSession();
        await session.ConnectAsync(Timeout());
        using var client = new HomeBaseClient(session, new StationCommandInfo("T8010P0000000001", Did, "admin-1", "me", "3.4.2.6h"),
            (_, _) => Task.FromResult<string?>(null));

        var jpeg = await client.DownloadImageAsync(path, Timeout());

        Assert.Equal(Convert.FromBase64String(Decoded), jpeg);
        Assert.Contains("\"cmd\":1308", request);
        Assert.Contains($"\"file\":\"{path}\"", request);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
}
