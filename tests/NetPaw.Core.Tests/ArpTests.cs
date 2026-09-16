using NetPaw.Arp;
using NetPaw.Model;
using NetPaw.Presets;
using Xunit;

namespace NetPaw.Tests;

sealed class FakeArp(Dictionary<string, string> alive) : IArpProvider
{
    public List<(string Ip, string? Src)> Probed { get; } = [];
    public List<ArpEntry> Cache { get; set; } = [];
    public IReadOnlyList<ArpEntry> ReadCache() => Cache;
    public Task<(string? Mac, int Ms)> Probe(string ip, string? sourceIp, CancellationToken ct)
    {
        lock (Probed) Probed.Add((ip, sourceIp));
        return Task.FromResult((alive.TryGetValue(ip, out var mac) ? mac : null, 2));
    }
}

public class ArpTableTests
{
    const string German = """
        Schnittstelle: 192.168.1.10 --- 0xc
          Internetadresse       Physische Adresse     Typ
          192.168.1.1           00-11-22-33-44-55     dynamisch
          192.168.1.255         ff-ff-ff-ff-ff-ff     statisch
          224.0.0.22            01-00-5e-00-00-16     statisch

        Interface: 10.5.0.20 --- 0x7
          Internet Address      Physical Address      Type
          10.5.0.1              aa-bb-cc-dd-ee-ff     dynamic
          10.5.0.77             aa-bb-cc-00-00-01     dynamic
        """;

    [Fact]
    public void ParsesAnyLocaleAndAttachesInterface()
    {
        var e = ArpTable.ParseArpA(German);
        Assert.Equal(5, e.Count);
        Assert.Equal(("192.168.1.1", "00-11-22-33-44-55", "dynamic", "192.168.1.10"), (e[0].Ip, e[0].Mac, e[0].Kind, e[0].Adapter));
        Assert.Equal("static", e[1].Kind); Assert.True(e[1].IsBroadcastOrMulticast); Assert.True(e[2].IsBroadcastOrMulticast);
        Assert.False(new ArpEntry("10.0.1.255", "00-11-22-33-44-66", "dynamic", null).IsBroadcastOrMulticast);   // a real host in a /23
        Assert.Equal("10.5.0.20", e[4].Adapter);
    }

    [Fact]
    public void BundlesBySubnetAndHidesNoise()
    {
        var e = ArpTable.ParseArpA(German);
        var adapters = new[] { Fx.Adapter("Ethernet", addrs: ["192.168.1.10/24", "10.5.0.20/16"]) };
        var b = ArpTable.Bundle(e, adapters);
        Assert.Equal(["10.5.0.0/16", "192.168.1.0/24"], b.Select(x => x.Cidr));
        Assert.Equal(2, b[0].Hosts.Count); Assert.Single(b[1].Hosts);
        Assert.Equal("Ethernet", b[1].Adapter); Assert.Equal("192.168.1.10", b[1].OwnAddress);
        var other = ArpTable.Bundle([new ArpEntry("172.30.1.1", "00-00-00-00-00-01", "dynamic", null)], adapters);
        Assert.Equal("other", other.Single().Network);
    }

    [Fact]
    public void SweepTargetsAreBoundedAndSkipSelf()
    {
        var t = ArpTable.SweepTargets(new IpAddr("192.168.1.10", 24)).ToList();
        Assert.Equal(253, t.Count); Assert.DoesNotContain("192.168.1.10", t); Assert.DoesNotContain("192.168.1.0", t); Assert.DoesNotContain("192.168.1.255", t);
        Assert.Empty(ArpTable.SweepTargets(new IpAddr("10.0.0.5", 16)));
        Assert.Equal(1021, ArpTable.SweepTargets(new IpAddr("10.0.0.5", 22)).Count()); // 1022 hosts minus our own
    }

