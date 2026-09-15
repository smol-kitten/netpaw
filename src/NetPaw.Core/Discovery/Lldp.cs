using System.Buffers.Binary;
using System.Text;

namespace NetPaw.Discovery;

/// <summary>What the switch tells us about the port we are plugged into. Either protocol fills the same shape.</summary>
public sealed record SwitchNeighbor(
    string Protocol,            // "LLDP" | "CDP"
    string? SystemName,         // sw-core-01
    string? PortId,             // Gi1/0/24, or a MAC
    string? PortDescription,    // "Uplink to office 3"
    string? ChassisId,          // MAC or hostname
    string? SystemDescription,  // "Cisco IOS Software, C2960X..." / "MikroTik RouterOS 7.16"
    string? ManagementAddress,  // 10.0.0.2
    int? PortVlan,              // 802.1 port VLAN id (LLDP) / native VLAN (CDP)
    IReadOnlyList<string> Capabilities,
    DateTimeOffset SeenAt)
{
    public string Headline => $"{SystemName ?? ChassisId ?? "?"}  port {PortId ?? "?"}{(PortVlan is { } v ? $"  VLAN {v}" : "")}";
}

/// <summary>Minimal Ethernet framing helpers shared by both decoders.</summary>
static class Frame
{
    public const int LldpEtherType = 0x88CC;
    public static readonly byte[] LldpMac = [0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E];
    public static readonly byte[] CdpMac = [0x01, 0x00, 0x0C, 0xCC, 0xCC, 0xCC];

    /// <summary>Returns (etherType, payloadOffset) after the MAC header, skipping an 802.1Q tag if present.</summary>
    public static (int EtherType, int Offset) Header(ReadOnlySpan<byte> f)
    {
        if (f.Length < 14) return (0, f.Length);
        var et = BinaryPrimitives.ReadUInt16BigEndian(f[12..]); var off = 14;
        if (et == 0x8100 && f.Length >= 18) { et = BinaryPrimitives.ReadUInt16BigEndian(f[16..]); off = 18; }
        return (et, off);
    }

    public static string Mac(ReadOnlySpan<byte> b) => string.Join("-", b.ToArray().Select(x => x.ToString("x2")));
    public static string Text(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b).Trim('\0', ' ');
}

/// <summary>IEEE 802.1AB LLDP: TLV list after the Ethernet header. Only the TLVs an admin wants are decoded.</summary>
public static class LldpDecoder
{
    public static bool IsLldp(ReadOnlySpan<byte> frame) => Frame.Header(frame).EtherType == Frame.LldpEtherType;

    public static SwitchNeighbor? Decode(ReadOnlySpan<byte> frame, DateTimeOffset? at = null)
    {
        var (et, off) = Frame.Header(frame);
        if (et != Frame.LldpEtherType) return null;
        string? sys = null, port = null, portDesc = null, chassis = null, sysDesc = null, mgmt = null; int? vlan = null;
        var caps = new List<string>();
        var p = off;
        while (p + 2 <= frame.Length)
        {
            var hdr = BinaryPrimitives.ReadUInt16BigEndian(frame[p..]);
            var type = hdr >> 9; var len = hdr & 0x1FF; p += 2;
            if (type == 0) break;
            if (p + len > frame.Length) break;
            var v = frame.Slice(p, len);
            switch (type)
            {
                case 1: chassis = v.Length > 1 ? (v[0] == 4 && v.Length == 7 ? Frame.Mac(v[1..]) : Frame.Text(v[1..])) : null; break;   // subtype 4 = MAC
                case 2: port = v.Length > 1 ? (v[0] == 3 && v.Length == 7 ? Frame.Mac(v[1..]) : Frame.Text(v[1..])) : null; break;      // subtype 3 = MAC, 5 = ifName, 7 = local
                case 4: portDesc = Frame.Text(v); break;
                case 5: sys = Frame.Text(v); break;
                case 6: sysDesc = Frame.Text(v); break;
                case 7:
                    if (v.Length >= 4)
                    {
                        var enabled = BinaryPrimitives.ReadUInt16BigEndian(v[2..]);
                        string[] names = ["other", "repeater", "bridge", "WLAN AP", "router", "telephone", "DOCSIS", "station"];
                        for (var i = 0; i < names.Length; i++) if ((enabled & (1 << i)) != 0) caps.Add(names[i]);
                    }
                    break;
                case 8:
                    // mgmt addr: [addrLen][subtype][addr...]; subtype 1 = IPv4
                    if (v.Length >= 6 && v[1] == 1 && v[0] == 5) mgmt ??= $"{v[2]}.{v[3]}.{v[4]}.{v[5]}";
                    break;
                case 127:
                    // org-specific: OUI(3) subtype(1) — 00-80-C2 subtype 1 = Port VLAN ID
                    if (v.Length >= 6 && v[0] == 0x00 && v[1] == 0x80 && v[2] == 0xC2 && v[3] == 1) vlan = BinaryPrimitives.ReadUInt16BigEndian(v[4..]);
                    break;
            }
            p += len;
        }
        if (sys is null && chassis is null && port is null) return null;
        return new SwitchNeighbor("LLDP", sys, port, portDesc, chassis, sysDesc, mgmt, vlan, caps, at ?? DateTimeOffset.Now);
    }
}

