using NetPaw.Connectivity;
using Xunit;

namespace NetPaw.Tests;

class FakeProbe : IProbe
{
    public HashSet<string> Reachable { get; } = [];
    public HashSet<string> Resolvable { get; } = [];
    public List<string> Pinged { get; } = [];
    public Task<ProbeResult> Ping(string host, int timeoutMs, CancellationToken ct) { Pinged.Add(host); return Task.FromResult(new ProbeResult(host, Reachable.Contains(host), 3)); }
    public Task<ProbeResult> Resolve(string host, int timeoutMs, CancellationToken ct) => Task.FromResult(new ProbeResult(host, Resolvable.Contains(host), 5));
    public HashSet<string> DnsAnswering { get; } = [];
    public List<string> DnsAsked { get; } = [];
    public Task<ProbeResult> DnsQuery(string server, string name, int timeoutMs, CancellationToken ct) { DnsAsked.Add(server); return Task.FromResult(new ProbeResult(server, DnsAnswering.Contains(server), 6, DnsAnswering.Contains(server) ? "1.2.3.4" : "no answer")); }
    /// <summary>target → path of hop addresses; null entries are silent hops. The last entry is the target itself.</summary>
    public Dictionary<string, string?[]> Paths { get; } = [];
    public Task<ProbeResult> PingTtl(string host, int ttl, int timeoutMs, CancellationToken ct)
    {
        if (!Paths.TryGetValue(host, out var path) || ttl > path.Length) return Task.FromResult(new ProbeResult(host, false, timeoutMs, "*"));
        var hop = path[ttl - 1];
        return Task.FromResult(new ProbeResult(host, hop == host, ttl * 2, hop ?? "*"));
    }
    public HashSet<string> OpenPorts { get; } = [];
    public HashSet<string> RefusedPorts { get; } = [];
    public HashSet<string> UnreachableHosts { get; } = [];
    public Task<ProbeResult> Connect(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var t = $"{host}:{port}";
        var detail = OpenPorts.Contains(t) ? "open" : RefusedPorts.Contains(t) ? "refused" : UnreachableHosts.Contains(host) ? "unreachable" : host.EndsWith(".invalid") ? "unresolved" : "timeout";
        return Task.FromResult(new ProbeResult(t, detail == "open", detail == "timeout" ? timeoutMs : 4, detail));
    }
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
    static NetPaw.Adapters.AdapterInfo Dhcp(string[]? addrs, string? gw, string[]? dns = null) => Fx.Nic(dhcp: true, addrs: addrs, gw: gw, dns: dns ?? ["10.0.0.53"]);

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

public class HyperVTests
{
    static NetPaw.Adapters.AdapterInfo Nic(string name, string desc, bool physical, bool hyperv, string[]? addrs, string? gw) => Fx.Nic(name, desc, physical: physical, dhcp: addrs is not null, addrs: addrs, gw: gw, hyperv: hyperv);

    static IReadOnlyList<NetPaw.Adapters.AdapterInfo> Host() => NetPaw.Adapters.AdapterSelector.TagVSwitchUplinks(
    [
        Nic("Ethernet", "Intel(R) 82599 10 Gigabit Network Connection", true, false, null, null),          // bound to the external vSwitch
        Nic("Ethernet 2", "Marvell AQtion 10GBASE-T Network Adapter", true, false, null, null) with { Up = false },
        Nic("vEthernet (UwU)", "Hyper-V Virtual Ethernet Adapter #2", false, true, ["10.10.0.42/24"], "10.10.0.1"),
        Nic("WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz", true, false, null, null) with { Up = false },
    ]);

    [Fact]
    public void UplinkIsTaggedAndVEthernetWinsAutoDetect()
    {
        var all = Host();
        Assert.True(all.First(a => a.Name == "Ethernet").VSwitchUplink);
        Assert.False(all.First(a => a.Name == "Ethernet 2").VSwitchUplink);               // down, not an uplink
        Assert.Equal("vEthernet (UwU)", NetPaw.Adapters.AdapterSelector.Pick(all, null)!.Name);
        Assert.Equal("Ethernet", NetPaw.Adapters.AdapterSelector.Pick(all, "Ethernet")!.Name); // explicit choice still honoured
        Assert.Contains("switch uplink", all.First(a => a.Name == "Ethernet").Summary());
    }

