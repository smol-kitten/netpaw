using NetPaw.Connectivity;
using Xunit;

namespace NetPaw.Tests;

sealed class FakeProbe : IProbe
{
    public HashSet<string> Reachable { get; } = [];
    public HashSet<string> Resolvable { get; } = [];
    public List<string> Pinged { get; } = [];
    public Task<ProbeResult> Ping(string host, int timeoutMs, CancellationToken ct) { Pinged.Add(host); return Task.FromResult(new ProbeResult(host, Reachable.Contains(host), 3)); }
    public Task<ProbeResult> Resolve(string host, int timeoutMs, CancellationToken ct) => Task.FromResult(new ProbeResult(host, Resolvable.Contains(host), 5));
}

public class ConnectivityTests
{
    static CheckSettings On() => new() { Enabled = true, InternetTargets = ["1.1.1.1", "9.9.9.9"], DnsCheckHost = "www.msftconnecttest.com" };

    [Fact]
    public async Task ChecksOffProbesNothing()
    {
        var probe = new FakeProbe();
        var snap = await new ConnectivityChecker(probe).Check(Fx.Adapter(gw: "192.168.1.1"), new CheckSettings());
        Assert.Empty(probe.Pinged);
        Assert.Equal(NetState.ChecksOff, snap.State);
        Assert.Equal(NetState.NoGateway, (await new ConnectivityChecker(probe).Check(Fx.Adapter(), new CheckSettings())).State);
        Assert.Equal(NetState.LinkDown, (await new ConnectivityChecker(probe).Check(Fx.Adapter(up: false), On())).State);
        Assert.Equal(NetState.Unknown, (await new ConnectivityChecker(probe).Check(null, On())).State);
    }

    [Fact]
    public async Task StatesFromProbes()
    {
        var probe = new FakeProbe(); var c = new ConnectivityChecker(probe); var a = Fx.Adapter(gw: "192.168.1.1");
        Assert.Equal(NetState.NoGateway, (await c.Check(a, On())).State);
        probe.Reachable.Add("192.168.1.1");
        Assert.Equal(NetState.IntranetOnly, (await c.Check(a, On())).State);
        probe.Reachable.Add("9.9.9.9");
        Assert.Equal(NetState.DnsBroken, (await c.Check(a, On())).State);          // internet pings but DNS fails
        probe.Resolvable.Add("www.msftconnecttest.com");
        var online = await c.Check(a, On());
        Assert.Equal(NetState.Online, online.State);
        Assert.True(online.GatewayOk); Assert.True(online.InternetOk); Assert.True(online.DnsOk);
        Assert.Equal(["192.168.1.1", "1.1.1.1", "9.9.9.9"], probe.Pinged.TakeLast(3));
        // gateway down but a custom intranet host answers → still intranet
        probe.Reachable.Remove("192.168.1.1"); probe.Reachable.Remove("9.9.9.9");
        var s = On(); s.IntranetTargets = ["10.0.0.5"]; probe.Reachable.Add("10.0.0.5");
        Assert.Equal(NetState.IntranetOnly, (await c.Check(a, s)).State);
        s.DnsCheckHost = ""; probe.Reachable.Add("1.1.1.1");
        Assert.Equal(NetState.Online, (await c.Check(a, s)).State);              // no DNS check configured
    }

    [Fact]
    public async Task TransitionsOnlyOnChangeAndNeverNagOnFirstGoodState()
    {
        var probe = new FakeProbe(); var c = new ConnectivityChecker(probe); var a = Fx.Adapter(gw: "192.168.1.1");
        probe.Reachable.UnionWith(["192.168.1.1", "1.1.1.1"]); probe.Resolvable.Add("www.msftconnecttest.com");
        var s1 = await c.Check(a, On());
        Assert.Null(Transitions.Detect(null, s1));                               // first snapshot, online: silent
        var s2 = await c.Check(a, On());
        Assert.Null(Transitions.Detect(s1, s2));                                 // unchanged: silent
        probe.Reachable.Remove("1.1.1.1");
        var s3 = await c.Check(a, On());
        var ch = Transitions.Detect(s2, s3)!;
        Assert.Equal(NetState.IntranetOnly, ch.To); Assert.True(ch.IsProblem); Assert.Contains("intranet still reachable", ch.Text);
        probe.Reachable.Add("1.1.1.1");
        var back = Transitions.Detect(s3, await c.Check(a, On()))!;
        Assert.Equal(NetState.Online, back.To); Assert.False(back.IsProblem); Assert.Contains("→ online", back.Text);
        var down = Transitions.Detect(null, await c.Check(Fx.Adapter(up: false, gw: "192.168.1.1"), On()))!;
        Assert.Equal(NetState.LinkDown, down.To); Assert.True(down.IsProblem);   // first snapshot but a problem: say it
    }
}
