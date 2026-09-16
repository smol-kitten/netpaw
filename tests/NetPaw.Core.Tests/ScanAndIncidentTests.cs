using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Scan;
using NetPaw.Arp;
using Xunit;

namespace NetPaw.Tests;

public class IncidentLogTests
{
    [Fact]
    public void AppendsAndReadsJsonLines()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var log = new IncidentLog(Path.Combine(dir, "sub", "incidents.jsonl"));
        Assert.Empty(log.Read());
        log.Append(new Incident(DateTimeOffset.UnixEpoch, "Ethernet", "problem", "Online", "IntranetOnly", "internet lost", ["Lease without DNS"]));
        log.Append(new Incident(DateTimeOffset.UnixEpoch.AddMinutes(1), "Ethernet", "recovery", "IntranetOnly", "Online", "back", []));
        File.AppendAllText(log.File, "garbage\n");
        var all = log.Read();
        Assert.Equal(2, all.Count); Assert.Equal("IntranetOnly", all[0].To); Assert.Single(all[0].Advisories);
        Assert.Single(log.Read(last: 2).Where(i => i.Kind == "recovery"));
        Assert.Contains("Online -> IntranetOnly", all[0].ToString());
    }

    [Fact]
    public async Task OnlyChangesProduceIncidents()
    {
        var c = new ConnectivityChecker(new FakeProbe());
        var snap = await c.Check(Fx.Adapter(gw: "192.168.1.1"), new CheckSettings());
        Assert.Null(IncidentLog.FromChange(snap, null, [], []));
        var adv = new List<Advisory> { new(AdvisorySeverity.Warning, "Lease without DNS", "x", true) };
        var cfg = IncidentLog.FromChange(snap, null, adv, [])!;
        Assert.Equal("config", cfg.Kind); Assert.Contains("Lease without DNS", cfg.Text);
        Assert.Null(IncidentLog.FromChange(snap, null, adv, ["Lease without DNS"]));
        var cleared = IncidentLog.FromChange(snap, null, [], ["Lease without DNS"])!;
        Assert.Equal("config-ok", cleared.Kind);
        var change = new Transitions.Change(NetState.Online, NetState.LinkDown, "Link down", "cable", true);
        Assert.Equal("problem", IncidentLog.FromChange(snap, change, [], [])!.Kind);
    }
}

public class NetworkScannerTests
{
    static Snapshot Snap(bool link = true, bool addr = true, bool gw = true, bool gwOk = false, bool inet = false, bool? dns = null) =>
        new(DateTimeOffset.UnixEpoch, Fx.Adapter(gw: gw ? "10.0.0.1" : null), link, addr, gw,
            [new ProbeResult("10.0.0.1", gwOk, gwOk ? 2 : 1000)], [new ProbeResult("1.1.1.1", inet, 9)], dns is null ? null : new ProbeResult("x", dns.Value, 5), true);

    [Fact]
    public void CandidatesDhcpFirstThenMatchingStatics()
    {
        var a = Fx.Adapter("Ethernet 2");
        var p1 = Fx.Static("any"); var p2 = Fx.Static("other-nic"); p2.Adapter = "Wi-Fi"; var p3 = Fx.Static("mine"); p3.Adapter = "Ethernet 2";
        var dhcp = new Profile { Name = "d", Dhcp = true }; var tmp = Fx.Static("tmp"); tmp.Temporary = true;
        var c = NetworkScanner.Candidates([p1, p2, p3, dhcp, tmp], a, includeDhcp: true);
        Assert.Equal(["DHCP", "any", "mine"], c.Select(x => x.Name));
        Assert.True(c[0].Dhcp); Assert.True(c[0].Temporary);
        Assert.Equal(["any", "mine"], NetworkScanner.Candidates([p1, p2, p3], a, false).Select(x => x.Name));
        // repo profiles come last and are de-duplicated by subnet against the user's own
        var repoSame = Fx.Static("community-dup", "192.168.1.1", "192.168.1.77/24"); repoSame.Source = "community";
        var repoNew = Fx.Static("MikroTik first boot", "192.168.88.1", "192.168.88.250/24"); repoNew.Source = "community";
        Assert.Equal(["DHCP", "any", "mine", "MikroTik first boot"], NetworkScanner.Candidates([p1, p2, p3], a, true, [repoSame, repoNew]).Select(x => x.Name));
    }

    [Fact]
    public void ScoringAndVerdicts()
    {
        Assert.Equal(0, NetworkScanner.Score(Snap(link: false)));
        Assert.Equal(2, NetworkScanner.Score(Snap()));
        Assert.Equal(5, NetworkScanner.Score(Snap(gwOk: true)));
        Assert.Equal(8, NetworkScanner.Score(Snap(gwOk: true, inet: true)));
        Assert.Equal(10, NetworkScanner.Score(Snap(gwOk: true, inet: true, dns: true)));
        Assert.Equal("gateway silent", NetworkScanner.Verdict(Snap()));
        Assert.Equal("intranet only", NetworkScanner.Verdict(Snap(gwOk: true)));
        Assert.Equal("internet, DNS failing", NetworkScanner.Verdict(Snap(gwOk: true, inet: true, dns: false)));
        Assert.Equal("online", NetworkScanner.Verdict(Snap(gwOk: true, inet: true, dns: true)));
    }

