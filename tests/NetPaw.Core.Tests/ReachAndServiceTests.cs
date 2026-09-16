using NetPaw.Model;
using NetPaw.Presets;
using NetPaw.Reach;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class ReachResolverTests
{
    static readonly IReadOnlyList<Preset> Presets = PresetLibrary.LoadBundled();

    [Fact]
    public void AlreadyReachable()
    {
        var d = ReachResolver.Resolve("192.168.1.1", Fx.Adapter(), [], Presets);
        Assert.Equal(ReachKind.AlreadyReachable, d.Kind);
    }

    [Fact]
    public void PrefersProfileMostSpecificSubnet()
    {
        var wide = Fx.Static("wide", null, "10.0.0.5/8");
        var narrow = Fx.Static("narrow", null, "10.20.30.5/24");
        var routed = Fx.Static("routed"); routed.Routes = [new StaticRoute("10.0.0.0", 8, "192.168.1.1", null)];
        var d = ReachResolver.Resolve("10.20.30.1", Fx.Adapter(), [routed, wide, narrow], Presets);
        Assert.Equal(ReachKind.UseProfile, d.Kind);
        Assert.Equal("narrow", d.Profile!.Name);
        var viaRoute = ReachResolver.Resolve("10.99.0.1", Fx.Adapter(), [routed], Presets);
        Assert.Equal("routed", viaRoute.Profile!.Name);
    }

    [Fact]
    public void TemporaryProfilesAndDhcpAreNotCandidates()
    {
        var t = Fx.Static("tmp", null, "10.20.30.5/24"); t.Temporary = true;
        var d = ReachResolver.Resolve("10.20.30.1", Fx.Adapter(), [t, new Profile { Name = "dhcp", Dhcp = true }], Presets);
        Assert.Equal(ReachKind.TempAddress, d.Kind);
    }

    [Fact]
    public void PresetKnowsTheSubnet()
    {
        var d = ReachResolver.Resolve("10.90.90.90", Fx.Adapter(), [], Presets);
        Assert.Equal(ReachKind.UsePreset, d.Kind);
        Assert.Equal("D-Link", d.Preset!.Vendor);
        Assert.Equal(8, d.Address!.PrefixLength);
        Assert.Equal("10.255.255.254", d.Address.Address);
    }

    [Fact]
    public void UnknownTargetGetsTopOfSlash24AvoidingKnownAddresses()
    {
        var p = Fx.Static("x", null, "172.20.5.254/24");
        var d = ReachResolver.Resolve("172.20.5.1", Fx.Adapter(addrs: ["172.20.5.253/24"]), [], Presets);
        Assert.Equal(ReachKind.AlreadyReachable, d.Kind);
        d = ReachResolver.Resolve("172.20.5.1", Fx.Adapter(), [p], Presets);
        Assert.Equal(ReachKind.UseProfile, d.Kind); // a saved profile in that subnet wins
        d = ReachResolver.Resolve("172.20.5.1/24", Fx.Adapter(), [p], Presets); // explicit prefix = "just put me there"
        Assert.Equal(ReachKind.TempAddress, d.Kind);
        Assert.Equal(new IpAddr("172.20.5.253", 24), d.Address); // .254 is taken by the profile
    }

    [Fact]
    public void ExplicitPrefixOverridesEverything()
    {
        var p = Fx.Static("x", null, "10.0.0.5/8");
        var d = ReachResolver.Resolve("10.20.30.1/28", Fx.Adapter(), [p], Presets);
        Assert.Equal(ReachKind.TempAddress, d.Kind);
        Assert.Equal(new IpAddr("10.20.30.14", 28), d.Address);
    }

    [Fact]
    public void InvalidInput()
    {
        Assert.Equal(ReachKind.Invalid, ReachResolver.Resolve("switch01", Fx.Adapter(), [], Presets).Kind);
        Assert.Equal(ReachKind.Invalid, ReachResolver.Resolve("10.0.0.1/32", Fx.Adapter(), [], Presets).Kind);
    }
}

