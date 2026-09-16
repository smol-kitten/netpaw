using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

static class R { public static StaticRoute P(string t) => StaticRoute.TryParse(t, out var r) ? r : throw new FormatException(t); }

public class RouteTableTests
{
    const string ShowRoute = """

        Publish  Type      Met  Prefix                    Idx  Gateway/Interface Name
        -------  --------  ---  ------------------------  ---  ------------------------
        No       Manual    256  0.0.0.0/0                  12  192.168.1.1
        No       System    256  127.0.0.0/8                 1  Loopback Pseudo-Interface 1
        No       Manual    10   10.20.0.0/16               12  192.168.1.254
        No       System    256  192.168.1.0/24             12  Ethernet
        No       Manual    5    10.30.0.5/32                7  10.9.9.1
        """;
    const string ShowInterfaces = """

        Idx     Met         MTU          State                Name
        ---  ----------  ----------  ------------  ---------------------------
          1          75  4294967295  connected     Loopback Pseudo-Interface 1
         12          25        1500  connected     Ethernet
          7          35        1500  connected     Ethernet 2
        """;

    [Fact]
    public void RouteTableParsesNetshOutput()
    {
        var r = RouteTable.Parse(ShowRoute);
        Assert.Equal(5, r.Count);
        Assert.Equal(new RouteEntry("10.20.0.0/16", 12, "192.168.1.254", 10), r[2]);
        Assert.Null(r[3].Gateway);                       // on-link route names the interface, not a hop
        Assert.Equal(new RouteEntry("10.30.0.5/32", 7, "10.9.9.1", 5), r[4]);
        var m = RouteTable.ParseInterfaceMetrics(ShowInterfaces);
        Assert.Equal(25, m[12]); Assert.Equal(35, m[7]);
        Assert.Empty(RouteTable.Parse("")); Assert.Empty(RouteTable.ParseInterfaceMetrics("garbage\nmore garbage"));
    }

    [Fact]
    public void VerifierReportsMissingRoute()
    {
        var p = Fx.Static("lab", "192.168.1.1", "192.168.1.10/24"); p.Dns = ["192.168.1.1"];
        p.Routes = [R.P("10.20.0.0/16 via 192.168.1.254 metric 10"), R.P("10.40.0.0/16 via 192.168.1.254")];
        var live = Fx.Adapter(addrs: ["192.168.1.10/24"], gw: "192.168.1.1");
        var routes = RouteTable.Parse(ShowRoute).Where(r => r.InterfaceIndex == 12).ToList();
        var diffs = ApplyVerifier.Compare(p, live, strict: true, routes);
        Assert.Contains("route 10.40.0.0/16 missing", diffs);
        Assert.DoesNotContain(diffs, d => d.Contains("10.20.0.0/16"));
        Assert.Empty(ApplyVerifier.Compare(p, live, strict: true, routes: null));            // not read = not judged
        p.Routes[0] = R.P("10.20.0.0/16 via 192.168.1.253 metric 20");
        diffs = ApplyVerifier.Compare(p, live, strict: true, routes);
        Assert.Contains("route 10.20.0.0/16 goes via 192.168.1.254, expected 192.168.1.253", diffs);
        Assert.Contains("route 10.20.0.0/16 has metric 10, expected 20", diffs);
        Assert.Empty(ApplyVerifier.Compare(p, live, strict: false, routes));                // menu ticks never look at routes
    }

    [Fact]
    public void VerifierReportsInterfaceMetric()
    {
        var p = Fx.Static("lab", "192.168.1.1", "192.168.1.10/24"); p.Dns = ["192.168.1.1"]; p.InterfaceMetric = 10;
        var live = Fx.Adapter(addrs: ["192.168.1.10/24"], gw: "192.168.1.1");
        Assert.Contains("interface metric is 25, expected 10", ApplyVerifier.Compare(p, live, strict: true, [], 25));
        Assert.Empty(ApplyVerifier.Compare(p, live, strict: true, [], 10));
        Assert.Empty(ApplyVerifier.Compare(p, live, strict: true, [], null));
    }