    [Fact]
    public async Task FirstWorkingStopsEarlyRankAllTriesEverything()
    {
        var outcomes = new Dictionary<string, Snapshot> { ["DHCP"] = Snap(), ["lab"] = Snap(gwOk: true, inet: true, dns: true), ["home"] = Snap(gwOk: true) };
        var tried = new List<string>();
        var scanner = new NetworkScanner((p, _) => { tried.Add(p.Name); return Task.FromResult(outcomes[p.Name]); });
        var cands = NetworkScanner.Candidates([Fx.Static("lab"), Fx.Static("home")], Fx.Adapter(), true);
        var first = await scanner.Run(cands, ScanMode.FirstWorking);
        Assert.Equal(["DHCP", "lab"], tried);
        Assert.Equal("lab", first[0].Candidate.Name); Assert.True(first[0].FullyWorking);
        tried.Clear();
        var all = await scanner.Run(cands, ScanMode.RankAll);
        Assert.Equal(3, tried.Count);
        Assert.Equal(["lab", "home", "DHCP"], all.Select(r => r.Candidate.Name));
        Assert.Equal([10, 5, 2], all.Select(r => r.Score));
    }

    [Fact]
    public async Task CancellationStopsTheScan()
    {
        using var cts = new CancellationTokenSource();
        var n = 0;
        var scanner = new NetworkScanner((p, _) => { n++; cts.Cancel(); return Task.FromResult(Snap()); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Run([Fx.Static("a"), Fx.Static("b")], ScanMode.RankAll, ct: cts.Token));
        Assert.Equal(1, n);
    }
}

public class AutoSwitchTests
{
    static NetPaw.Adapters.AdapterInfo Live(string[]? addrs, string? gw, string? dhcp = null) =>
        new("Ethernet", "x", "{e}", 1, true, true, dhcp is not null, (addrs ?? []).Select(IpAddr.Parse).ToList(), gw is null ? [] : [gw], [], "AA:AA") { DhcpServers = dhcp is null ? [] : [dhcp] };

    static Profile Auto(string name, string fpMac, string subnet, string? dhcp = null, string[]? addrs = null, string? gw = null, bool dhcpProfile = false)
    {
        var p = dhcpProfile ? new Profile { Name = name, Dhcp = true } : Fx.Static(name, gw ?? "10.0.0.1", addrs ?? ["10.0.0.5/24"]);
        p.AutoSwitch = true; p.Fingerprint = new NetworkFingerprint(fpMac, dhcp, subnet);
        return p;
    }

