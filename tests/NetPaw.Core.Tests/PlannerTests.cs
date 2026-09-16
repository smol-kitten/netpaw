using NetPaw.Model;
using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

public class PlannerTests
{
    [Fact]
    public void StaticProfileProducesSetAddAndDnsSteps()
    {
        var p = Fx.Static("lab", "192.168.1.1", "192.168.1.10/24", "10.0.0.5/8");
        var plan = ApplyPlanner.Plan(p, Fx.Adapter(dhcp: true));
        var cmds = plan.Steps.Select(s => s.CommandLine).ToList();
        Assert.Equal([
            "netsh interface ipv4 set address name=\"Ethernet\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=192.168.1.1 gwmetric=1",
            "netsh interface ipv4 add address name=\"Ethernet\" address=10.0.0.5 mask=255.0.0.0",
            "netsh interface ipv4 set dnsservers name=\"Ethernet\" source=static address=1.1.1.1 register=primary validate=no",
            "netsh interface ipv4 add dnsservers name=\"Ethernet\" address=9.9.9.9 index=2 validate=no",
        ], cmds);
        Assert.True(plan.Steps[0].Critical);
        Assert.False(plan.Steps[1].Critical);
    }

    [Fact]
    public void NoGatewayNoDns()
    {
        var p = new Profile { Name = "iso", Addresses = [IpAddr.Parse("192.168.88.250/24")] };
        var plan = ApplyPlanner.Plan(p, Fx.Adapter());
        Assert.Contains("gateway=none", plan.Steps[0].Arguments);
        Assert.Contains(plan.Steps, s => s.Arguments.Contains("address=none"));
    }

    [Fact]
    public void DhcpProfile()
    {
        var plan = ApplyPlanner.Plan(new Profile { Name = "dhcp", Dhcp = true }, Fx.Adapter(dhcp: false));
        Assert.Equal(["netsh interface ipv4 set address name=\"Ethernet\" source=dhcp", "netsh interface ipv4 set dnsservers name=\"Ethernet\" source=dhcp"], plan.Steps.Select(s => s.CommandLine));
        // Issued even when the (possibly stale) snapshot already says DHCP.
        var already = ApplyPlanner.Plan(new Profile { Name = "dhcp", Dhcp = true }, Fx.Adapter(dhcp: true));
        Assert.Equal(2, already.Steps.Count);
        Assert.Empty(already.Warnings);
    }

    [Fact]
    public void RoutesMetricSuffixAndVlan()
    {
        var p = Fx.Static("full");
        p.Routes = [new StaticRoute("10.0.0.0", 8, "192.168.1.1", 5), new StaticRoute("172.16.0.0", 12, null, null)];
        p.InterfaceMetric = 10; p.DnsSuffix = "lab.local"; p.VlanId = 42;
        var plan = ApplyPlanner.Plan(p, Fx.Adapter(), new Adapters.VlanInfo(true, 0, "VlanID"));
        var cmds = plan.Steps.Select(s => s.CommandLine).ToList();
        Assert.Contains("netsh interface ipv4 add route prefix=10.0.0.0/8 interface=\"Ethernet\" nexthop=192.168.1.1 metric=5 store=persistent", cmds);
        Assert.Contains("netsh interface ipv4 delete route prefix=10.0.0.0/8 interface=\"Ethernet\" nexthop=192.168.1.1", cmds);
        Assert.True(plan.Steps.First(s => s.Arguments.Contains("delete route")).ExpectFailure);
        Assert.True(new StepResult(plan.Steps.First(s => s.Arguments.Contains("delete route")), 1, "no such route").Ok);
        Assert.Contains("netsh interface ipv4 add route prefix=172.16.0.0/12 interface=\"Ethernet\" store=persistent", cmds);
        Assert.Contains("netsh interface ipv4 set interface \"Ethernet\" metric=10", cmds);
        Assert.Contains(cmds, c => c.StartsWith("powershell") && c.Contains("Set-DnsClient") && c.Contains("lab.local"));
        Assert.Contains(cmds, c => c.Contains("Set-NetAdapterAdvancedProperty") && c.Contains("-RegistryValue 42"));
    }

    [Fact]
    public void VlanSkippedWhenDriverCannot()
    {
        var p = Fx.Static("v"); p.VlanId = 42;
        var plan = ApplyPlanner.Plan(p, Fx.Adapter(), new Adapters.VlanInfo(false, null, null));
        Assert.DoesNotContain(plan.Steps, s => s.Arguments.Contains("VlanID"));
        Assert.Contains(plan.Warnings, w => w.Contains("no VlanID"));
        var same = ApplyPlanner.Plan(p, Fx.Adapter(), new Adapters.VlanInfo(true, 42, "VlanID"));
        Assert.DoesNotContain(same.Steps, s => s.Arguments.Contains("VlanID"));
    }

    [Fact]
    public void AdapterNameIsQuoted()
    {
        var plan = ApplyPlanner.Plan(Fx.Static("x"), Fx.Adapter("Ethernet 2 (lab)"));
        Assert.All(plan.Steps.Where(s => s.FileName == "netsh"), s => Assert.Contains("name=\"Ethernet 2 (lab)\"", s.Arguments));
    }