public class PresetTests
{
    [Fact]
    public void BundledPresetsAreSane()
    {
        var all = PresetLibrary.LoadBundled();
        Assert.True(all.Count > 40);
        Assert.All(all, p => { Assert.True(Net.IpMath.IsIPv4(p.Ip), p.Key); Assert.InRange(p.Prefix, 8, 30); Assert.NotEmpty(p.Vendor); Assert.NotEmpty(p.Model); });
        Assert.Equal(all.Count, all.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UserPresetOverridesBundled()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "presets.json");
        File.WriteAllText(file, """[{"vendor":"MikroTik","model":"RouterOS (any)","ip":"10.0.0.1","prefix":24},{"vendor":"Me","model":"Box","ip":"10.1.1.1"}]""");
        var all = PresetLibrary.Load(file);
        Assert.Equal("10.0.0.1", all.Single(p => p.Vendor == "MikroTik").Ip);
        Assert.Contains(all, p => p.Vendor == "Me");
        Assert.Equal(2, PresetLibrary.Search(all, "mikro").Count() + PresetLibrary.Search(all, "10.1.1").Count());
    }

    [Fact]
    public void PresetProfilePicksHostAndGateway()
    {
        var p = PresetLibrary.LoadBundled().First(p => p.Vendor == "MikroTik").ToProfile("Ethernet", ["192.168.88.254"]);
        Assert.Equal("192.168.88.253", p.Primary!.Address);
        Assert.Equal("192.168.88.1", p.Gateway);
        Assert.True(p.Temporary);
        Assert.Empty(p.Validate());
    }
}

public class ServiceTests
{
    static NetPawService Make(FakeRunner runner, params Adapters.AdapterInfo[] adapters)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        return new NetPawService(new JsonStore(dir), new FakeAdapters(adapters.Length == 0 ? [Fx.Adapter(gw: "192.168.1.1")] : adapters), new FakeVlan(false), runner);
    }

    [Fact]
    public void ProfilesRoundTripThroughStore()
    {
        var svc = Make(new FakeRunner());
        var p = Fx.Static("lab"); p.Routes = [new StaticRoute("10.0.0.0", 8, "192.168.1.1", 3)]; p.Hotkey = "Ctrl+Alt+1"; p.VlanId = 7;
        svc.Profiles.Add(p); svc.SaveProfiles();
        svc.Reload();
        var back = svc.FindProfile("lab")!;
        Assert.Equal(p.Addresses, back.Addresses); Assert.Equal(p.Routes, back.Routes); Assert.Equal(7, back.VlanId); Assert.Equal("Ctrl+Alt+1", back.Hotkey);
        Assert.NotNull(svc.FindProfile("la"));
        Assert.Null(svc.FindProfile("zzz"));
    }

    [Fact]
    public void ReachTempAddressIsTrackedAndCleared()
    {
        var runner = new FakeRunner();
        var svc = Make(runner);
        var d = svc.ResolveReach("172.20.5.1");
        var (plan, outcome) = svc.Reach(d);
        Assert.True(outcome!.Success);
        Assert.Contains("add address", plan.Steps.Single().Arguments);
        Assert.Single(svc.TempAddresses);
        svc.ClearTemp();
        Assert.Contains(runner.Ran, s => s.Arguments.Contains("delete address") && s.Arguments.Contains("172.20.5.254"));
        Assert.Empty(svc.TempAddresses);
    }

    [Fact]
    public void ReachOnDhcpAdapterFallsBackToReplace()
    {
        var svc = Make(new FakeRunner(), Fx.Adapter(dhcp: true));
        var (plan, outcome) = svc.Reach(svc.ResolveReach("192.168.88.1"));
        Assert.Contains("set address", plan.Steps[0].Arguments);
        Assert.Contains("gateway=192.168.88.1", plan.Steps[0].Arguments); // preset knowledge used
        Assert.True(outcome!.Success);
        Assert.Empty(svc.TempAddresses); // full replace: nothing to "clear" later
    }

    [Fact]
    public void ApplyingAProfileForgetsTempAddressesOnThatAdapter()
    {
        var svc = Make(new FakeRunner());
        svc.Reach(svc.ResolveReach("172.20.5.1"));
        Assert.Single(svc.TempAddresses);
        svc.ApplyProfile(Fx.Static("lab"));
        Assert.Empty(svc.TempAddresses);
    }

    [Fact]
    public void CaptureSnapshotsAdapter()
    {
        var svc = Make(new FakeRunner());
        var p = svc.Capture("now", svc.WorkAdapter()!);
        Assert.Equal("192.168.1.10/24", p.Primary!.ToString()); Assert.Equal("192.168.1.1", p.Gateway); Assert.Equal(["192.168.1.1"], p.Dns);
    }

    [Fact]
    public void UnknownAdapterNameThrowsListingOptions()
    {
        var svc = Make(new FakeRunner());
        var ex = Assert.Throws<InvalidOperationException>(() => svc.ResolveAdapter("Wi-Fi 9"));
        Assert.Contains("Ethernet", ex.Message);
    }
}

