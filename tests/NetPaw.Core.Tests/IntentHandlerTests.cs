using NetPaw.Adapters;
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
    internal sealed record Rig(IntentHandler Handler, FakeRunner Runner, string Dir, List<string> Log);

    internal static Rig MakeRigFor(Dictionary<string, object>? policy) => Make(policy);
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
        Assert.True(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8", RouteGateway = "192.168.1.254" }, "u", Tier.Admin).Ok);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("delete route prefix=10.0.0.0/8 interface=7 nexthop=192.168.1.254"));
        Assert.False(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0" }, "u", Tier.Admin).Ok);
        Assert.False(r.Handler.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8", RouteGateway = "x y" }, "u", Tier.Admin).Ok);
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
    [InlineData(RequestKind.Prefer)]
    public void Destructive_intents_need_a_privileged_caller(RequestKind kind)
    {
        var r = Make();
        var req = Req(kind) with { RoutePrefix = "203.0.113.0/24", RouteGateway = "192.168.1.254" };
        Assert.StartsWith(kind + " needs", r.Handler.Handle(req, "u", privileged: false).Error);
        Assert.Empty(r.Runner.Ran);
        // DeleteRoute is Admin-only by default (operator 2026-10-29); the other three are NCO.
        Assert.True(r.Handler.Handle(req, "u", kind == RequestKind.DeleteRoute ? Tier.Admin : Tier.NetworkConfigOperators).Ok);
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

    // ---- appliesTo adapter binding + virtual-adapter guard (t-0e5511) -------------------------

    static Rig MakeWith(Adapters.AdapterInfo adapter)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeRunner();
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), new FakeAdapters(adapter), new FakeVlan(false), runner,
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())));
        var log = new List<string>();
        return new Rig(new IntentHandler(svc, Path.Combine(dir, "service"), log.Add, settleMs: 0), runner, dir, log);
    }

    static ServiceRequest ApplyInline(Profile p, string adapter) => Req(RequestKind.ApplyProfile) with { Profile = p, Adapter = adapter };

    [Fact]
    public void AppliesTo_absent_applies_to_any_named_adapter()
    {
        var r = Make();
        Assert.True(r.Handler.Handle(ApplyInline(Fx.Static("lab", "192.168.1.1", "192.168.1.50/24"), "Ethernet"), "u", privileged: true).Ok);
    }

    [Fact]
    public void AppliesTo_name_pattern_or_mac_match_is_allowed()
    {
        var r = Make();
        var byName = Fx.Static("lab", "192.168.1.1", "192.168.1.50/24"); byName.AppliesTo = new AppliesTo { Names = ["Ether*"] };
        Assert.True(r.Handler.Handle(ApplyInline(byName, "Ethernet"), "u", privileged: true).Ok);
        var byMac = Fx.Static("lab", "192.168.1.1", "192.168.1.50/24"); byMac.AppliesTo = new AppliesTo { Macs = ["aa-bb-cc-dd-ee-ff"] };   // separators differ, still matches
        Assert.True(r.Handler.Handle(ApplyInline(byMac, "Ethernet"), "u", privileged: true).Ok);
    }

    [Fact]
    public void AppliesTo_mismatch_is_refused_before_planning()
    {
        var r = Make();
        var p = Fx.Static("lab", "192.168.1.1", "192.168.1.50/24"); p.AppliesTo = new AppliesTo { Names = ["Wi-Fi*"], Macs = ["11:22:33:44:55:66"] };
        var resp = r.Handler.Handle(ApplyInline(p, "Ethernet"), "u", privileged: true);
        Assert.False(resp.Ok);
        Assert.Contains("restricted to", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void A_virtual_adapter_is_refused_unless_the_profile_lists_it()
    {
        var r = MakeWith(Fx.Nic(name: "tap0", physical: false, addrs: ["10.0.0.5/24"], gw: "10.0.0.1"));
        var refused = r.Handler.Handle(ApplyInline(Fx.Static("v", "10.0.0.1", "10.0.0.5/24"), "tap0"), "u", privileged: true);
        Assert.False(refused.Ok);
        Assert.Contains("virtual adapter", refused.Error);
        Assert.Empty(r.Runner.Ran);
        var opted = Fx.Static("v", "10.0.0.1", "10.0.0.5/24"); opted.AppliesTo = new AppliesTo { Names = ["tap0"] };
        Assert.True(r.Handler.Handle(ApplyInline(opted, "tap0"), "u", privileged: true).Ok);
    }

    [Fact]
    public void A_standard_user_cannot_opt_a_virtual_adapter_in_via_inline_appliesTo()
    {
        // A non-privileged caller's OWN inline appliesTo may restrict but must not UNLOCK a VPN/TAP adapter;
        // only a managed (admin-defined) profile or a privileged caller may target a non-host-facing adapter.
        var r = MakeWith(Fx.Nic(name: "tap0", physical: false, addrs: ["10.0.0.5/24"], gw: "10.0.0.1"));
        var p = Fx.Static("v", "10.0.0.1", "10.0.0.5/24"); p.AppliesTo = new AppliesTo { Names = ["tap0"] };
        var refused = r.Handler.Handle(ApplyInline(p, "tap0"), "u", privileged: false);
        Assert.False(refused.Ok);
        Assert.Contains("virtual adapter", refused.Error);
        Assert.Empty(r.Runner.Ran);
        // the same profile from a privileged caller IS allowed to unlock it
        Assert.True(r.Handler.Handle(ApplyInline(p, "tap0"), "admin", privileged: true).Ok);
    }

    sealed class InlineSource(IReadOnlyDictionary<PolicyIntent, Tier> o) : ITierPolicySource
    {
        public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read() => (o, true, null);
    }

    [Fact]
    public void Policy_cannot_lower_the_virtual_unlock_below_its_NCO_floor_via_the_service()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), new FakeAdapters(Fx.Nic(name: "tap0", physical: false, addrs: ["10.0.0.5/24"], gw: "10.0.0.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())));
        // A policy tries to loosen VirtualUnlock to StandardUser; the floor clamps it to NCO.
        var policy = new PolicyTiers(new InlineSource(new Dictionary<PolicyIntent, Tier> { [PolicyIntent.VirtualUnlock] = Tier.StandardUser }), Path.Combine(dir, "policy.cache.json"));
        var h = new IntentHandler(svc, Path.Combine(dir, "service"), _ => { }, settleMs: 0, policy: policy);
        var p = Fx.Static("v", "10.0.0.1", "10.0.0.5/24"); p.AppliesTo = new AppliesTo { Names = ["tap0"] };
        // StandardUser still cannot unlock the virtual adapter (floor is NCO, policy could not lower it).
        Assert.False(h.Handle(ApplyInline(p, "tap0"), "u", Tier.StandardUser).Ok);
        // An NCO caller can.
        Assert.True(h.Handle(ApplyInline(p, "tap0"), "nco", Tier.NetworkConfigOperators).Ok);
    }

    [Fact]
    public void Handle_enforces_a_policy_tightened_tier_end_to_end()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())));
        var policy = new PolicyTiers(new InlineSource(new Dictionary<PolicyIntent, Tier> { [PolicyIntent.Dhcp] = Tier.Admin }), Path.Combine(dir, "policy.cache.json"));
        var h = new IntentHandler(svc, Path.Combine(dir, "service"), _ => { }, settleMs: 0, policy: policy);
        // Dhcp tightened to Admin: an NCO caller is now denied, an Admin caller is allowed.
        Assert.False(h.Handle(Req(RequestKind.Dhcp), "u", Tier.NetworkConfigOperators).Ok);
        Assert.True(h.Handle(Req(RequestKind.Dhcp), "a", Tier.Admin).Ok);
        // DeleteRoute defaults to Admin (operator 2026-10-29): StandardUser and NCO are denied through the tier overload.
        Assert.False(h.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8" }, "u", Tier.StandardUser).Ok);
        Assert.Contains("needs tier Admin", h.Handle(Req(RequestKind.DeleteRoute) with { RoutePrefix = "10.0.0.0/8" }, "u", Tier.NetworkConfigOperators).Error);
    }
}