    [Fact]
    public async Task UplinkIsNotAProblemAndGetsAnExplanation()
    {
        var uplink = Host().First(a => a.Name == "Ethernet");
        var snap = await new ConnectivityChecker(new FakeProbe()).Check(uplink, new CheckSettings { Enabled = true });
        Assert.Equal(NetState.VSwitchUplink, snap.State);
        Assert.False(Snapshot.IsProblem(snap.State));
        var adv = Assert.Single(Advisor.Analyze(snap));
        Assert.Equal(AdvisorySeverity.Info, adv.Severity); Assert.Contains("vEthernet", adv.Text); Assert.False(adv.CanRenew);
    }

    [Fact]
    public void InternalSwitchesDoNotTagOrWin()
    {
        // WSL2 laptop: physical NIC waiting for DHCP (APIPA), vEthernet (WSL) with a NAT address and no gateway.
        var all = NetPaw.Adapters.AdapterSelector.TagVSwitchUplinks(
        [
            Nic("Ethernet", "Intel I219", true, false, ["169.254.10.10/16"], null),
            Nic("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", false, true, ["172.29.0.1/20"], null),
        ]);
        Assert.False(all[0].VSwitchUplink);
        Assert.Equal("Ethernet", NetPaw.Adapters.AdapterSelector.Pick(all, null)!.Name);
        var noAddr = NetPaw.Adapters.AdapterSelector.TagVSwitchUplinks([Nic("Ethernet", "Intel I219", true, false, null, null), all[1]]);
        Assert.False(noAddr[0].VSwitchUplink);                       // between link-up and lease: still not an uplink
        Assert.Equal("Ethernet", NetPaw.Adapters.AdapterSelector.Pick(noAddr, null)!.Name);
    }

    [Fact]
    public void NoHyperVMeansNoTagging()
    {
        var plain = NetPaw.Adapters.AdapterSelector.TagVSwitchUplinks([Nic("Ethernet", "Intel", true, false, null, null)]);
        Assert.False(plain[0].VSwitchUplink);
        Assert.Equal("Ethernet", NetPaw.Adapters.AdapterSelector.Pick(plain, null)!.Name);
    }
}

public class VpnTests
{
    static NetPaw.Adapters.AdapterInfo A(string name, string desc, bool up, string[]? addrs, string? gw) => Fx.Nic(name, desc, up, physical: false, addrs: addrs, gw: gw);

    [Fact]
    public void ClassifiesCommonClients()
    {
        Assert.Equal(VpnKind.WireGuard, VpnDetector.Classify("wg0", "WireGuard Tunnel"));
        Assert.Equal(VpnKind.OpenVpn, VpnDetector.Classify("OpenVPN TAP-Windows6", "TAP-Windows Adapter V9"));
        Assert.Equal(VpnKind.OpenVpn, VpnDetector.Classify("OpenVPN Data Channel Offload", "OpenVPN Data Channel Offload"));
        Assert.Equal(VpnKind.WindowsNative, VpnDetector.Classify("Office VPN", "WAN Miniport (IKEv2)"));
        Assert.Equal(VpnKind.Tailscale, VpnDetector.Classify("Tailscale", "Tailscale Tunnel"));
        Assert.Null(VpnDetector.Classify("Ethernet", "Intel(R) I219-LM"));
    }

    [Fact]
    public void DetectsModeAndChanges()
    {
        var before = VpnDetector.Detect([A("wg0", "WireGuard Tunnel", false, null, null), A("Office VPN", "WAN Miniport (IKEv2)", false, null, null)]);
        Assert.Single(before); Assert.False(before[0].Up);                                 // idle WAN miniport hidden, configured WireGuard shown as down
        var after = VpnDetector.Detect([A("wg0", "WireGuard Tunnel", true, ["10.66.0.2/32"], "0.0.0.0"), A("Office VPN", "WAN Miniport (IKEv2)", true, ["172.30.5.9/32"], null)]);
        Assert.Equal(2, after.Count);
        Assert.Equal("full tunnel (default route)", after.First(v => v.Adapter == "wg0").Mode);
        Assert.Equal("split tunnel", after.First(v => v.Adapter == "Office VPN").Mode);
        var ch = VpnDetector.Changes(before, after);
        Assert.Equal(2, ch.Count); Assert.Contains(ch, m => m.StartsWith("VPN up: WireGuard 'wg0' — full tunnel"));
        Assert.Empty(VpnDetector.Changes(after, after));
        var gone = VpnDetector.Changes(after, VpnDetector.Detect([A("wg0", "WireGuard Tunnel", false, null, null)]));
        Assert.Equal(2, gone.Count); Assert.Contains(gone, m => m.Contains("adapter gone"));
    }
}

public class V07AdvisorTests
{
    static NetPaw.Adapters.AdapterInfo Nic(string name, bool up, bool dhcp, string[]? addrs, string? gw) => Fx.Nic(name, up: up, dhcp: dhcp, addrs: addrs, gw: gw);

