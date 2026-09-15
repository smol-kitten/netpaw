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

public class ApipaTests
{
    [Fact]
    public async Task SelfAssignedAddressCountsAsNoAddress()
    {
        var probe = new FakeProbe();
        var a = Fx.Adapter(addrs: ["169.254.167.202/16"]);
        var snap = await new ConnectivityChecker(probe).Check(a, new CheckSettings { Enabled = true });
        Assert.False(snap.HasAddress);
        Assert.Equal(NetState.NoAddress, snap.State);
        Assert.Empty(probe.Pinged);
    }
}

public class AdvisorTests
{
    static NetPaw.Adapters.AdapterInfo Dhcp(string[]? addrs, string? gw, string[]? dns = null) =>
        new("Ethernet", "x", "{g}", 1, true, true, true, (addrs ?? []).Select(NetPaw.Model.IpAddr.Parse).ToList(), gw is null ? [] : [gw], dns ?? ["10.0.0.53"], "AA");

    [Fact]
    public async Task DhcpFindings()
    {
        var probe = new FakeProbe(); var c = new ConnectivityChecker(probe); var off = new CheckSettings();
        Assert.Contains(Advisor.Analyze(await c.Check(Dhcp(["169.254.5.5/16"], null), off)), a => a.Title == "No DHCP lease" && a.CanRenew);
        var noGw = Advisor.Analyze(await c.Check(Dhcp(["10.0.0.20/24"], null, []), off));
        Assert.Equal(["Lease without gateway", "Lease without DNS"], noGw.Select(a => a.Title));
        Assert.Empty(Advisor.Analyze(await c.Check(Dhcp(["10.0.0.20/24"], "10.0.0.1"), off)));
        // with checks: gateway silent, and DNS failing while internet works
        var on = new CheckSettings { Enabled = true, InternetTargets = ["1.1.1.1"], DnsCheckHost = "x.test" };
        probe.Reachable.Add("1.1.1.1");
        var findings = Advisor.Analyze(await c.Check(Dhcp(["10.0.0.20/24"], "10.0.0.1"), on));
        Assert.Contains(findings, a => a.Title == "Gateway from lease does not answer" && a.Severity == AdvisorySeverity.Error);
        Assert.Contains(findings, a => a.Title == "DNS servers not answering" && a.CanRenew);
        Assert.Empty(Advisor.Analyze(await c.Check(Dhcp(["10.0.0.20/24"], "10.0.0.1") with { Up = false }, on)));
    }

    [Fact]
    public async Task StaticFindingsNeverSuggestRenew()
    {
        var c = new ConnectivityChecker(new FakeProbe());
        var f = Advisor.Analyze(await c.Check(Fx.Adapter(gw: null), new CheckSettings()));
        Assert.Single(f); Assert.False(f[0].CanRenew); Assert.Equal(AdvisorySeverity.Info, f[0].Severity);
        Assert.Contains("static", Advisor.PlanRenew(Fx.Adapter()).Warnings[0]);
        Assert.Equal("ipconfig /renew \"Ethernet 2\"", Advisor.PlanRenew(Dhcp(["10.0.0.20/24"], "10.0.0.1") with { Name = "Ethernet 2" }).Steps[0].CommandLine);
    }

    [Fact]
    public void SchedulerIsBoundedAndSpaced()
    {
        var r = new RepairSettings { AutoRenew = true, AutoRenewIntervalSeconds = 60, AutoRenewMaxAttempts = 2 };
        var s = new RenewScheduler(); var t0 = DateTimeOffset.UnixEpoch;
        var fix = new[] { new Advisory(AdvisorySeverity.Error, "x", "y", true) };
        Assert.False(s.ShouldRenew(new RepairSettings(), fix, t0));                 // off by default
        Assert.False(s.ShouldRenew(r, fix, t0));                                    // first sight: let DHCP finish on its own
        Assert.False(s.ShouldRenew(r, fix, t0.AddSeconds(5)));
        Assert.True(s.ShouldRenew(r, fix, t0.AddSeconds(10))); s.Mark(t0.AddSeconds(10));
        Assert.False(s.ShouldRenew(r, fix, t0.AddSeconds(40)));                     // too soon after an attempt
        Assert.True(s.ShouldRenew(r, fix, t0.AddSeconds(71))); s.Mark(t0.AddSeconds(71));
        Assert.False(s.ShouldRenew(r, fix, t0.AddSeconds(200)));                    // max attempts
        Assert.False(s.ShouldRenew(r, [], t0.AddSeconds(300)));                     // healthy → resets
        Assert.Equal(0, s.Attempts);
        Assert.False(s.ShouldRenew(r, fix, t0.AddSeconds(301)));                    // new incident starts the persistence clock again
        Assert.False(s.ShouldRenew(r, [new Advisory(AdvisorySeverity.Info, "s", "t", false)], t0.AddSeconds(400)));
    }
}
