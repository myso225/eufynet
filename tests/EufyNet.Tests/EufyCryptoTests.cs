using EufyNet.Cloud;

namespace EufyNet.Tests;

/// <summary>Vectors generated with Node crypto, mirroring eufy-security-client http/utils.js + http/api.js.</summary>
public class EufyCryptoTests
{
    internal const string ClientPrivate = "1f2e3d4c5b6a79881f2e3d4c5b6a79881f2e3d4c5b6a79881f2e3d4c5b6a7988";
    internal const string ClientPublic = "04bd7c73b88b2e9b4ceda62022b2da8be13193a5b56edc26e7df7842e24cd0b5eb0605ada7bda83ac6a2b80d7e314040fa47ff16b83bac85cedb014451bb7ce71a";
    internal const string Server2Private = "0a0b0c0d0e0f10111213141516171819202122232425262728292a2b2c2d2e2f";
    internal const string Server2Public = "04b197f741e3bab065f7002610a2f5f9027961164504dfc6e2765299fe447ef937b6df1baf78810299ca01221785d4da302c29f477f36005ee1df0ec27e07eb0d3";

    [Fact]
    public void Shared_secret_with_static_server_key_matches_node()
    {
        using var key = EufyCrypto.ImportKey(ClientPrivate, ClientPublic);
        var secret = EufyCrypto.DeriveSharedSecret(key, EufyCrypto.StaticServerPublicKeyHex);
        Assert.Equal("3bd3e3d6032ac40e3652797db35041de31cadc46d564881b9d6da310c99e13ac", Convert.ToHexStringLower(secret));
    }

    [Fact]
    public void Password_encryption_matches_node()
    {
        using var key = EufyCrypto.ImportKey(ClientPrivate, ClientPublic);
        var secret = EufyCrypto.DeriveSharedSecret(key, EufyCrypto.StaticServerPublicKeyHex);
        Assert.Equal("3Q+80drjwR9yy2mcbomtwuitV2kRAmogLP6hEBxnrvA=", EufyCrypto.EncryptApiData("TestPassword123!", secret));
    }

    [Fact]
    public void Server_payload_with_trailing_nuls_decrypts()
    {
        using var key = EufyCrypto.ImportKey(ClientPrivate, ClientPublic);
        var secret = EufyCrypto.DeriveSharedSecret(key, Server2Public);
        Assert.Equal("2c6fc79bfd6489765bfdd84effc10690050297a9532f676166d79655907f8510", Convert.ToHexStringLower(secret));
        Assert.Equal("""{"user":"x@y.z","n":1}""", EufyCrypto.DecryptApiString("J5e572KzA82nBLfuAMYN28Iv/GV5Y/ujAhkuE8dMKb8=", secret));
    }

    [Fact]
    public void Generated_key_round_trips_through_hex()
    {
        using var key = EufyCrypto.CreateKey();
        var publicHex = EufyCrypto.ExportPublicKeyHex(key);
        using var imported = EufyCrypto.ImportKey(EufyCrypto.ExportPrivateKeyHex(key), publicHex);

        Assert.Equal(130, publicHex.Length);
        Assert.Equal(EufyCrypto.DeriveSharedSecret(key, Server2Public), EufyCrypto.DeriveSharedSecret(imported, Server2Public));
    }

    [Fact]
    public void Md5_matches_node()
    {
        Assert.Equal("1c075c7ed3b9de227c7cec6e13e7f612", EufyCrypto.Md5Hex("user@example.com:secret"));
    }

    [Theory]
    [InlineData(60, "GMT+01:00")]
    [InlineData(-330, "GMT-05:30")]
    [InlineData(0, "GMT+00:00")]
    public void Timezone_header_format(int minutes, string expected)
    {
        Assert.Equal(expected, EufyCloudClient.TimeZoneGmtString(TimeSpan.FromMinutes(minutes)));
    }
}
