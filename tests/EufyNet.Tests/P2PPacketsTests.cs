using System.Net;
using EufyNet.P2P;

namespace EufyNet.Tests;

/// <summary>Vectors from copies of the eufy-security-client p2p/utils.js builders run under Node.</summary>
public class P2PPacketsTests
{
    private const string Did = "ABCDEFG-123456-WXYZA";

    [Fact]
    public void Did_buffer_matches_node_and_round_trips()
    {
        var bytes = P2PPackets.DidToBytes(Did);
        Assert.Equal("41424344454647000001e2405758595a41000000", Convert.ToHexStringLower(bytes));
        Assert.Equal(Did, P2PPackets.DidFromBytes(bytes));
    }

    [Fact]
    public void Lookup_and_check_cam_match_node()
    {
        Assert.Equal("f13000020000", Hex(P2PPackets.LocalLookup()));
        Assert.Equal("f141001741424344454647000001e2405758595a41000000000000", Hex(P2PPackets.CheckCam(Did)));
    }

    [Fact]
    public void Cloud_lookups_match_node()
    {
        var local = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 50123);
        Assert.Equal(
            "f126003c41424344454647000001e2405758595a410000000002cbc33201a8c000000000000000000204000044534b4b45593031323334353637383900000000",
            Hex(P2PPackets.LookupWithKey(Did, local, "DSKKEY0123456789")));
        Assert.Equal(
            "f16a002841424344454647000001e2405758595a4100000044534b4b45593031323334353637383900000000",
            Hex(P2PPackets.LookupWithKey2(Did, "DSKKEY0123456789")));
    }

    [Fact]
    public void Acks_match_node()
    {
        Assert.Equal("f1d10006d10000010007", Hex(P2PPackets.Ack(P2PDataType.Data, 7)));
        Assert.Equal("f1d10006d10200010201", Hex(P2PPackets.Ack(P2PDataType.Control, 513)));
    }

    [Fact]
    public void App_conn_decodes_like_node()
    {
        var servers = CloudIpDecoder.Decode("EDGHFDBMKFJIGEJLFBHHEHFKHAMHGHMPGPEDBACEAA:EUPRAKM");
        Assert.Equal(["34.117.1.2:32100", "3.120.4.5:32100"], servers.Select(s => s.ToString()));
    }

    [Fact]
    public void Lookup_addr_parses_reversed_ip_and_le_port()
    {
        byte[] packet = [0xf1, 0x40, 0x00, 0x10, 0x00, 0x02, 0x61, 0x7d, 224, 1, 168, 192, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal("192.168.1.224:32097", P2PPackets.ParseLookupAddr(packet)!.ToString());
    }

    [Fact]
    public void Data_packet_exposes_type_sequence_and_command()
    {
        byte[] packet = [0xf1, 0xd0, 0x00, 0x0e, 0xd1, 0x02, 0x00, 0x05, (byte)'X', (byte)'Z', (byte)'Y', (byte)'H', 0x90, 0x04, 0, 0, 0, 0];
        var data = P2PPackets.ParseData(packet)!;
        Assert.Equal(P2PDataType.Control, data.DataType);
        Assert.Equal(5, data.Sequence);
        Assert.Equal((ushort)1168, data.CommandId);
    }

    [Theory]
    [InlineData("192.168.1.224", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("46.34.229.203", false)]
    public void Private_address_detection(string ip, bool expected) =>
        Assert.Equal(expected, P2PPackets.IsPrivate(IPAddress.Parse(ip)));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
