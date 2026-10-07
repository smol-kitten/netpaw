using System.Text;
using NetPaw.Ipc;
using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

/// <summary>The service side of change requests (docs/PLAN-v0.14.md, "IPC"): requests are intents, every value
/// that reaches a command line is checked, and the service builds the commands itself.</summary>
public class IntentHandlerTests
{
    sealed record Rig(IntentHandler Handler, FakeRunner Runner, string Dir, List<string> Log);

    static Rig Make(Dictionary<string, object>? policy = null, string? managedJson = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var machine = Path.Combine(dir, "profiles.d");
        if (managedJson is not null) { Directory.CreateDirectory(machine); File.WriteAllText(Path.Combine(machine, "corp.json"), managedJson); }
        var runner = new FakeRunner();
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), runner,
            new MachineStore(machine), () => PolicyReader.Read(new DictionaryPolicySource(policy ?? [])));
        var log = new List<string>();
        return new Rig(new IntentHandler(svc, Path.Combine(dir, "service"), log.Add, settleMs: 0), runner, dir, log);
    }

    static ServiceRequest Req(RequestKind k) => new(ServiceProtocol.Version, k);

    [Fact]
    public void Inline_profile_is_planned_by_the_service()
    {
        var r = Make();
        var resp = r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = Fx.Static("lab", "192.168.1.1", "192.168.1.50/24") }, "PC\\anna (session 2)", privileged: true);
        Assert.True(resp.Ok, resp.Error);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("set address name=\"Ethernet\" source=static address=192.168.1.50"));
        Assert.Equal("Ethernet", resp.Apply!.Adapter);
        Assert.Contains(r.Log, l => l.Contains("PC\\anna"));
        Assert.False(File.Exists(r.Handler.JournalPath));
    }

    [Theory]
    [InlineData("corp.local' ; Remove-Item C:\\ -Recurse ; '")]
    [InlineData("corp.local\u2019; calc; \u2018")]      // PowerShell reads ’ and ‘ as single quotes too
    [InlineData("a b")]
    public void Dns_suffix_that_is_not_a_dns_name_is_refused(string suffix)
    {
        var r = Make();
        var p = Fx.Static("x"); p.DnsSuffix = suffix;
        var resp = r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = p }, "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("DNS suffix", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Theory]
    [InlineData("10.0.0.1 store=active", null)]
    [InlineData("10.0.0.0", "1.2.3.4 metric=1")]
    [InlineData("::1", null)]
    public void Route_fields_that_are_not_ipv4_are_refused(string dest, string? gw)
    {
        var r = Make();
        var p = Fx.Static("x"); p.Routes = [new StaticRoute(dest, 8, gw, null)];
        var resp = r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = p }, "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("Route", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Address_objects_are_checked_too()
    {
        var r = Make();
        var p = Fx.Static("x"); p.Addresses = [new IpAddr("192.168.1.5 mask=0.0.0.0", 24)];
        Assert.False(r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = p }, "u", privileged: true).Ok);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Unknown_adapter_is_refused_with_the_list()
    {
        var r = Make();
        var resp = r.Handler.Handle(Req(RequestKind.Dhcp) with { Adapter = "Ethernet\" source=static" }, "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("not found", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Policy_is_enforced_by_the_service()
    {
        var r = Make(new() { ["AllowDhcp"] = 0, ["AllowUserProfiles"] = 0 });
        Assert.StartsWith("denied by policy", r.Handler.Handle(Req(RequestKind.Dhcp), "u", privileged: true).Error);
        Assert.StartsWith("denied by policy", r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = Fx.Static("x") }, "u", privileged: true).Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Managed_profile_by_id_and_a_forged_managed_flag()
    {
        var r = Make(managedJson: """[{ "name": "Corp", "addresses": [{ "address": "10.1.0.5", "prefixLength": 16 }], "gateway": "10.1.0.1" }]""");
        var ok = r.Handler.Handle(Req(RequestKind.ApplyProfile) with { ProfileId = "m-corp-corp" }, "u", privileged: true);
        Assert.True(ok.Ok, ok.Error);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("address=10.1.0.5"));
        Assert.Contains("no managed profile", r.Handler.Handle(Req(RequestKind.ApplyProfile) with { ProfileId = "m-corp-other" }, "u", privileged: true).Error);
        Assert.Contains("not both", r.Handler.Handle(Req(RequestKind.ApplyProfile) with { ProfileId = "m-corp-corp", Profile = Fx.Static("x") }, "u", privileged: true).Error);
        // "managed" is not part of the wire format: a sent profile is always a user profile.
        var json = """{"v":1,"kind":"ApplyProfile","profile":{"name":"x","managed":true,"addresses":[{"address":"192.168.1.9","prefixLength":24}]}}""";
        var (req, _) = ServiceProtocol.ParseRequest(Encoding.UTF8.GetBytes(json));
        Assert.False(req!.Profile!.Managed);
    }

    [Fact]
    public void A_sent_profile_cannot_pick_an_adapter_that_does_not_exist()
    {
        var r = Make();
        var p = Fx.Static("x"); p.Adapter = "VPN";
        Assert.Contains("not found", r.Handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = p }, "u", privileged: true).Error);
    }

    [Theory]
    [InlineData("192.168.88.1", true)]
    [InlineData("10.20.30.1/28", true)]
    [InlineData("nas", false)]
    [InlineData("192.168.88.1 & calc", false)]
    [InlineData("10.0.0.1/40", false)]
    public void Reach_targets_are_ipv4_only(string target, bool ok)
    {
        var r = Make();
        var resp = r.Handler.Handle(Req(RequestKind.Reach) with { Target = target }, "u", privileged: true);
        if (ok) Assert.True(resp.Ok, resp.Error);
        else { Assert.False(resp.Ok); Assert.Contains("IPv4", resp.Error); Assert.Empty(r.Runner.Ran); }
    }

    [Fact]
    public void Delete_route_needs_a_prefix_and_an_ipv4_gateway()
    {
        var r = Make();
        Assert.True(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8", RouteGateway = "192.168.1.254" }, "u", privileged: true).Ok);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("delete route prefix=10.0.0.0/8 interface=7 nexthop=192.168.1.254"));
        Assert.False(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0" }, "u", privileged: true).Ok);
        Assert.False(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8", RouteGateway = "x y" }, "u", privileged: true).Ok);
    }

    [Fact]
    public void An_adapter_with_an_unsafe_name_is_refused_before_a_plan()
    {
        // Windows adapter names are user-chosen (a VPN phonebook entry), so a resolved name is still untrusted.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeRunner();
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), new FakeAdapters(Fx.Adapter("a’;calc;‘", gw: "192.168.1.1")), new FakeVlan(false), runner,
            new MachineStore(Path.Combine(dir, "profiles.d")), () => Policy.None);
        var handler = new IntentHandler(svc, Path.Combine(dir, "service"), _ => { }, settleMs: 0);
        var resp = handler.Handle(Req(RequestKind.ApplyProfile) with { Profile = Fx.Static("x", "192.168.1.1", "192.168.1.50/24") }, "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("not allowed", resp.Error);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public void Renew_is_allowed_for_a_standard_user_release_and_mtu_are_not()
    {
        var r = Make();
        // DHCP adapter so renew has a step.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeRunner();
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "s")), new FakeAdapters(Fx.Nic("Ethernet", dhcp: true, addrs: ["192.168.1.10/24"], gw: "192.168.1.1")),
            new FakeVlan(false), runner, new MachineStore(Path.Combine(dir, "pd")), () => Policy.None);
        var h = new IntentHandler(svc, Path.Combine(dir, "s"), _ => { }, settleMs: 0);
        Assert.True(h.Handle(Req(RequestKind.Renew), "u", privileged: false).Ok);
        Assert.Contains(runner.Ran, s => s.Arguments.Contains("/renew"));
        Assert.StartsWith("Release needs", h.Handle(Req(RequestKind.Release), "u", privileged: false).Error);
        Assert.StartsWith("SetMtu needs", h.Handle(Req(RequestKind.SetMtu) with { Mtu = 1400 }, "u", privileged: false).Error);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(99999)]
    [InlineData(null)]
    public void SetMtu_out_of_range_is_refused(int? mtu)
    {
        var r = Make();
        var resp = r.Handler.Handle(Req(RequestKind.SetMtu) with { Mtu = mtu }, "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("576..9000", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Prefer_and_setmtu_run_for_a_privileged_caller()
    {
        var r = Make();
        Assert.True(r.Handler.Handle(Req(RequestKind.Prefer), "a", privileged: true).Ok);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("set interface") && s.Arguments.Contains("metric=10"));
        r.Runner.Ran.Clear();
        Assert.True(r.Handler.Handle(Req(RequestKind.SetMtu) with { Mtu = 1400 }, "a", privileged: true).Ok);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("mtu=1400"));
    }

    [Fact]
    public void Status_is_not_a_change_request()
    {
        Assert.Contains("not a change request", Make().Handler.Handle(Req(RequestKind.Status), "u", privileged: true).Error);
    }

    [Fact]
    public void An_apply_cut_off_by_a_crash_is_reported_at_the_next_start()
    {
        var r = Make();
        Directory.CreateDirectory(Path.GetDirectoryName(r.Handler.JournalPath)!);
        File.WriteAllText(r.Handler.JournalPath, """{"title":"lab","adapter":"Ethernet","caller":"PC\\anna","at":"2026-10-07T10:00:00+00:00","commands":["netsh x"]}""");
        var j = r.Handler.Interrupted();
        Assert.Equal("lab", j!.Title);
        Assert.False(File.Exists(r.Handler.JournalPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(r.Handler.JournalPath)!, "interrupted-*.json"));
        Assert.Contains(r.Log, l => l.Contains("interrupted apply"));
        Assert.Null(r.Handler.Interrupted());
    }

    [Theory]
    [InlineData(RequestKind.DeleteRoute)]
    [InlineData(RequestKind.ResetAdapter)]
    [InlineData(RequestKind.ClearTemp)]
    public void Destructive_intents_need_a_privileged_caller(RequestKind kind)
    {
        var r = Make();
        var req = Req(kind) with { RoutePrefix = "203.0.113.0/24", RouteGateway = "192.168.1.254" };
        Assert.StartsWith(kind + " needs", r.Handler.Handle(req, "u", privileged: false).Error);
        Assert.Empty(r.Runner.Ran);
        Assert.True(r.Handler.Handle(req, "u", privileged: true).Ok);
    }

    [Fact]
    public void A_managed_profile_is_validated_too()
    {
        // A file planted under 0.13.x survives the upgrade; "it is in profiles.d" is not enough to trust it.
        var r = Make(managedJson: """[{ "name": "Corp", "dnsSuffix": "a'; calc; '", "addresses": [{ "address": "10.1.0.5", "prefixLength": 16 }] }]""");
        var resp = r.Handler.Handle(Req(RequestKind.ApplyProfile) with { ProfileId = "m-corp-corp" }, "u", privileged: true);
        Assert.False(resp.Ok);
        // Two layers reject it: MachineStore skips the bad file at load (so the id is gone), and if one ever
        // reached the handler, ProfileGuard would refuse it. Either message is acceptable.
        Assert.True(resp.Error!.Contains("no managed profile") || resp.Error.Contains("DNS suffix"), resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void Apply_report_fits_the_wire_limit_even_with_long_command_output()
    {
        var plan = new Planning.ApplyPlan { Title = "t", Adapter = "Ethernet" };
        var outcome = new Planning.ApplyOutcome();
        for (var i = 0; i < 40; i++)
        {
            var step = Planning.Step.Netsh("s" + i, "interface ipv4 show config");
            plan.Steps.Add(step);
            outcome.Results.Add(new Planning.StepResult(step, 0, new string('x', 20_000)));
        }
        var wire = ServiceProtocol.Encode(new ServiceResponse(1, true) { Apply = ApplyReport.From(plan, outcome) });
        Assert.True(wire.Length < ServiceProtocol.MaxLine);
    }
}
