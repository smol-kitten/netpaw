using System.Text.Json;
using NetPaw;
using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Ipc;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

/// <summary>G4 (docs/DESIGN-G4-commit-confirm.md, step 2): the service's own revert timer, driven here by a fake clock and a fake liveness.</summary>
public class CommitConfirmTests
{
    sealed class FakeLiveness : ILiveness
    {
        public bool Up = true;
        public int Rounds;
        public Task<bool> GatewayUp(string gateway, CancellationToken ct) { Rounds++; return Task.FromResult(Up); }
        public Task<bool> DnsUp(string server, CancellationToken ct) => Task.FromResult(Up);
    }
    sealed class Adapters(AdapterInfo first) : IAdapterProvider { public AdapterInfo Current = first; public IReadOnlyList<AdapterInfo> GetAdapters() => [Current]; }
    sealed class TierSource(IReadOnlyDictionary<PolicyIntent, Tier> o) : ITierPolicySource { public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read() => (o, true, null); }

    sealed class Rig
    {
        public readonly string Dir = Directory.CreateTempSubdirectory().FullName;
        public readonly FakeRunner Runner = new();
        public readonly FakeLiveness Live = new();
        public readonly List<string> Log = [];
        public DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        public IntentHandler Handler;
        public Rig(Dictionary<string, object>? storePolicy = null, Dictionary<PolicyIntent, Tier>? tiers = null) => Handler = NewHandler(settleMs: 0, storePolicy, tiers);