public class PresetPrecedenceTests
{
    [Fact]
    public void ExplicitPresetBeatsRoutedProfile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var routed = Fx.Static("routed"); routed.Routes = [new StaticRoute("172.16.0.0", 12, "192.168.1.1", null)];
        var svc = new NetPawService(new JsonStore(dir), new FakeAdapters(Fx.Adapter()), new FakeVlan(false), new FakeRunner());
        svc.Profiles.Add(routed);
        var sophos = svc.Presets.First(p => p.Ip == "172.16.16.16");
        Assert.Equal(ReachKind.UseProfile, svc.ResolveReach(sophos.Ip).Kind);       // generic reach: the route is fine
        var (plan, outcome) = svc.ApplyPreset(sophos);                                // explicit preset: go on-link
        Assert.Equal(ReachKind.UsePreset, svc.ResolvePreset(sophos, svc.WorkAdapter()!).Kind);
        Assert.Contains("172.16.16.254", plan.Steps.Single().Arguments);
        Assert.True(outcome!.Success);
        Assert.Single(svc.TempAddresses);
    }

    [Fact]
    public void FilterDriverPseudoInterfacesAreSkipped()
    {
        Assert.Matches(@"-\d{4}$", "Ethernet 2-QoS Packet Scheduler-0000");
        Assert.DoesNotMatch(@"-\d{4}$", "Ethernet 2");
    }
}