    [Fact]
    public async Task DockingStationHeldAddress()
    {
        var oldNic = Nic("Ethernet", up: false, dhcp: true, ["10.0.0.5/24"], "10.0.0.1");      // undocked, still holds the lease
        var dockNic = Nic("Ethernet 3", up: true, dhcp: true, ["169.254.7.7/16"], null);
        var snap = await new ConnectivityChecker(new FakeProbe()).Check(dockNic, new CheckSettings());
        var adv = Advisor.Analyze(snap, [oldNic, dockNic]);
        var held = Assert.Single(adv, a => a.Title == "Address held by another adapter");
        Assert.Equal("Ethernet", held.RepairAdapter); Assert.Equal(RepairKind.Reset, held.Repair);    // ipconfig /release refuses a disconnected NIC
        Assert.Contains("10.0.0.5/24", held.Text);
        Assert.Equal("ipconfig /release \"Ethernet\"", Advisor.PlanRelease(oldNic).Steps.Single().CommandLine);
        // With a known DHCP server, only a holder in that server's subnet is blamed.
        var dockKnown = dockNic with { DhcpServers = ["10.0.0.1"] };
        Assert.Contains(Advisor.Analyze(await new ConnectivityChecker(new FakeProbe()).Check(dockKnown, new CheckSettings()), [oldNic, dockKnown]), a => a.Repair == RepairKind.Reset);
        var dockOther = dockNic with { DhcpServers = ["172.30.0.1"] };
        Assert.DoesNotContain(Advisor.Analyze(await new ConnectivityChecker(new FakeProbe()).Check(dockOther, new CheckSettings()), [oldNic, dockOther]), a => a.Repair == RepairKind.Reset);
        Assert.Equal(RepairKind.Renew, Assert.Single(adv, a => a.Title == "No DHCP lease").Repair);
        Assert.Contains(adv, a => a.Title == "No DHCP lease");                                   // the plain finding is still there
        Assert.DoesNotContain(Advisor.Analyze(snap, [dockNic]), a => a.Repair == RepairKind.Release);
        var healthyWifi = Nic("Wi-Fi", up: true, dhcp: true, ["192.168.1.20/24"], "192.168.1.1");   // an up adapter with its own lease is not a "holder"
        Assert.DoesNotContain(Advisor.Analyze(snap, [healthyWifi, dockNic]), a => a.Repair == RepairKind.Release);
    }

    [Fact]
    public async Task TwoDefaultGatewaysAndPrefer()
    {
        var eth = Nic("Ethernet", true, false, ["10.0.0.5/24"], "10.0.0.1");
        var wifi = Nic("Wi-Fi", true, true, ["192.168.1.20/24"], "192.168.1.1");
        var snap = await new ConnectivityChecker(new FakeProbe()).Check(eth, new CheckSettings());
        var adv = Assert.Single(Advisor.Analyze(snap, [eth, wifi]), a => a.Title == "Two default gateways");
        Assert.Equal(RepairKind.Prefer, adv.Repair); Assert.Contains("Wi-Fi (192.168.1.1)", adv.Text);
        var plan = Advisor.PlanPrefer(eth, [eth, wifi]);
        Assert.Equal(["netsh interface ipv4 set interface \"Ethernet\" metric=10", "netsh interface ipv4 set interface \"Wi-Fi\" metric=50"], plan.Steps.Select(s => s.CommandLine));
        Assert.Empty(Advisor.Analyze(snap, [eth]).Where(a => a.Title == "Two default gateways"));
    }