    [Fact]
    public async Task ReachabilityRunsInParallelAndReportsProgress()
    {
        var arp = new FakeArp(new() { ["192.168.1.1"] = "00-11-22-33-44-55", ["192.168.1.7"] = "aa-aa-aa-aa-aa-aa" });
        var n = 0;
        var r = await ArpReachability.Check(arp, ["192.168.1.7", "192.168.1.1", "192.168.1.2"], "192.168.1.10", 2, new Progress<int>(_ => Interlocked.Increment(ref n)));
        Assert.Equal(["192.168.1.1", "192.168.1.7"], r.Select(x => x.Ip));
        Assert.All(arp.Probed, p => Assert.Equal("192.168.1.10", p.Src));
        Assert.All(r, x => Assert.True(x.Alive));
    }
}

public class RouterFinderTests
{
    static readonly IReadOnlyList<Preset> Presets = PresetLibrary.LoadBundled();

    [Fact]
    public void CandidatesMergeCommonNetsAndPresets()
    {
        var c = RouterFinder.Candidates(Presets);
        var mt = c.Single(x => x.Cidr == "192.168.88.0/24");
        Assert.Contains("192.168.88.1", mt.GatewayGuesses); Assert.Contains("192.168.88.254", mt.GatewayGuesses); Assert.Contains("MikroTik", mt.Vendors);
        var dlink = c.Single(x => x.Cidr == "10.90.90.0/24");           // the /8 default probed as a /24 around it
        Assert.Contains("10.90.90.90", dlink.GatewayGuesses);
        Assert.Equal(c.Count, c.Select(x => x.Cidr).Distinct().Count());
        Assert.True(c.Count >= RouterFinder.CommonNetworks.Length);
        var temp = RouterFinder.TempAddress(mt);
        Assert.Equal(24, temp.PrefixLength); Assert.DoesNotContain(temp.Address, mt.GatewayGuesses); Assert.StartsWith("192.168.88.", temp.Address);
    }

    [Fact]
    public async Task FindAddsRemovesAndReportsHits()
    {
        var arp = new FakeArp(new() { ["192.168.88.1"] = "b8-69-f4-00-00-01", ["10.0.0.1"] = "00-00-00-00-00-aa" });
        var added = new List<string>(); var removed = new List<string>();
        var finder = new RouterFinder((a, _) => { added.Add(a.ToString()); return Task.FromResult(true); }, (a, _) => { removed.Add(a.ToString()); return Task.CompletedTask; }, arp);
        var cands = RouterFinder.Candidates(Presets).Where(c => c.Cidr is "192.168.88.0/24" or "10.0.0.0/24" or "192.168.2.0/24").ToList();
        var adapters = new[] { Fx.Adapter("Ethernet", addrs: ["10.0.0.50/24"]) };   // already on 10.0.0.0/24 → no temp address there
        var hits = await finder.Find(cands, adapters, "Ethernet");
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Ip == "192.168.88.1" && h.Vendors.Contains("MikroTik"));
        Assert.Contains(hits, h => h.Ip == "10.0.0.1");
        Assert.Equal(2, added.Count); Assert.Equal(added, removed);                    // every borrowed address was returned
        Assert.DoesNotContain(added, a => a.StartsWith("10.0.0."));
        Assert.Contains(arp.Probed, p => p.Ip == "10.0.0.1" && p.Src == "10.0.0.50");
    }

    [Fact]
    public async Task FailedTempAddressSkipsTheSubnetButContinues()
    {
        var arp = new FakeArp(new() { ["192.168.2.1"] = "00-00-00-00-00-02" });
        var finder = new RouterFinder((a, _) => Task.FromResult(!a.Address.StartsWith("192.168.88.")), (a, _) => Task.CompletedTask, arp);
        var cands = RouterFinder.Candidates(Presets).Where(c => c.Cidr is "192.168.88.0/24" or "192.168.2.0/24").ToList();
        var hits = await finder.Find(cands, [Fx.Adapter()], "Ethernet");
        Assert.Single(hits); Assert.Equal("192.168.2.1", hits[0].Ip);
    }
}