public class BorrowTests
{
    static (NetPawService, FakeRunner) Make(params Adapters.AdapterInfo[] adapters)
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var r = new FakeRunner();
        return (new NetPawService(new JsonStore(dir), new FakeAdapters(adapters), new FakeVlan(false), r, new MachineStore(Path.Combine(dir, "profiles.d")), () => Policy.None), r);
    }

    [Fact]
    public void StaticAdapterBorrowsASecondaryAndReturnsIt()
    {
        var (svc, r) = Make(Fx.Adapter(gw: "192.168.1.1"));
        Assert.True(svc.Borrow("Ethernet", IpAddr.Parse("10.5.0.250/24")));
        Assert.Contains(r.Ran, s => s.Arguments.Contains("add address") && s.Arguments.Contains("10.5.0.250"));
        svc.Return("Ethernet", IpAddr.Parse("10.5.0.250/24"));
        Assert.Contains(r.Ran, s => s.Arguments.Contains("delete address") && s.Arguments.Contains("10.5.0.250"));
        svc.Return("Ethernet", IpAddr.Parse("10.5.0.250/24"));   // idempotent
    }

    [Fact]
    public void WorkingLeaseIsNeverDroppedUnlessAccepted()
    {
        var leased = Fx.Adapter(dhcp: true, addrs: ["10.5.10.20/8"], gw: "10.0.0.1");
        var (svc, r) = Make(leased);
        Assert.True(NetPawService.BorrowDropsLease(leased));
        Assert.False(svc.Borrow("Ethernet", IpAddr.Parse("192.168.88.250/24")));
        Assert.Empty(r.Ran);
        Assert.True(svc.Borrow("Ethernet", IpAddr.Parse("192.168.88.250/24"), allowLeaseDrop: true));
        Assert.Contains(r.Ran, s => s.Arguments.Contains("source=static") && s.Arguments.Contains("192.168.88.250"));
        svc.Return("Ethernet", IpAddr.Parse("192.168.88.250/24"));
        Assert.Contains(r.Ran, s => s.Arguments.Contains("source=dhcp"));
        // a lease-less DHCP adapter (169.254) is fair game without the flag
        var apipa = Fx.Adapter(dhcp: true, addrs: ["169.254.3.3/16"]);
        var (svc2, _) = Make(apipa);
        Assert.False(NetPawService.BorrowDropsLease(apipa));
        Assert.True(svc2.Borrow("Ethernet", IpAddr.Parse("192.168.88.250/24")));
    }
}

public class MacBindingTests
{
    static NetPawService Make(params Adapters.AdapterInfo[] adapters)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        return new NetPawService(new JsonStore(dir), new FakeAdapters(adapters), new FakeVlan(false), new FakeRunner(), new MachineStore(Path.Combine(dir, "profiles.d")), () => Policy.None);
    }

    [Fact]
    public void MacWinsOverNameThenNameThenWorkAdapter()
    {
        var eth = Fx.Adapter("Ethernet", gw: "192.168.1.1") with { Mac = "AA:BB:CC:00:00:01" };
        var dock = Fx.Adapter("Ethernet 3", addrs: ["10.5.0.9/24"]) with { Mac = "AA:BB:CC:00:00:99" };
        var svc = Make(eth, dock);
        var p = Fx.Static("dock"); p.Adapter = "Ethernet"; p.AdapterMac = "aa:bb:cc:00:00:99";   // renamed dock NIC: MAC says Ethernet 3
        Assert.Equal("Ethernet 3", svc.AdapterFor(p).Name);
        p.AdapterMac = "AA:BB:CC:FF:FF:FF";                                                      // MAC gone, name still there
        Assert.Equal("Ethernet", svc.AdapterFor(p).Name);
        p.Adapter = "Ethernet 9";                                                                // neither present: refuse, never silently reconfigure another NIC
        Assert.Throws<InvalidOperationException>(() => svc.AdapterFor(p));
        p.Adapter = null; p.AdapterMac = null;
        Assert.Equal("Ethernet", svc.AdapterFor(p).Name);                                      // unbound: work adapter
        // Hyper-V: vEthernet shares the uplink's MAC; never resolve onto the uplink
        var uplink = Fx.Adapter("Ethernet", addrs: []) with { Mac = "AA:BB:CC:00:00:77", VSwitchUplink = true };
        var veth = Fx.Adapter("vEthernet (External)", gw: "10.0.0.1") with { Mac = "AA:BB:CC:00:00:77", IsPhysical = false, HyperVVirtual = true };
        var hv = Make(uplink, veth);
        var hp = Fx.Static("hv"); hp.Adapter = "vEthernet (External)"; hp.AdapterMac = "AA:BB:CC:00:00:77";
        Assert.Equal("vEthernet (External)", hv.AdapterFor(hp).Name);
        var cap = svc.Capture("c", dock);
        Assert.Equal("AA:BB:CC:00:00:99", cap.AdapterMac);
        Assert.Equal("Ethernet 3", svc.PlanProfile(cap).Adapter);
    }
}
