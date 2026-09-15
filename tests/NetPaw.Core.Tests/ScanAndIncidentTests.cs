using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Scan;
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