    [Fact]
    public void SecondaryPlanRefusesOnDhcp()
    {
        var ok = ApplyPlanner.PlanAddSecondary(Fx.Adapter(), IpAddr.Parse("10.1.1.250/24"), "t");
        Assert.Single(ok.Steps);
        var dup = ApplyPlanner.PlanAddSecondary(Fx.Adapter(), IpAddr.Parse("192.168.1.10/24"), "t");
        Assert.Empty(dup.Steps);
        var dhcp = ApplyPlanner.PlanAddSecondary(Fx.Adapter(dhcp: true), IpAddr.Parse("10.1.1.250/24"), "t");
        Assert.Empty(dhcp.Steps); Assert.NotEmpty(dhcp.Warnings);
    }

    [Fact]
    public void MatchesDetectsCurrentProfile()
    {
        var a = Fx.Adapter(addrs: ["192.168.1.10/24", "10.0.0.5/8"], gw: "192.168.1.1");
        Assert.True(ApplyPlanner.Matches(Fx.Static("p", "192.168.1.1", "192.168.1.10/24"), a));
        Assert.True(ApplyPlanner.Matches(Fx.Static("p", "192.168.1.1", "192.168.1.10/24", "10.0.0.5/8"), a));
        Assert.False(ApplyPlanner.Matches(Fx.Static("p", "192.168.1.1", "10.0.0.5/8", "192.168.1.10/24"), a)); // primary differs
        Assert.False(ApplyPlanner.Matches(Fx.Static("p", null, "192.168.1.10/24"), a));
        Assert.False(ApplyPlanner.Matches(Fx.Static("p", "192.168.1.1", "192.168.1.10/25"), a));
        Assert.True(ApplyPlanner.Matches(new Profile { Dhcp = true }, Fx.Adapter(dhcp: true)));
    }

    [Fact]
    public void ExecutorAbortsOnCriticalFailureOnly()
    {
        var p = Fx.Static("lab", "192.168.1.1", "192.168.1.10/24", "10.0.0.5/8");
        var plan = ApplyPlanner.Plan(p, Fx.Adapter());
        var soft = new FakeRunner(failContaining: "add address");
        var o1 = PlanExecutor.Execute(plan, soft);
        Assert.True(o1.Success); Assert.False(o1.Aborted); Assert.Equal(plan.Steps.Count, soft.Ran.Count); Assert.Single(o1.Failures);
        var hard = new FakeRunner(failContaining: "set address");
        var o2 = PlanExecutor.Execute(plan, hard);
        Assert.False(o2.Success); Assert.True(o2.Aborted); Assert.Single(hard.Ran);
    }

    [Fact]
    public void ProfileValidation()
    {
        var p = Fx.Static("ok");
        Assert.Empty(p.Validate());
        p.Gateway = "10.9.9.9";
        Assert.Contains(p.Validate(), e => e.Contains("not inside"));
        p.Gateway = "192.168.1.1"; p.Hotkey = "Q"; p.VlanId = 5000; p.Dns.Add("nope");
        var errs = p.Validate();
        Assert.Equal(3, errs.Count);
        Assert.NotEmpty(new Profile { Name = "s" }.Validate());
        Assert.Empty(new Profile { Name = "d", Dhcp = true }.Validate());
    }
}

public class V07PlannerTests
{
    [Fact]
    public void PlansThatChangeDnsAreFlaggedAndTheServiceAppendsOneFlush()
    {
        Assert.True(ApplyPlanner.Plan(Fx.Static("x"), Fx.Adapter()).ChangesDns);
        Assert.True(ApplyPlanner.PlanDhcp(Fx.Adapter()).ChangesDns);
        Assert.True(NetPaw.Connectivity.Advisor.PlanRenew(Fx.Adapter(dhcp: true)).ChangesDns);
        Assert.True(ApplyPlanner.PlanResetAdapter(Fx.Adapter()).ChangesDns);
        Assert.False(ApplyPlanner.PlanAddSecondary(Fx.Adapter(), IpAddr.Parse("10.1.1.250/24"), "t").ChangesDns);
        Assert.False(ApplyPlanner.Plan(Fx.Static("x"), Fx.Adapter()).Steps.Any(s => s.FileName == "ipconfig"));
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeRunner();
        var store = new NetPaw.Store.JsonStore(dir); store.SaveSettings(new Settings { FlushDns = true });
        var svc = new NetPawService(store, new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), runner, new NetPaw.Store.MachineStore(Path.Combine(dir, "profiles.d")), () => NetPaw.Store.Policy.None);
        svc.Apply(ApplyPlanner.PlanDhcp(Fx.Adapter()));
        Assert.Equal("ipconfig /flushdns", runner.Ran.Last().CommandLine); Assert.False(runner.Ran.Last().Critical);
        Assert.Equal(1, runner.Ran.Count(s => s.Arguments == "/flushdns"));
        svc.Settings.FlushDns = false; runner.Ran.Clear();
        svc.Apply(ApplyPlanner.PlanDhcp(Fx.Adapter()));
        Assert.DoesNotContain(runner.Ran, s => s.FileName == "ipconfig");
    }

    [Fact]
    public void ResetAdapterIsTwoStepsAndWarnsOnDefaultRoute()
    {
        var plan = ApplyPlanner.PlanResetAdapter(Fx.Adapter("Wi-Fi", gw: "192.168.1.1"));
        Assert.Equal(["netsh interface set interface \"Wi-Fi\" admin=disable", "netsh interface set interface \"Wi-Fi\" admin=enable"], plan.Steps.Select(s => s.CommandLine));
        Assert.Single(plan.Warnings);
        Assert.Empty(ApplyPlanner.PlanResetAdapter(Fx.Adapter("Ethernet 2")).Warnings);
    }
}