    [Fact]
    public void VerifierSpotsTheTechNetBugShape()
    {
        var p = Fx.Static("lab", "10.2.0.250", "10.2.1.158/23"); p.Dns = ["10.2.0.53"];
        var ok = Fx.Adapter(addrs: ["10.2.1.158/23"], gw: "10.2.0.250") with { Dns = ["10.2.0.53"] };
        Assert.Empty(NetPaw.Planning.ApplyVerifier.Compare(p, ok));
        var bug = ok with { Dhcp = true, Gateways = ["10.2.0.250", "192.168.1.254"], Dns = [] };
        var diffs = NetPaw.Planning.ApplyVerifier.Compare(p, bug);
        Assert.Contains(diffs, d => d.Contains("still reports DHCP"));
        Assert.Contains(diffs, d => d.StartsWith("two default gateways"));
        Assert.Contains(diffs, d => d.StartsWith("DNS is empty"));
        Assert.Empty(NetPaw.Planning.ApplyVerifier.Compare(new NetPaw.Model.Profile { Dhcp = true }, Fx.Adapter(dhcp: true)));
        Assert.Single(NetPaw.Planning.ApplyVerifier.Compare(new NetPaw.Model.Profile { Dhcp = true }, Fx.Adapter(dhcp: false)));
    }
}

public class MultiAdapterTests
{
    [Fact]
    public void SecondariesExcludeWorkUplinkAndVirtual()
    {
        var work = Fx.Nic("Wi-Fi", wireless: true, dhcp: true, addrs: ["10.1.1.5/24"], gw: "10.1.1.1");
        var all = NetPaw.Adapters.AdapterSelector.TagVSwitchUplinks(
        [
            work,
            Fx.Nic("Ethernet 3", "Realtek USB GbE", dhcp: true, addrs: ["10.2.2.5/24"], gw: "10.2.2.1"),
            Fx.Nic("Ethernet 4", "Intel I225", dhcp: true, addrs: ["169.254.7.7/16"], gw: null),                 // link up, DHCP failed → no real address
            Fx.Nic("Ethernet 5", "Intel I226", dhcp: false, addrs: ["192.168.50.2/24"], gw: null),              // static lab port, no gateway: still watched
            Fx.Nic("WireGuard", "WireGuard Tunnel", physical: false, addrs: ["10.99.0.2/32"], gw: "10.99.0.1"),
            Fx.Nic("Ethernet", "Intel X550", addrs: null, gw: null),                                              // bound to the external switch
            Fx.Nic("vEthernet (UwU)", "Hyper-V Virtual Ethernet Adapter #2", physical: false, dhcp: true, addrs: ["10.10.0.42/24"], gw: "10.10.0.1", hyperv: true),
            Fx.Nic("Ethernet 9", up: false, addrs: ["10.3.3.3/24"], gw: "10.3.3.1"),
        ]);
        var sec = NetPaw.Adapters.AdapterSelector.Secondaries(all, work);
        Assert.Equal(["Ethernet 3", "vEthernet (UwU)", "Ethernet 5"], sec.Select(a => a.Name));   // with gateway first, then by name
    }

    [Fact]
    public async Task SecondaryCheckPingsGatewayOnly()
    {
        var probe = new FakeProbe(); probe.Reachable.Add("10.2.2.1");
        var checker = new ConnectivityChecker(probe);
        var s = new CheckSettings { Enabled = true, InternetTargets = ["1.1.1.1"], DnsCheckHost = "x", CaptiveProbeUrl = "http://x" };
        var dock = Fx.Nic("Ethernet 3", dhcp: true, addrs: ["10.2.2.5/24"], gw: "10.2.2.1");
        var lab = Fx.Nic("Ethernet 5", addrs: ["192.168.50.2/24"], gw: null);
        var dead = Fx.Nic("Ethernet 6", dhcp: true, addrs: ["10.4.4.5/24"], gw: "10.4.4.1");
        var r = await checker.CheckSecondaries([dock, lab, dead], s);
        Assert.Equal(["10.2.2.1", "10.4.4.1"], probe.Pinged);                                  // no internet, DNS or captive probes for secondaries
        Assert.Equal(NetState.GatewayReachable, r[0].State); Assert.False(Snapshot.IsProblem(r[0].State));
        Assert.Equal(NetState.NoGateway, r[1].State);
        Assert.Equal(NetState.NoGateway, r[2].State);
        Assert.All(r, x => Assert.True(x.Partial));
        var off = await checker.CheckSecondaries([dock], new CheckSettings());
        Assert.Empty(probe.Pinged.Skip(2)); Assert.Equal(NetState.ChecksOff, off[0].State);
    }

    [Fact]
    public void AdviseNamesTheAdapterWithoutGateway()
    {
        var dock = Fx.Nic("Ethernet 3", dhcp: true, addrs: ["10.2.2.5/24"], gw: null, dns: ["10.2.2.53"]);
        var snap = new Snapshot(DateTimeOffset.Now, dock, true, true, false, [], [], null, false) { Partial = true };
        var adv = Advisor.Analyze(snap, null);
        Assert.NotEmpty(adv);
        Assert.All(adv, a => Assert.Equal("Ethernet 3", a.Adapter));
        Assert.Contains(adv, a => a.Title.Contains("gateway", StringComparison.OrdinalIgnoreCase));
    }
}