public class RevertLastApplyTests
{
    sealed class MutableAdapters(AdapterInfo first) : IAdapterProvider
    {
        public AdapterInfo Current = first;
        public IReadOnlyList<AdapterInfo> GetAdapters() => [Current];
    }

    sealed record Rig(IntentHandler Handler, FakeRunner Runner, MutableAdapters Adapters, List<string> Log);
    sealed class TierSource(IReadOnlyDictionary<PolicyIntent, Tier> o) : ITierPolicySource { public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read() => (o, true, null); }

    static Rig Make(AdapterInfo? adapter = null, Dictionary<string, object>? storePolicy = null, Dictionary<PolicyIntent, Tier>? tiers = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeRunner();
        var adapters = new MutableAdapters(adapter ?? Fx.Adapter(gw: "192.168.1.1"));
        var svc = new NetPawService(new JsonStore(Path.Combine(dir, "service")), adapters, new FakeVlan(false), runner,
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(storePolicy ?? [])));
        var log = new List<string>();
        var policy = tiers is null ? null : new PolicyTiers(new TierSource(tiers), Path.Combine(dir, "service", "policy.cache.json"), log.Add);
        return new Rig(new IntentHandler(svc, Path.Combine(dir, "service"), log.Add, settleMs: 0, policy: policy), runner, adapters, log);
    }

    static ServiceRequest Revert() => new(ServiceProtocol.Version, RequestKind.RevertLastApply);
    static ServiceRequest Apply(string ip) => new ServiceRequest(ServiceProtocol.Version, RequestKind.ApplyProfile) { Profile = Fx.Static("lab", "192.168.1.1", ip) };
    static IEnumerable<string> Netsh(Rig r, int from) => r.Runner.Ran.Skip(from).Select(s => s.Arguments);

    [Fact]
    public void Nothing_recorded_is_refused_with_a_reason()
    {
        var r = Make();
        var resp = r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false);
        Assert.False(resp.Ok); Assert.Contains("nothing to revert", resp.Error);
        Assert.Null(r.Handler.LastApplySummary());
    }

    [Fact]
    public void Static_pre_state_is_restored_with_its_addresses_and_a_standard_user_may_do_it()
    {
        var r = Make();   // Ethernet is static 192.168.1.10/24 gw .1 before the apply
        Assert.True(r.Handler.Handle(Apply("192.168.1.50/24"), "PC\\anna (session 2)", privileged: false).Ok);
        var pre = r.Handler.LastApply()!;
        Assert.Equal("Ethernet", pre.Adapter); Assert.Equal("PC\\anna (session 2)", pre.Caller); Assert.Equal(pre.Mac, pre.Before.AdapterMac);
        Assert.Contains("192.168.1.10/24", pre.Before.Addresses.Select(a => a.ToString()));
        Assert.Contains("on Ethernet at", r.Handler.LastApplySummary());
        var n = r.Runner.Ran.Count;
        var revert = r.Handler.Handle(Revert(), "PC\\bob (session 3)", privileged: false);
        Assert.True(revert.Ok, revert.Error);
        Assert.Contains(Netsh(r, n), a => a.Contains("set address name=\"Ethernet\" source=static address=192.168.1.10"));
        Assert.Contains(r.Log, l => l.StartsWith("revert to the pre-apply state for PC\\bob"));
    }

    [Fact]
    public void Dhcp_pre_state_is_restored_as_dhcp()
    {
        var r = Make(Fx.Adapter(dhcp: true, addrs: ["192.168.1.77/24"], gw: "192.168.1.1"));
        Assert.True(r.Handler.Handle(Apply("192.168.1.50/24"), "PC\\anna (session 2)", privileged: false).Ok);
        Assert.True(r.Handler.LastApply()!.Before.Dhcp);
        var n = r.Runner.Ran.Count;
        Assert.True(r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false).Ok);
        Assert.Contains(Netsh(r, n), a => a.Contains("set address name=\"Ethernet\" source=dhcp"));
    }

    [Fact]
    public void Two_reverts_return_to_the_post_apply_state_and_do_not_loop()
    {
        var r = Make();
        Assert.True(r.Handler.Handle(Apply("192.168.1.50/24"), "PC\\anna (session 2)", privileged: false).Ok);
        r.Adapters.Current = Fx.Adapter(addrs: ["192.168.1.50/24"], gw: "192.168.1.1");   // what the apply produced
        var n1 = r.Runner.Ran.Count;
        Assert.True(r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false).Ok);
        Assert.Contains(Netsh(r, n1), a => a.Contains("source=static address=192.168.1.10"));   // back to the pre-apply state
        r.Adapters.Current = Fx.Adapter(addrs: ["192.168.1.10/24"], gw: "192.168.1.1");
        var n2 = r.Runner.Ran.Count;
        Assert.True(r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false).Ok);
        Assert.Contains(Netsh(r, n2), a => a.Contains("source=static address=192.168.1.50"));   // the revert's own snapshot: the post-apply state
        Assert.DoesNotContain(Netsh(r, n2), a => a.Contains("address=192.168.1.10"));
        Assert.Equal(2, r.Log.Count(l => l.StartsWith("revert to the pre-apply state")));          // exactly the two explicit calls
    }

    [Fact]
    public void A_policy_can_raise_the_revert_tier()
    {
        var r = Make(tiers: new() { [PolicyIntent.RevertLastApply] = Tier.Admin });
        Assert.True(r.Handler.Handle(Apply("192.168.1.50/24"), "PC\\anna (session 2)", privileged: false).Ok);
        var resp = r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: true);   // NCO, not Admin
        Assert.False(resp.Ok); Assert.Contains("needs tier Admin", resp.Error);
    }

    [Fact]
    public void A_virtual_adapter_snapshot_needs_the_virtual_unlock_tier()
    {
        var tap = Fx.Nic("TAP-Windows", "TAP-Windows Adapter V9", physical: false, addrs: ["10.8.0.2/24"]);
        var r = Make(tap);
        Directory.CreateDirectory(Path.GetDirectoryName(r.Handler.PreApplyPath)!);
        File.WriteAllText(r.Handler.PreApplyPath, System.Text.Json.JsonSerializer.Serialize(
            new IntentHandler.PreApply(tap.Name, tap.Mac, "corp-vpn", "PC\\admin (session 1)", DateTimeOffset.Now, new Profile { Name = "pre-apply", Dhcp = true }), ServiceProtocol.Json));
        var user = r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false);
        Assert.False(user.Ok); Assert.Contains("virtual adapter", user.Error);
        var n = r.Runner.Ran.Count;
        Assert.True(r.Handler.Handle(Revert(), "PC\\ops (session 2)", privileged: true).Ok);   // NCO = the VirtualUnlock default
        Assert.Contains(Netsh(r, n), a => a.Contains("source=dhcp"));
    }

    [Fact]
    public void Without_user_profiles_only_a_dhcp_pre_state_can_be_restored()
    {
        var r = Make(storePolicy: new() { ["AllowUserProfiles"] = 0 });
        Directory.CreateDirectory(Path.GetDirectoryName(r.Handler.PreApplyPath)!);
        var a = r.Adapters.Current;
        File.WriteAllText(r.Handler.PreApplyPath, System.Text.Json.JsonSerializer.Serialize(
            new IntentHandler.PreApply(a.Name, a.Mac, "corp", "PC\\admin (session 1)", DateTimeOffset.Now, new Profile { Name = "pre-apply", Addresses = [IpAddr.Parse("192.168.1.10/24")] }), ServiceProtocol.Json));
        var resp = r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false);
        Assert.False(resp.Ok); Assert.Contains("denied by policy", resp.Error);
    }

    [Fact]
    public void A_snapshot_of_another_adapter_identity_is_refused()
    {
        var r = Make();
        var stale = new IntentHandler.PreApply("Ethernet", "11:22:33:44:55:66", "lab", "PC\\anna (session 2)", DateTimeOffset.Now, new Profile { Name = "pre-apply", Dhcp = true });
        Directory.CreateDirectory(Path.GetDirectoryName(r.Handler.PreApplyPath)!);
        File.WriteAllText(r.Handler.PreApplyPath, System.Text.Json.JsonSerializer.Serialize(stale, ServiceProtocol.Json));
        var resp = r.Handler.Handle(Revert(), "PC\\anna (session 2)", privileged: false);
        Assert.False(resp.Ok); Assert.Contains("MAC differs", resp.Error);
    }

    [Fact]
    public void A_failed_snapshot_removes_the_previous_one()
    {
        var r = Make();
        Assert.True(r.Handler.Handle(Apply("192.168.1.50/24"), "PC\\anna (session 2)", privileged: false).Ok);
        Assert.NotNull(r.Handler.LastApply());
        // the next apply names an adapter the provider no longer lists: the old snapshot must not survive as "last apply"
        var resp = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = "Ethernet" }, "PC\\anna (session 2)", privileged: false);
        r.Adapters.Current = Fx.Adapter(name: "Ethernet 9");
        var dhcp = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = "Ethernet 9" }, "PC\\anna (session 2)", privileged: false);
        Assert.True(dhcp.Ok, dhcp.Error);
        Assert.Equal("Ethernet 9", r.Handler.LastApply()!.Adapter);
    }

    [Fact]
    public void ListenGoesThroughTierGateAndCapturePolicyAndNeverHoldsTheChangeGate()
    {
        // Off Windows pktmon is absent: an allowed call fails with "not available", a refused one with the tier/policy reason.
        var r = Make();
        var noDir = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "nodir", Tier.StandardUser);
        Assert.False(noDir.Ok); Assert.Contains("no service-owned capture folder", noDir.Error);
        r.Handler.CaptureDirectory = () => Path.GetTempPath();
        var ok = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "a", Tier.StandardUser);
        Assert.False(ok.Ok); Assert.Contains("pktmon is not available", ok.Error);
        var denied = Make(storePolicy: new() { ["AllowCapture"] = 0 }).Handler.Handle(ServiceChange.Listen("Ethernet", 35), "a", Tier.Admin);
        Assert.False(denied.Ok); Assert.Contains("denied by policy", denied.Error);
        var tier = Make(tiers: new() { [PolicyIntent.Capture] = Tier.Admin }).Handler.Handle(ServiceChange.Listen("Ethernet", 35), "a", Tier.StandardUser);
        Assert.False(tier.Ok); Assert.Contains("needs tier Admin", tier.Error);
        var noAdapter = r.Handler.Handle(ServiceChange.Listen("Nope", 35), "a", Tier.StandardUser);
        Assert.False(noAdapter.Ok); Assert.DoesNotContain("pktmon", noAdapter.Error);
    }

    [Fact]
    public void ListenHasAPerCallerCooldownSoOneUserCannotKeepPktmonBusy()
    {
        var r = Make();
        r.Handler.CaptureDirectory = () => Path.GetTempPath();
        var t0 = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        r.Handler.Clock = () => t0;
        var first = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "PC\\anna", Tier.StandardUser);
        Assert.Contains("pktmon is not available", first.Error);            // reached the capture (off Windows it stops there)
        var again = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "PC\\anna", Tier.StandardUser);
        Assert.False(again.Ok); Assert.Contains("listen cooldown: wait 60 s", again.Error);
        var other = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "PC\\bob", Tier.StandardUser);
        Assert.Contains("pktmon is not available", other.Error);            // the cooldown is per caller
        r.Handler.Clock = () => t0.AddSeconds(61);
        var later = r.Handler.Handle(ServiceChange.Listen("Ethernet", 35), "PC\\anna", Tier.StandardUser);
        Assert.Contains("pktmon is not available", later.Error);
    }
}
