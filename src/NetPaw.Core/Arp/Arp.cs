using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;

namespace NetPaw.Arp;

/// <summary>One neighbour from the ARP cache (passive) or an explicit ARP probe (active).</summary>
public sealed record ArpEntry(string Ip, string Mac, string Kind, string? Adapter, bool? Alive = null, int? Ms = null)
{
    public bool IsBroadcastOrMulticast => Mac.StartsWith("ff-ff", StringComparison.OrdinalIgnoreCase) || Mac.StartsWith("01-00-5e", StringComparison.OrdinalIgnoreCase) || Ip.StartsWith("224.") || Ip.StartsWith("239.") || Ip.EndsWith(".255");
}

/// <summary>Neighbours of one subnet the adapter sits in, so a flat cache reads as "networks".</summary>
public sealed record NetworkBundle(string Network, int Prefix, string? Adapter, string? OwnAddress, IReadOnlyList<ArpEntry> Hosts)
{
    public string Cidr => $"{Network}/{Prefix}";
    public int Alive => Hosts.Count(h => h.Alive == true);
}

public interface IArpProvider
{
    /// <summary>Passive: the OS neighbour cache. No packets sent.</summary>
    IReadOnlyList<ArpEntry> ReadCache();
    /// <summary>Active: one ARP request. Returns the MAC or null. Source address selects the interface (must be on-link).</summary>
    Task<(string? Mac, int Ms)> Probe(string ip, string? sourceIp, CancellationToken ct);
}

/// <summary>Pure parsing/bundling so the Windows-specific provider stays thin.</summary>
public static class ArpTable
{
    // "  192.168.1.1          00-11-22-33-44-55     dynamisch" / "dynamic" / "static" — locale-agnostic on the first two columns.
    static readonly Regex Row = new(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+(\S+)", RegexOptions.Compiled);
    // "Interface: 192.168.1.10 --- 0xc" / "Schnittstelle: 192.168.1.10 --- 0xc"
    static readonly Regex Iface = new(@"^\S+:\s+(\d{1,3}(?:\.\d{1,3}){3})\s+---", RegexOptions.Compiled);

    /// <summary>Parses `arp -a` output (any Windows locale). The interface address is attached to each row.</summary>
    public static List<ArpEntry> ParseArpA(string text)
    {
        var list = new List<ArpEntry>(); string? iface = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Iface.Match(line) is { Success: true } m) { iface = m.Groups[1].Value; continue; }
            if (Row.Match(line) is { Success: true } r)
            {
                var kind = r.Groups[3].Value.ToLowerInvariant();
                list.Add(new ArpEntry(r.Groups[1].Value, r.Groups[2].Value.ToLowerInvariant(), kind.StartsWith("stat") ? "static" : "dynamic", iface));
            }
        }
        return list;
    }

    /// <summary>Groups neighbours by the adapter subnet that contains them; entries outside every known subnet land in "other".</summary>
    public static List<NetworkBundle> Bundle(IEnumerable<ArpEntry> entries, IReadOnlyList<AdapterInfo> adapters, bool hideNoise = true)
    {
        var subnets = adapters.SelectMany(a => a.Addresses.Select(addr => (Adapter: a.Name, Addr: addr))).ToList();
        var groups = new Dictionary<string, (string? Adapter, IpAddr? Own, List<ArpEntry> Hosts)>();
        foreach (var e in entries)
        {
            if (hideNoise && e.IsBroadcastOrMulticast) continue;
            var hit = subnets.Where(s => s.Addr.Contains(e.Ip)).OrderByDescending(s => s.Addr.PrefixLength).FirstOrDefault();
            var key = hit.Addr is null ? "other" : $"{hit.Addr.Network}/{hit.Addr.PrefixLength}";
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (hit.Adapter, hit.Addr, []);
            if (!g.Hosts.Any(h => h.Ip == e.Ip)) g.Hosts.Add(e);
        }
        return groups.Select(kv => new NetworkBundle(kv.Value.Own?.Network ?? "other", kv.Value.Own?.PrefixLength ?? 0, kv.Value.Adapter, kv.Value.Own?.Address,
                kv.Value.Hosts.OrderBy(h => IpMath.IsIPv4(h.Ip) ? IpMath.ToUInt(h.Ip) : uint.MaxValue).ToList()))
            .OrderBy(b => b.Network == "other" ? 1 : 0).ThenBy(b => b.Cidr).ToList();
    }

    /// <summary>Host addresses of a subnet to sweep, smallest first, excluding network/broadcast and our own.</summary>
    public static IEnumerable<string> SweepTargets(IpAddr own, int maxHosts = 1024)
    {
        if (own.PrefixLength < 22) yield break; // a /16 sweep is not something a tray tool should do
        var net = IpMath.ToUInt(own.Network); var bcast = IpMath.ToUInt(IpMath.BroadcastOf(own.Address, own.PrefixLength));
        var n = 0;
        for (var v = net + 1; v < bcast && n < maxHosts; v++, n++) { var ip = IpMath.FromUInt(v); if (ip != own.Address) yield return ip; }
    }
}

/// <summary>Windows: `arp -a` for the cache, IpHlpApi SendARP for probes. On other OSes the cache is empty and probes fail — used only through the fake in tests.</summary>
public sealed class WindowsArpProvider : IArpProvider
{
    public IReadOnlyList<ArpEntry> ReadCache()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var p = Process.Start(new ProcessStartInfo("arp", "-a") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var text = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            return ArpTable.ParseArpA(text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref uint macLen);

    public Task<(string? Mac, int Ms)> Probe(string ip, string? sourceIp, CancellationToken ct) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return ((string?)null, 0);
        var sw = Stopwatch.StartNew();
        var mac = new byte[6]; uint len = 6;
        // SendARP wants the address bytes in network order packed into a uint (i.e. the raw bytes as-is).
        static uint Packed(string s) { var b = System.Net.IPAddress.Parse(s).GetAddressBytes(); return BitConverter.ToUInt32(b, 0); }
        var rc = SendARP(Packed(ip), sourceIp is null ? 0 : Packed(sourceIp), mac, ref len);
        return (rc == 0 && len == 6 ? string.Join("-", mac.Select(b => b.ToString("x2"))) : null, (int)sw.ElapsedMilliseconds);
    }, ct);
}

/// <summary>
/// Active neighbour check: re-ARPs every cached neighbour of a subnet (or sweeps the subnet) with bounded
/// parallelism. SendARP blocks ~1 s per silent host, so 32 in flight keeps a /24 under ~10 s.
/// </summary>
public static class ArpReachability
{
    public static async Task<List<ArpEntry>> Check(IArpProvider arp, IEnumerable<string> ips, string? sourceIp, int parallel = 32, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new List<ArpEntry>(); var done = 0;
        using var gate = new SemaphoreSlim(parallel);
        var tasks = ips.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (mac, ms) = await arp.Probe(ip, sourceIp, ct);
                lock (results) { if (mac is not null) results.Add(new ArpEntry(ip, mac, "probe", null, true, ms)); progress?.Report(++done); }
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return results.OrderBy(r => IpMath.ToUInt(r.Ip)).ToList();
    }
}