/// <summary>Cisco Discovery Protocol: SNAP-encapsulated (LLC AA AA 03, OUI 00-00-0C, protocol 0x2000), then version/TTL/checksum and TLVs.</summary>
public static class CdpDecoder
{
    public static bool IsCdp(ReadOnlySpan<byte> frame) => frame.Length > 22 && frame[..6].SequenceEqual(Frame.CdpMac);

    public static SwitchNeighbor? Decode(ReadOnlySpan<byte> frame, DateTimeOffset? at = null)
    {
        if (!IsCdp(frame)) return null;
        // 802.3 length field at 12, LLC at 14: DSAP AA SSAP AA CTRL 03, SNAP OUI 3 bytes, PID 2 bytes → CDP starts at 22
        var start = 22;
        if (frame.Length < start + 4 || frame[14] != 0xAA || frame[15] != 0xAA) return null;
        var p = start + 4; // version(1) ttl(1) checksum(2)
        string? dev = null, port = null, platform = null, sw = null, addr = null; int? vlan = null;
        var caps = new List<string>();
        while (p + 4 <= frame.Length)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(frame[p..]); var len = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 2)..]);
            if (len < 4 || p + len > frame.Length) break;
            var v = frame.Slice(p + 4, len - 4);
            switch (type)
            {
                case 0x0001: dev = Frame.Text(v); break;
                case 0x0002: addr ??= FirstIPv4(v); break;
                case 0x0003: port = Frame.Text(v); break;
                case 0x0004:
                    if (v.Length >= 4)
                    {
                        var bits = BinaryPrimitives.ReadUInt32BigEndian(v);
                        string[] names = ["router", "bridge", "source-route bridge", "switch", "host", "IGMP", "repeater"];
                        for (var i = 0; i < names.Length; i++) if ((bits & (1u << i)) != 0) caps.Add(names[i]);
                    }
                    break;
                case 0x0005: sw = Frame.Text(v); break;
                case 0x0006: platform = Frame.Text(v); break;
                case 0x000A: if (v.Length >= 2) vlan = BinaryPrimitives.ReadUInt16BigEndian(v); break;
            }
            p += len;
        }
        if (dev is null && port is null) return null;
        var desc = string.Join(" — ", new[] { platform, sw?.Split('\n')[0].Trim() }.Where(x => !string.IsNullOrEmpty(x)));
        return new SwitchNeighbor("CDP", dev, port, null, dev, desc.Length == 0 ? null : desc, addr, vlan, caps, at ?? DateTimeOffset.Now);
    }

    static string? FirstIPv4(ReadOnlySpan<byte> v)
    {
        if (v.Length < 4) return null;
        var n = BinaryPrimitives.ReadUInt32BigEndian(v); var p = 4;
        for (var i = 0; i < n && p + 2 <= v.Length; i++)
        {
            var ptype = v[p]; var plen = v[p + 1]; p += 2;
            if (p + plen + 2 > v.Length) break;
            var proto = v.Slice(p, plen); p += plen;
            var alen = BinaryPrimitives.ReadUInt16BigEndian(v[p..]); p += 2;
            if (p + alen > v.Length) break;
            if (ptype == 1 && plen == 1 && proto[0] == 0xCC && alen == 4) return $"{v[p]}.{v[p + 1]}.{v[p + 2]}.{v[p + 3]}";
            p += alen;
        }
        return null;
    }
}

/// <summary>Just enough pcapng to read what `pktmon etl2pcapng` writes: Section Header, Interface Description and Enhanced Packet blocks.</summary>
public static class PcapNg
{
    public static List<(DateTimeOffset At, byte[] Frame)> Frames(byte[] file)
    {
        var list = new List<(DateTimeOffset, byte[])>();
        var p = 0; var little = true; var tsResolution = 1_000_000L; // microseconds by default
        while (p + 12 <= file.Length)
        {
            var type = little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
            if (type == 0x0A0D0D0A) { little = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 8)) == 0x1A2B3C4D; }
            var len = (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 4)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 4)));
            if (len < 12 || p + len > file.Length) break;
            if (type == 0x00000006 && len >= 32)
            {
                var hi = little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 12)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 12));
                var lo = little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 16)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 16));
                var cap = (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 20)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 20)));
                var ts = ((long)hi << 32) | lo;
                if (cap >= 0 && p + 28 + cap <= file.Length)
                    list.Add((DateTimeOffset.FromUnixTimeMilliseconds(ts / (tsResolution / 1000)), file.AsSpan(p + 28, cap).ToArray()));
            }
            p += len;
        }
        return list;
    }

    /// <summary>Decode every LLDP/CDP frame in a capture; newest per (protocol, chassis) wins.</summary>
    public static List<SwitchNeighbor> Neighbors(byte[] pcapng)
    {
        var found = new Dictionary<string, SwitchNeighbor>();
        foreach (var (at, f) in Frames(pcapng))
        {
            var n = LldpDecoder.Decode(f, at) ?? CdpDecoder.Decode(f, at);
            if (n is not null) found[n.Protocol + "|" + (n.ChassisId ?? n.SystemName)] = n;
        }
        return found.Values.OrderBy(n => n.Protocol).ToList();
    }
}