    [Fact]
    public async Task VirtualRouterMacAloneNeverMatches()
    {
        // Two sites, both 192.168.1.0/24 behind a VRRP pair: same virtual MAC, different DHCP servers.
        Assert.True(NetworkFingerprint.IsVirtualRouterMac("00-00-5E-00-01-01"));
        Assert.True(NetworkFingerprint.IsVirtualRouterMac("00:00:0c:9f:f0:01"));
        Assert.False(NetworkFingerprint.IsVirtualRouterMac("aa-bb-cc-dd-ee-01"));
        var vrrp = new NetworkFingerprint("00-00-5e-00-01-01", null, "192.168.1.0/24");
        Assert.False(vrrp.Matches(vrrp));                                                                    // no DHCP evidence at all
        Assert.False(vrrp.Matches(vrrp with { DhcpServer = "192.168.1.10" }));                              // one side only
        Assert.True((vrrp with { DhcpServer = "192.168.1.10" }).Matches(vrrp with { DhcpServer = "192.168.1.10" }));
        Assert.False((vrrp with { DhcpServer = "192.168.1.10" }).Matches(vrrp with { DhcpServer = "192.168.1.11" }));
        var real = new NetworkFingerprint("aa-bb-cc-dd-ee-01", null, "192.168.1.0/24");
        Assert.True(real.Matches(real with { DhcpServer = "192.168.1.10" }));                               // unchanged rule for real MACs

        var arp = new FakeArp(new() { ["192.168.1.1"] = "00-00-5e-00-01-01" });
        var sw = new AutoSwitcher(arp, (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask);
        var site = Auto("site", "00-00-5e-00-01-01", "192.168.1.0/24", addrs: ["192.168.1.77/24"], gw: "192.168.1.1");
        Assert.Null((await sw.Decide(Live(["192.168.1.20/24"], "192.168.1.1"), [site])).Profile);           // static now, no DHCP server known
        Assert.Equal("site", (await sw.Decide(Live(["192.168.1.20/24"], "192.168.1.1", "192.168.1.10"), [Auto("site", "00-00-5e-00-01-01", "192.168.1.0/24", dhcp: "192.168.1.10", addrs: ["192.168.1.77/24"], gw: "192.168.1.1")])).Profile?.Name);
    }

    [Fact]
    public async Task LearnsAndMatchesExactlyOne()
    {
        var arp = new FakeArp(new() { ["192.168.1.1"] = "aa-bb-cc-dd-ee-01" });
        var sw = new AutoSwitcher(arp, (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask);
        var live = Live(["192.168.1.20/24"], "192.168.1.1", "192.168.1.1");
        var fp = await sw.Learn(live);
        Assert.Equal(new NetworkFingerprint("aa-bb-cc-dd-ee-01", "192.168.1.1", "192.168.1.0/24"), fp);
        var office = Auto("office", "aa-bb-cc-dd-ee-01", "192.168.1.0/24", dhcpProfile: false, addrs: ["192.168.1.77/24"], gw: "192.168.1.1");
        var other = Auto("other", "aa-bb-cc-dd-ee-02", "192.168.1.0/24");
        var off = Auto("off", "aa-bb-cc-dd-ee-01", "192.168.1.0/24"); off.AutoSwitch = false;
        var d = await sw.Decide(live, [office, other, off]);
        Assert.Equal("office", d.Profile!.Name);
        Assert.Null((await sw.Decide(live, [office, Auto("dup", "aa-bb-cc-dd-ee-01", "192.168.1.0/24")])).Profile);   // ambiguous
        Assert.Null((await sw.Decide(live, [other])).Profile);                                                         // no match
        Assert.Null((await sw.Decide(live, [])).Profile);
        var already = Auto("already", "aa-bb-cc-dd-ee-01", "192.168.1.0/24", dhcp: null, dhcpProfile: true);         // DHCP profile matches the DHCP state → already applied
        Assert.Contains("already applied", (await sw.Decide(live, [already])).Reason);
        var wrongDhcp = Auto("wrongdhcp", "aa-bb-cc-dd-ee-01", "192.168.1.0/24", dhcp: "192.168.1.9");
        Assert.Null((await sw.Decide(live, [wrongDhcp])).Profile);                                                     // DHCP server differs → no match
    }

    [Fact]
    public async Task StaticNetworkWithoutLeaseIsIdentifiedByBorrowingTheProfileAddress()
    {
        var arp = new FakeArp(new() { ["10.9.0.1"] = "de-ad-be-ef-00-01" });
        var added = new List<string>(); var removed = new List<string>();
        var sw = new AutoSwitcher(arp, (a, _) => { added.Add(a.ToString()); return Task.FromResult(true); }, (a, _) => { removed.Add(a.ToString()); return Task.CompletedTask; });
        var live = Live(["169.254.3.3/16"], null);                                                                    // link up, no lease
        var lab = Auto("lab", "de-ad-be-ef-00-01", "10.9.0.0/24", addrs: ["10.9.0.50/24"], gw: "10.9.0.1");
        var home = Auto("home", "de-ad-be-ef-00-09", "192.168.1.0/24", addrs: ["192.168.1.50/24"], gw: "192.168.1.1");
        var d = await sw.Decide(live, [home, lab]);
        Assert.Equal("lab", d.Profile!.Name);
        Assert.Equal(added, removed); Assert.Equal(2, added.Count);                                                    // borrowed both, returned both
        Assert.Contains(arp.Probed, p => p.Ip == "10.9.0.1" && p.Src == "10.9.0.50");
    }
}

public class CaptivePortalTests
{
    sealed class PortalProbe : FakeProbe, IProbe
    {
        public bool Portal, Blocked;
        // Mirrors NetworkProbe.Http: only a redirect / foreign 2xx body is a portal; blocked ports and errors are inconclusive (Ok = true).
        public Task<ProbeResult> Http(string url, string expectedBody, int timeoutMs, CancellationToken ct) => Task.FromResult(new ProbeResult(url, !Portal, 5, Portal ? "redirected: http://portal/login" : Blocked ? "inconclusive: connection refused" : null));
    }

    [Fact]
    public async Task PortalStateOnlyWhenEverythingElseWorks()
    {
        var probe = new PortalProbe { Portal = true }; probe.Reachable.UnionWith(["10.0.0.1", "1.1.1.1"]); probe.Resolvable.Add("www.msftconnecttest.com");
        var s = new CheckSettings { Enabled = true, InternetTargets = ["1.1.1.1"], DnsCheckHost = "www.msftconnecttest.com" };
        var snap = await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "10.0.0.1"), s);
        Assert.Equal(NetState.CaptivePortal, snap.State); Assert.True(Snapshot.IsProblem(snap.State));
        probe.Portal = false;
        Assert.Equal(NetState.Online, (await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "10.0.0.1"), s)).State);
        probe.Portal = false; probe.Blocked = true;
        Assert.Equal(NetState.Online, (await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "10.0.0.1"), s)).State);  // blocked port 80 is not a portal
        s.CaptiveProbeUrl = ""; probe.Portal = true;
        Assert.Equal(NetState.Online, (await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "10.0.0.1"), s)).State);
        probe.Reachable.Remove("1.1.1.1");
        Assert.Equal(NetState.IntranetOnly, (await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "10.0.0.1"), s)).State);  // no HTTP probe without internet
    }
}