    [Fact]
    public void ServiceVerifiesEveryPlanWithAProfile()
    {
        // DHCP plan on an adapter that stays static: the diff surfaces without any Tray/CLI code path involved.
        var dir = Directory.CreateTempSubdirectory("netpaw-verify");
        try
        {
            var stuck = Fx.Adapter(dhcp: false, addrs: ["192.168.1.10/24"], gw: "192.168.1.1");
            var svc = new NetPawService(new JsonStore(dir.FullName), new FakeAdapters(stuck), new FakeVlan(false), new FakeRunner());
            var o = svc.ApplyAndVerify(ApplyPlanner.PlanDhcp(stuck), settleMs: 0);
            Assert.True(o.Success);
            Assert.Contains("adapter still reports a static configuration", o.VerifyDiffs);

            // Static plan with a route: the service asks the route reader for that interface only.
            int? asked = null;
            svc.RouteReader = idx => { asked = idx; return ([new RouteEntry("10.20.0.0/16", 7, "192.168.1.254", 10)], 10); };
            var p = Fx.Static("lab", "192.168.1.1", "192.168.1.10/24"); p.Dns = ["192.168.1.1"]; p.Routes = [R.P("10.20.0.0/16 via 192.168.1.254")]; p.InterfaceMetric = 10;
            o = svc.ApplyAndVerify(svc.PlanProfile(p, stuck), settleMs: 0);
            Assert.Equal(7, asked); Assert.Empty(o.VerifyDiffs);

            // No routes/metric in the profile: the reader is never consulted (no netsh spawn for plain profiles).
            asked = null;
            var plain = Fx.Static("plain", "192.168.1.1", "192.168.1.10/24"); plain.Dns = ["192.168.1.1"];
            o = svc.ApplyAndVerify(svc.PlanProfile(plain, stuck), settleMs: 0);
            Assert.Null(asked); Assert.Empty(o.VerifyDiffs);

            // Reset has no profile: nothing to verify against, no diffs invented.
            Assert.Empty(svc.ApplyAndVerify(ApplyPlanner.PlanResetAdapter(stuck), settleMs: 0).VerifyDiffs);
        }
        finally { dir.Delete(true); }
    }
}

public class SelectorTests
{
    [Fact]
    public void PickPrefersDockOverWifi()
    {
        var wifi = Fx.Nic("Wi-Fi", "Intel AX211", wireless: true, dhcp: true, addrs: ["10.1.1.5/24"], gw: "10.1.1.1");
        var dock = Fx.Nic("Ethernet 3", "Realtek USB GbE", dhcp: true, addrs: ["10.2.2.5/24"], gw: "10.2.2.1");
        Assert.Equal("Ethernet 3", AdapterSelector.Pick([wifi, dock], null)!.Name);
        Assert.Equal("Wi-Fi", AdapterSelector.Pick([wifi, dock with { Up = false }], null)!.Name);
        Assert.Equal("Wi-Fi", AdapterSelector.Pick([wifi, dock], "wi-fi")!.Name);                      // explicit choice wins, case-insensitive
    }

    [Fact]
    public void PickSkipsVSwitchUplink()
    {
        var all = AdapterSelector.TagVSwitchUplinks(
        [
            Fx.Nic("Ethernet", "Intel X550", addrs: null, gw: null),                                              // bound to the external switch
            Fx.Nic("vEthernet (UwU)", "Hyper-V Virtual Ethernet Adapter #2", physical: false, dhcp: true, addrs: ["10.10.0.42/24"], gw: "10.10.0.1", hyperv: true),
            Fx.Nic("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", physical: false, addrs: ["172.30.0.1/28"], gw: null, hyperv: true),
        ]);
        Assert.True(all[0].VSwitchUplink);
        Assert.Equal("vEthernet (UwU)", AdapterSelector.Pick(all, null)!.Name);
    }

    [Fact]
    public void PickIgnoresVpnTunnel()
    {
        var vpn = Fx.Nic("WireGuard Tunnel", "WireGuard Tunnel", physical: false, addrs: ["10.99.0.2/32"], gw: "10.99.0.1");
        var tap = Fx.Nic("OpenVPN TAP", "TAP-Windows Adapter V9", physical: false, addrs: ["10.8.0.6/24"], gw: "10.8.0.1");
        var nic = Fx.Nic("Ethernet", "Intel I219", dhcp: true, addrs: ["192.168.1.20/24"], gw: null);            // up, no gateway yet
        Assert.Equal("Ethernet", AdapterSelector.Pick([vpn, tap, nic], null)!.Name);
        Assert.Null(AdapterSelector.Pick([vpn, tap], null));                                                    // nothing host-facing at all
    }
}
