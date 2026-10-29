using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

/// <summary>The operator's case (2026-10-29): 192.168.1.x reaches 192.168.0.20, 10.17.0.5/21 on the same VLAN and the same router box does not.</summary>
public class PathDiagnosisTests
{
    sealed class FakeProber(Dictionary<string, SourceProbe> pings, HashSet<string> arpOk) : ISourceProber
    {
        public List<string> Calls { get; } = [];
        public Task<SourceProbe> Ping(string target, string source, int ttl, int timeoutMs, CancellationToken ct)
        { Calls.Add($"ping {target} from {source} ttl {ttl}"); return Task.FromResult(pings.GetValueOrDefault($"{source}>{target}@{(ttl == 1 ? 1 : 64)}") ?? new SourceProbe("timeout", null, null)); }
        public Task<SourceProbe> Connect(string target, int port, string source, int timeoutMs, CancellationToken ct)
        { Calls.Add($"tcp {target}:{port} from {source}"); return Task.FromResult(pings.GetValueOrDefault($"{source}>{target}:{port}") ?? new SourceProbe("timeout", null, null)); }
        public Task<string?> Arp(string ip, string source, CancellationToken ct) => Task.FromResult(arpOk.Contains($"{source}>{ip}") ? "aa-bb-cc-00-00-01" : null);
    }

    static readonly List<RouteEntry> Routes =
    [
        new("0.0.0.0/0", 12, "192.168.1.252", 25),
        new("0.0.0.0/0", 12, "10.17.0.1", 35),
        new("192.168.1.0/24", 12, null, 281), new("10.17.0.0/21", 12, null, 281),
    ];
    static readonly SourceProbe Reply = new("reply", "192.168.0.20", 2);

    [Fact]
    public void MatchRoute_prefers_longest_prefix_then_metric()
    {
        var routes = new List<RouteEntry> { new("0.0.0.0/0", 12, "192.168.1.252", 25), new("192.168.0.0/24", 12, "10.17.0.1", 50), new("192.168.0.0/16", 12, "1.1.1.1", 1) };
        Assert.Equal("10.17.0.1", PathDiagnosis.MatchRoute(routes, "192.168.0.20")!.Gateway);
        Assert.Equal("192.168.1.252", PathDiagnosis.MatchRoute(routes, "8.8.8.8")!.Gateway);
        Assert.Null(PathDiagnosis.MatchRoute([new("10.0.0.0/8", 12, null, 1)], "192.168.0.20"));
    }

    [Fact]
    public async Task Second_subnet_sent_via_the_other_subnets_gateway_is_PcRouting_when_silent_and_Reachable_when_the_router_accepts_it()
    {
        // Lowest-metric default goes via 192.168.1.252; packets from 10.17.0.5 are sent there too (not on its subnet).
        var pings = new Dictionary<string, SourceProbe>
        {
            ["192.168.1.20>192.168.1.252@64"] = Reply, ["192.168.1.20>192.168.0.20@1"] = new("ttl-expired", "192.168.1.252", 1), ["192.168.1.20>192.168.0.20@64"] = Reply,
            ["10.17.0.5>192.168.1.252@64"] = new("reply", "192.168.1.252", 1), ["10.17.0.5>192.168.0.20@1"] = new("ttl-expired", "192.168.1.252", 1),   // the router answers, the target never does
        };
        var arps = new HashSet<string> { "192.168.1.20>192.168.1.252", "10.17.0.5>192.168.1.252" };
        var p = new FakeProber(pings, arps);
        var r = await PathDiagnosis.Run("192.168.0.20", null, Fx.Adapter("Ethernet", addrs: ["192.168.1.20/24", "10.17.0.5/21"]) with { Index = 12 }, [new("192.168.1.20", 24), new("10.17.0.5", 21)], Routes, null, p);
        Assert.Equal(DiagVerdict.Reachable, r.Sources[0].Verdict);
        Assert.Equal(DiagVerdict.PcRouting, r.Sources[1].Verdict);
        Assert.Contains("NOT on this source's subnet", r.Sources[1].Steps[0].Detail);
        Assert.Contains(p.Calls, c => c.Contains("from 10.17.0.5"));   // measured anyway: the ladder still runs from the misrouted source
        Assert.Contains(r.Conclusions, l => l.Contains("PcRouting"));
        // Same table, but the router accepts the foreign source: measured beats inferred, the verdict is Reachable with a note.
        pings["10.17.0.5>192.168.0.20@64"] = Reply;
        var ok = await PathDiagnosis.Run("192.168.0.20", null, Fx.Adapter("Ethernet") with { Index = 12 }, [new("10.17.0.5", 21)], Routes, null, new FakeProber(pings, arps));
        Assert.Equal(DiagVerdict.Reachable, ok.Sources[0].Verdict); Assert.Contains("another subnet", ok.Sources[0].Explanation);
    }

