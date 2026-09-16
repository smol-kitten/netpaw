using System.Buffers.Binary;
using System.Text;
using NetPaw.Discovery;
using Xunit;

namespace NetPaw.Tests;

public static class Frames
{
    static byte[] Tlv(int type, byte[] value) { var b = new byte[2 + value.Length]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)((type << 9) | value.Length)); value.CopyTo(b, 2); return b; }
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    static byte[] S(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A realistic LLDP frame: chassis MAC, port ifName, TTL, port desc, sys name/desc, caps (bridge+router), mgmt 10.0.0.2, port VLAN 20.</summary>
    public static byte[] Lldp(bool tagged = false)
    {
        var mac = new byte[] { 0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var eth = tagged ? Cat(mac, [0x81, 0x00, 0x00, 0x14, 0x88, 0xCC]) : Cat(mac, [0x88, 0xCC]);
        return Cat(eth,
            Tlv(1, Cat([4], [0x00, 0x11, 0x22, 0x33, 0x44, 0x55])),
            Tlv(2, Cat([5], S("Gi1/0/24"))),
            Tlv(3, [0x00, 0x78]),
            Tlv(4, S("Uplink office 3")),
            Tlv(5, S("sw-core-01")),
            Tlv(6, S("Cisco IOS Software, C2960X")),
            Tlv(7, [0x00, 0x14, 0x00, 0x14]),                                     // bridge + router
            Tlv(8, Cat([5, 1], [10, 0, 0, 2], [2, 0, 0, 0, 1], [0])),              // addrLen 5, subtype IPv4, 10.0.0.2, ifSubtype/num, oid len 0
            Tlv(127, Cat([0x00, 0x80, 0xC2, 0x01], [0x00, 0x14])),                 // port VLAN 20
            Tlv(0, []));
    }

    static byte[] CdpTlv(int type, byte[] value) { var b = new byte[4 + value.Length]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)type); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(4 + value.Length)); value.CopyTo(b, 4); return b; }

    public static byte[] Cdp()
    {
        var addr = Cat([0, 0, 0, 1], [1, 1, 0xCC], [0, 4], [192, 168, 10, 1]);       // 1 address: NLPID(1) len 1 proto 0xCC, addrlen 4, IPv4
        var body = Cat([2, 180, 0, 0],                                             // version 2, ttl 180, checksum (ignored)
            CdpTlv(1, S("mikro-sw.lab")), CdpTlv(2, addr), CdpTlv(3, S("ether7")), CdpTlv(4, [0, 0, 0, 0x28]), // switch + IGMP
            CdpTlv(5, S("RouterOS 7.16\nsecond line")), CdpTlv(6, S("MikroTik CRS326")), CdpTlv(0x0A, [0, 100]));
        var llc = new byte[] { 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00 };
        var len = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)(llc.Length + body.Length));
        return Cat([0x01, 0x00, 0x0C, 0xCC, 0xCC, 0xCC, 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE], len, llc, body);
    }

    /// <summary>Wraps frames in a minimal little-endian pcapng (SHB + IDB + EPBs), like pktmon etl2pcapng.</summary>
    public static byte[] PcapNg(params byte[][] frames)
    {
        var ms = new MemoryStream();
        void Block(uint type, byte[] body) { var total = 12 + body.Length; var pad = (4 - body.Length % 4) % 4; total += pad; var b = new byte[total]; BinaryPrimitives.WriteUInt32LittleEndian(b, type); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)total); body.CopyTo(b, 8); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(total - 4), (uint)total); ms.Write(b); }
        var shb = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(shb, 0x1A2B3C4D); BinaryPrimitives.WriteUInt16LittleEndian(shb.AsSpan(4), 1); BinaryPrimitives.WriteUInt64LittleEndian(shb.AsSpan(8), ulong.MaxValue);
        Block(0x0A0D0D0A, shb);
        var idb = new byte[8]; BinaryPrimitives.WriteUInt16LittleEndian(idb, 1); Block(0x00000001, idb);
        var ts = (ulong)DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).ToUnixTimeMilliseconds() * 1000;
        foreach (var f in frames)
        {
            var body = new byte[20 + f.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(ts >> 32)); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)ts);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)f.Length); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)f.Length);
            f.CopyTo(body, 20); Block(0x00000006, body);
        }
        return ms.ToArray();
    }
}

public class DiscoveryTests
{
    [Fact]
    public void DecodesLldp()
    {
        var n = LldpDecoder.Decode(Frames.Lldp())!;
        Assert.Equal(("LLDP", "sw-core-01", "Gi1/0/24", "Uplink office 3", "00-11-22-33-44-55"), (n.Protocol, n.SystemName, n.PortId, n.PortDescription, n.ChassisId));
        Assert.Equal("Cisco IOS Software, C2960X", n.SystemDescription); Assert.Equal("10.0.0.2", n.ManagementAddress); Assert.Equal(20, n.PortVlan);
        Assert.Equal(["bridge", "router"], n.Capabilities);
        Assert.Equal("sw-core-01  port Gi1/0/24  VLAN 20", n.Headline);
        Assert.NotNull(LldpDecoder.Decode(Frames.Lldp(tagged: true)));
        Assert.Null(LldpDecoder.Decode(Frames.Cdp())); Assert.Null(LldpDecoder.Decode(new byte[10]));
    }

    [Fact]
    public void DecodesCdp()
    {
        var n = CdpDecoder.Decode(Frames.Cdp())!;
        Assert.Equal(("CDP", "mikro-sw.lab", "ether7", "192.168.10.1", 100), (n.Protocol, n.SystemName, n.PortId, n.ManagementAddress, n.PortVlan));
        Assert.Equal("MikroTik CRS326 — RouterOS 7.16", n.SystemDescription);
        Assert.Equal(["switch", "IGMP"], n.Capabilities);
        Assert.Null(CdpDecoder.Decode(Frames.Lldp()));
    }

    [Fact]
    public void ReadsPcapngAndDedupes()
    {
        var file = Frames.PcapNg(Frames.Lldp(), Frames.Cdp(), Frames.Lldp(), new byte[] { 1, 2, 3 });
        Assert.Equal(4, PcapNg.Frames(file).Count);
        var n = PcapNg.Neighbors(file);
        Assert.Equal(2, n.Count);
        Assert.Equal(["CDP", "LLDP"], n.Select(x => x.Protocol));
        Assert.Equal(2023, n[0].SeenAt.Year);
        Assert.Empty(PcapNg.Neighbors([]));
    }
}