        /// <summary>A second handler on the same folder = the service restarted.</summary>
        public IntentHandler NewHandler(int settleMs = 0, Dictionary<string, object>? storePolicy = null, Dictionary<PolicyIntent, Tier>? tiers = null)
        {
            // the adapter is on DHCP before every apply, so the revert of a static profile is one DHCP plan: "set address ... source=dhcp"
            // plus the DNS step (#145: both are always issued); Reverts counts the address step, one per revert
            var svc = new NetPawService(new JsonStore(Path.Combine(Dir, "service")), new Adapters(Fx.Adapter(dhcp: true, gw: "192.168.1.1")), new FakeVlan(false), Runner,
                new MachineStore(Path.Combine(Dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(storePolicy ?? [])));
            var policy = tiers is null ? null : new PolicyTiers(new TierSource(tiers), Path.Combine(Dir, "service", "policy.cache.json"), Log.Add);
            return new IntentHandler(svc, Path.Combine(Dir, "service"), Log.Add, settleMs: settleMs, policy: policy, liveness: Live) { Clock = () => Now };
        }

        public ServiceResponse Apply(int? confirm = 30, Tier tier = Tier.StandardUser, string? gw = "192.168.1.1", bool dns = true, string caller = "PC\\anna (session 2)")
        {
            var p = Fx.Static("lab", gw, "192.168.1.50/24"); if (!dns) p.Dns = [];
            return Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.ApplyProfile) { Profile = p, ConfirmSeconds = confirm }, caller, tier);
        }
        public ServiceResponse Confirm(Tier tier = Tier.StandardUser, string? id = null) =>
            Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.ConfirmApply) { AdapterId = id }, "PC\\bob (session 3)", tier);
        public int Reverts => Runner.Ran.Count(s => s.Arguments.Contains("set address") && s.Arguments.Contains("source=dhcp"));
        public bool Armed => File.Exists(Handler.PendingPath);
        public void Ticks(int n, int stepSeconds = 2) { for (var i = 0; i < n; i++) { Now = Now.AddSeconds(stepSeconds); Handler.Tick(); } }
    }

    [Fact]
    public void A_window_that_is_not_confirmed_is_reverted_at_the_deadline()
    {
        var r = new Rig();
        var resp = r.Apply(30);
        Assert.True(resp.Ok, resp.Error); Assert.True(r.Armed); Assert.Equal(0, r.Reverts);
        Assert.Contains(r.Log, l => l.Contains("armed commit-confirm: 30 s on Ethernet (#7) for PC\\anna") && l.Contains("probes: gateway 192.168.1.1, dns 1.1.1.1"));
        var w = r.Handler.ArmedWindow()!;
        Assert.Equal(("Ethernet", 30, "PC\\anna (session 2)"), (w.Adapter, w.SecondsLeft, w.Caller)); Assert.Null(w.Reason);
        r.Ticks(10);                                            // 20 s, everything answers
        Assert.Equal(0, r.Reverts); Assert.Equal(10, r.Handler.ArmedWindow()!.SecondsLeft);
        r.Ticks(6);                                             // 32 s
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed); Assert.Null(r.Handler.ArmedWindow());
        Assert.Contains(r.Log, l => l.Contains("commit-confirm timeout: reverting 'lab' on Ethernet (armed by PC\\anna"));
        Assert.Contains(r.Log, l => l.Contains("DHCP on Ethernet (#7) for commit-confirm (timeout): ok"));
        r.Ticks(3);                                             // nothing is re-armed
        Assert.Equal(1, r.Reverts);
    }

    [Fact]
    public void Three_consecutive_failed_probe_rounds_revert_early_and_one_good_round_resets_the_count()
    {
        var r = new Rig();
        Assert.True(r.Apply(60).Ok);
        r.Live.Up = false; r.Ticks(2);
        Assert.Equal(0, r.Reverts);
        r.Live.Up = true; r.Ticks(1);                           // the count starts over
        r.Live.Up = false; r.Ticks(2);
        Assert.Equal(0, r.Reverts); Assert.True(r.Armed);
        r.Ticks(1);                                             // third failed round in a row
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm liveness: reverting 'lab' on Ethernet"));
        Assert.Contains(r.Log, l => l.Contains("for commit-confirm (liveness): ok"));
    }

    [Fact]
    public void A_confirm_closes_the_window_and_the_apply_stays()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        var c = r.Confirm();
        Assert.True(c.Ok, c.Error); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm confirmed by PC\\bob (session 3): 'lab' on Ethernet stays (armed by PC\\anna (session 2), 30 s were left)"));
        r.Live.Up = false; r.Ticks(20);                         // 40 s of dead probes, past the deadline: nothing to revert
        Assert.Equal(0, r.Reverts);
        Assert.Contains("nothing to confirm", r.Confirm().Error);
    }

    [Fact]
    public void A_confirm_below_the_stored_tier_is_refused()
    {
        // ApplyProfile raised to NCO by policy: the confirm needs NCO too (max of the two tiers), whatever ConfirmApply's own tier says.
        var r = new Rig(tiers: new() { [PolicyIntent.ApplyProfile] = Tier.NetworkConfigOperators });
        Assert.True(r.Apply(30, Tier.NetworkConfigOperators).Ok);
        Assert.Contains(r.Log, l => l.Contains("confirm needs tier NetworkConfigOperators"));
        var low = r.Confirm(Tier.StandardUser);
        Assert.False(low.Ok); Assert.Contains("needs tier NetworkConfigOperators or higher; you are StandardUser", low.Error); Assert.True(r.Armed);
        Assert.True(r.Confirm(Tier.Admin).Ok);                  // a colleague with the tier may keep it
        Assert.False(r.Armed);
    }

    [Fact]
    public void A_new_apply_on_the_same_adapter_replaces_the_window()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        Assert.True(r.Apply(confirm: null).Ok);                 // the second apply carries no window
        Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm window on Ethernet ('lab' by PC\\anna (session 2)) cancelled by a new apply"));
        r.Ticks(20);
        Assert.Equal(0, r.Reverts);
        // and a confirmed second apply opens its own window with its own deadline
        Assert.True(r.Apply(10).Ok); Assert.Equal(10, r.Handler.ArmedWindow()!.SecondsLeft);
    }

    [Fact]
    public void A_manual_revert_closes_the_window()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        var rev = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.RevertLastApply), "PC\\anna (session 2)", Tier.StandardUser);
        Assert.True(rev.Ok, rev.Error); Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        r.Ticks(20);
        Assert.Equal(1, r.Reverts);                             // the service does not revert a second time
    }

    [Fact]
    public void A_restart_past_the_deadline_reverts_at_once()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        r.Now = r.Now.AddSeconds(45);
        r.Handler = r.NewHandler();                              // the service came back after the deadline
        Assert.Null(r.Handler.Interrupted());
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm timeout: reverting 'lab'"));
    }

    [Fact]
    public void A_restart_inside_the_window_re_arms_and_ignores_probes_until_settle_plus_three_ticks()
    {
        var r = new Rig();
        Assert.True(r.Apply(60).Ok);
        r.Now = r.Now.AddSeconds(10);
        r.Handler = r.NewHandler(settleMs: 5000);
        r.Live.Up = false;
        r.Handler.Interrupted();
        Assert.Contains(r.Log, l => l.Contains("re-armed commit-confirm: 50 s left on Ethernet"));
        Assert.True(r.Armed); Assert.Equal(50, r.Handler.ArmedWindow()!.SecondsLeft);
        r.Handler.Tick(); r.Ticks(2);                            // +0, +2, +4: inside the fresh settle, dead probes do not count
        Assert.Equal(0, r.Live.Rounds); Assert.Equal(0, r.Reverts);
        r.Ticks(2);                                              // +6, +8: two failed rounds
        Assert.Equal(2, r.Live.Rounds); Assert.Equal(0, r.Reverts);
        r.Ticks(1);                                              // +10: the third
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm liveness: reverting"));
    }

    [Fact]
    public void A_window_left_in_reverting_by_a_crash_is_reverted_once_more_and_removed()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        var list = JsonSerializer.Deserialize<List<IntentHandler.Pending>>(File.ReadAllText(r.Handler.PendingPath), ServiceProtocol.Json)!;
        File.WriteAllText(r.Handler.PendingPath, JsonSerializer.Serialize(new[] { list[0] with { State = "reverting", RevertReason = "liveness" } }, ServiceProtocol.Json));
        r.Handler = r.NewHandler();
        r.Handler.Interrupted();
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm liveness: reverting 'lab'"));
        r.Handler.Interrupted(); r.Ticks(20);
        Assert.Equal(1, r.Reverts);                              // exactly once from the user's view
    }

    [Fact]
    public void A_profile_without_gateway_or_dns_is_timer_only()
    {
        var r = new Rig();
        Assert.True(r.Apply(30, gw: null, dns: false).Ok);
        Assert.Contains(r.Log, l => l.Contains("probes: none, timer only"));
        r.Live.Up = false; r.Ticks(10);                          // 20 s of "loss" that cannot be measured
        Assert.Equal(0, r.Live.Rounds); Assert.Equal(0, r.Reverts);
        r.Ticks(6);
        Assert.Equal(1, r.Reverts);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm timeout"));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(601)] [InlineData(-5)]
    public void An_out_of_range_window_is_refused_before_anything_runs(int seconds)
    {
        var r = new Rig();
        var resp = r.Apply(seconds);
        Assert.False(resp.Ok); Assert.Contains("confirm window must be 5..600 s (0 = off)", resp.Error);
        Assert.Empty(r.Runner.Ran); Assert.False(r.Armed);
    }

    [Fact]
    public void A_window_on_an_intent_that_cannot_carry_one_is_refused_not_ignored()
    {
        var r = new Rig();
        var resp = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.ResetAdapter) { Adapter = "Ethernet", ConfirmSeconds = 30 }, "nco", Tier.NetworkConfigOperators);
        Assert.False(resp.Ok); Assert.Contains("not ResetAdapter", resp.Error);
        Assert.Empty(r.Runner.Ran);
    }

    [Fact]
    public void The_policy_minimum_raises_a_request_without_a_window_and_says_so()
    {
        var r = new Rig(storePolicy: new() { ["CommitConfirmMinSeconds"] = 20 });
        Assert.True(r.Apply(confirm: null).Ok);
        var w = r.Handler.ArmedWindow()!;
        Assert.Equal((20, "policy"), (w.SecondsLeft, w.Reason));
        Assert.Contains(r.Log, l => l.Contains("armed commit-confirm: 20 s") && l.Contains("(policy;"));
        // a larger request is kept as asked
        Assert.True(r.Apply(45).Ok); Assert.Equal(45, r.Handler.ArmedWindow()!.SecondsLeft); Assert.Null(r.Handler.ArmedWindow()!.Reason);
        // the status record carries it for the tray
        var pol = PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object> { ["CommitConfirmMinSeconds"] = 20 }));
        Assert.Equal(20, pol.CommitConfirmMinSeconds); Assert.True(pol.IsSet("CommitConfirmMinSeconds")); Assert.Contains("CommitConfirmMinSeconds", Policy.Keys);
        Assert.Null(Policy.None.CommitConfirmMinSeconds);
    }

    [Fact]
    public void A_dhcp_request_can_be_confirmed_too_and_a_revert_of_a_static_pre_state_is_a_static_plan()
    {
        var r = new Rig();
        // a static adapter, DHCP applied with a window: the revert puts the static state back
        var svc = new NetPawService(new JsonStore(Path.Combine(r.Dir, "service")), new Adapters(Fx.Adapter(dhcp: false, gw: "192.168.1.1")), new FakeVlan(false), r.Runner,
            new MachineStore(Path.Combine(r.Dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())));
        r.Handler = new IntentHandler(svc, Path.Combine(r.Dir, "service"), r.Log.Add, settleMs: 0, liveness: r.Live) { Clock = () => r.Now };
        var resp = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = "Ethernet", AdapterId = Fx.Id("Ethernet"), ConfirmSeconds = 10 }, "PC\\anna (session 2)", Tier.StandardUser);
        Assert.True(resp.Ok, resp.Error);
        Assert.Contains(r.Log, l => l.Contains("armed commit-confirm: 10 s") && l.Contains("probes: none, timer only"));   // a DHCP plan defines no gateway or DNS to probe
        r.Ticks(6);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("source=static address=192.168.1.10"));
        Assert.False(r.Armed);
    }
}