    [Fact]
    public async Task Router_forwards_but_target_has_no_return_route_is_TargetReturnPath()
    {
        // One specific route via 10.17.0.1 (the same box as 192.168.1.252): both sources hand their packets to it, it is on 10.17.0.5's subnet only.
        var routes = new List<RouteEntry> { new("192.168.0.0/24", 12, "10.17.0.1", 5), new("0.0.0.0/0", 12, "192.168.1.252", 25) };
        var p = new FakeProber(new()
        {
            ["192.168.1.20>10.17.0.1@64"] = new("reply", "10.17.0.1", 1), ["192.168.1.20>192.168.0.20@1"] = new("ttl-expired", "10.17.0.1", 1),
            ["10.17.0.5>10.17.0.1@64"] = new("reply", "10.17.0.1", 1), ["10.17.0.5>192.168.0.20@1"] = new("ttl-expired", "10.17.0.1", 1),
            ["10.17.0.5>192.168.0.1@64"] = new("reply", "192.168.0.1", 1),   // far side answers: the router forwards this source
            ["192.168.1.20>192.168.0.20@64"] = Reply,
        }, ["10.17.0.5>10.17.0.1", "192.168.1.20>10.17.0.1"]);
        var r = await PathDiagnosis.Run("192.168.0.20", null, Fx.Adapter("Ethernet") with { Index = 12 }, [new("192.168.1.20", 24), new("10.17.0.5", 21)], routes, "192.168.0.1", p);
        Assert.Equal(DiagVerdict.Reachable, r.Sources[0].Verdict);
        Assert.Equal(DiagVerdict.TargetReturnPath, r.Sources[1].Verdict);
        Assert.Contains(r.Conclusions, l => l.Contains("reaches it on the same cable"));
        Assert.Contains(r.Conclusions, l => l.Contains("route back to 10.17.0.0/21"));
    }

    [Fact]
    public async Task Far_side_silent_is_RouterPolicy_and_no_far_side_is_BeyondRouter()
    {
        var routes = new List<RouteEntry> { new("0.0.0.0/0", 12, "10.17.0.1", 5) };
        var pings = new Dictionary<string, SourceProbe> { ["10.17.0.5>10.17.0.1@64"] = new("reply", "10.17.0.1", 1), ["10.17.0.5>192.168.0.20@1"] = new("ttl-expired", "10.17.0.1", 1) };
        var src = new[] { new IpAddr("10.17.0.5", 21) };
        var a = await PathDiagnosis.Run("192.168.0.20", null, Fx.Adapter("Ethernet") with { Index = 12 }, src, routes, "192.168.0.1", new FakeProber(pings, ["10.17.0.5>10.17.0.1"]));
        Assert.Equal(DiagVerdict.RouterPolicy, a.Sources[0].Verdict);
        var b = await PathDiagnosis.Run("192.168.0.20", null, Fx.Adapter("Ethernet") with { Index = 12 }, src, routes, null, new FakeProber(pings, ["10.17.0.5>10.17.0.1"]));
        Assert.Equal(DiagVerdict.BeyondRouter, b.Sources[0].Verdict);
        Assert.Contains(b.Conclusions, l => l.Contains("--via"));
    }

    [Fact]
    public async Task Ladder_stops_at_the_first_failing_layer()
    {
        var routes = new List<RouteEntry> { new("0.0.0.0/0", 12, "10.17.0.1", 5) };
        var src = new[] { new IpAddr("10.17.0.5", 21) };
        var noArp = await PathDiagnosis.Run("192.168.0.20", 443, Fx.Adapter("Ethernet") with { Index = 12 }, src, routes, null, new FakeProber([], []));
        Assert.Equal(DiagVerdict.LocalLink, noArp.Sources[0].Verdict);
        Assert.Equal(2, noArp.Sources[0].Steps.Count);   // route, arp — nothing sent past a silent gateway
        var noGwPing = await PathDiagnosis.Run("192.168.0.20", 443, Fx.Adapter("Ethernet") with { Index = 12 }, src, routes, null, new FakeProber([], ["10.17.0.5>10.17.0.1"]));
        Assert.Equal(DiagVerdict.RouterInterface, noGwPing.Sources[0].Verdict);
        var icmpOnly = await PathDiagnosis.Run("192.168.0.20", 443, Fx.Adapter("Ethernet") with { Index = 12 }, src, routes, null,
            new FakeProber(new() { ["10.17.0.5>10.17.0.1@64"] = new("reply", "10.17.0.1", 1), ["10.17.0.5>192.168.0.20@1"] = new("ttl-expired", "10.17.0.1", 1), ["10.17.0.5>192.168.0.20:443"] = new("open", null, 3) }, ["10.17.0.5>10.17.0.1"]));
        Assert.Equal(DiagVerdict.IcmpFiltered, icmpOnly.Sources[0].Verdict);
    }

    [Fact]
    public void On_link_target_never_consults_the_gateway()
    {
        var (v, why) = PathDiagnosis.Judge(hasRoute: true, gatewayOnSubnet: true, onLink: true, arp: null, gwPing: null, hop1IsGateway: null, farSide: null, target: false, tcp: null);
        Assert.Equal(DiagVerdict.TargetReturnPath, v); Assert.Contains("same subnet", why);
    }
}
