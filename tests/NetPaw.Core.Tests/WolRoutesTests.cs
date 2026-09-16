using System.Net;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

sealed class FakeWol : IWol
{
    public List<(byte[] Packet, IReadOnlyList<IPEndPoint> Targets)> Sent { get; } = [];
    public Task Send(byte[] packet, IReadOnlyList<IPEndPoint> targets, CancellationToken ct) { Sent.Add((packet, targets)); return Task.CompletedTask; }
}

public class WolRoutesTests
{
    [Theory]
    [InlineData("bc:24:11:2f:f7:33", true)]
    [InlineData("BC-24-11-2F-F7-33", true)]
    [InlineData("bc24.112f.f733", true)]
    [InlineData("bc2411 2ff733", false)]
    [InlineData("bc:24:11:2f:f7", false)]
    [InlineData("zz:24:11:2f:f7:33", false)]
    public void WolParsesMacForms(string text, bool ok)
    {
        Assert.Equal(ok, WakeOnLan.TryParseMac(text, out var mac));
        if (ok) Assert.Equal(new byte[] { 0xBC, 0x24, 0x11, 0x2F, 0xF7, 0x33 }, mac);
    }

    [Fact]
    public async Task MagicPacketLayoutAndTargets()
    {
        var wol = new FakeWol();
        await WakeOnLan.Wake(wol, "bc:24:11:2f:f7:33", IpAddr.Parse("10.10.20.50/24"));
        var (packet, targets) = Assert.Single(wol.Sent);
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(new byte[] { 0xBC, 0x24, 0x11, 0x2F, 0xF7, 0x33 }, packet.Skip(6 + i * 6).Take(6));
        Assert.Equal(["255.255.255.255:9", "10.10.20.255:9"], targets.Select(t => t.ToString()));
        Assert.Single(WakeOnLan.Targets(null));                                                     // no subnet known: global only
        Assert.Single(WakeOnLan.Targets(IpAddr.Parse("10.0.0.1/32")));                              // /32 has no broadcast
        await Assert.ThrowsAsync<FormatException>(() => WakeOnLan.Wake(wol, "nope", null));
    }

    const string ShowRoute = """
        Publish  Type      Met  Prefix                    Idx  Gateway/Interface Name
        -------  --------  ---  ------------------------  ---  ------------------------
        No       Manual    256  0.0.0.0/0                  12  192.168.1.1
        No       Manual    35   0.0.0.0/0                  12  10.8.0.1
        No       System    256  192.168.1.0/24             12  Ethernet
        No       Manual    5    10.30.0.0/16                7  10.9.9.1
        """;

    [Fact]
    public void RouteEntriesCarryTypeAndPlanDeleteIsOneStep()
    {
        var r = RouteTable.Parse(ShowRoute);
        Assert.True(r[0].Manual); Assert.True(r[0].Default); Assert.False(r[2].Manual); Assert.Equal("Ethernet", r[2].InterfaceName);
        var plan = ApplyPlanner.PlanDeleteRoute(r[1], "Ethernet");
        var step = Assert.Single(plan.Steps);
        Assert.Equal("interface ipv4 delete route prefix=0.0.0.0/0 interface=12 nexthop=10.8.0.1", step.Arguments);
        Assert.Equal("interface ipv4 delete route prefix=192.168.1.0/24 interface=12", Assert.Single(ApplyPlanner.PlanDeleteRoute(r[2], "Ethernet").Steps).Arguments);
        Assert.Equal(4, RouteTable.ReadAll(new ScriptedRunner(ShowRoute)).Count);
    }

    [Fact]
    public void AdvisorFlagsTheSecondManualDefaultRoute()
    {
        var nic = Fx.Nic(dhcp: false, addrs: ["192.168.1.10/24"], gw: "192.168.1.1", dns: ["192.168.1.1"], index: 12);
        var snap = new Snapshot(DateTimeOffset.Now, nic, true, true, true, [], [], null, false) { Routes = RouteTable.Parse(ShowRoute) };
        var adv = Assert.Single(Advisor.Analyze(snap, null), x => x.Title.StartsWith("Two default routes"));
        Assert.Equal(RepairKind.DeleteRoute, adv.Repair); Assert.Equal("192.168.1.1", adv.Route!.Gateway); Assert.Equal(256, adv.Route.Metric);   // the higher metric one is the leftover
        Assert.Contains("via 10.8.0.1 (metric 35)", adv.Text);
        var other = snap with { Adapter = nic with { Index = 7 } };
        Assert.DoesNotContain(Advisor.Analyze(other, null), x => x.Title.StartsWith("Two default"));                        // routes belong to another interface
        Assert.DoesNotContain(Advisor.Analyze(snap with { Routes = null }, null), x => x.Title.StartsWith("Two default"));   // table not read
    }
}

/// <summary>Runner that answers every step with one canned output.</summary>
sealed class ScriptedRunner(string output) : IStepRunner
{
    public StepResult Run(Step step) => new(step, 0, output);
}
