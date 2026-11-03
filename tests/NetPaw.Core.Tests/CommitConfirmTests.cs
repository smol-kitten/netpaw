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
        /// <summary>The in-process clock (ms); Ticks advances both, a test that moves only Now simulates a wall-clock jump.</summary>
        public long Mono = 1_000_000;
        public Adapters Nics = null!;
        public IntentHandler Handler;
        public Rig(Dictionary<string, object>? storePolicy = null, Dictionary<PolicyIntent, Tier>? tiers = null) => Handler = NewHandler(settleMs: 0, storePolicy, tiers);

        /// <summary>A second handler on the same folder = the service restarted.</summary>
        public IntentHandler NewHandler(int settleMs = 0, Dictionary<string, object>? storePolicy = null, Dictionary<PolicyIntent, Tier>? tiers = null)
        {
            // the adapter is on DHCP before every apply, so the revert of a static profile is one DHCP plan: "set address ... source=dhcp"
            // plus the DNS step (#145: both are always issued); Reverts counts the address step, one per revert
            Nics ??= new Adapters(Fx.Adapter(dhcp: true, gw: "192.168.1.1"));
            var svc = new NetPawService(new JsonStore(Path.Combine(Dir, "service")), Nics, new FakeVlan(false), Runner,
                new MachineStore(Path.Combine(Dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(storePolicy ?? [])));
            var policy = tiers is null ? null : new PolicyTiers(new TierSource(tiers), Path.Combine(Dir, "service", "policy.cache.json"), Log.Add);
            return new IntentHandler(svc, Path.Combine(Dir, "service"), Log.Add, settleMs: settleMs, policy: policy, liveness: Live) { Clock = () => Now, Monotonic = () => Mono };
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
        public void Ticks(int n, int stepSeconds = 2) { for (var i = 0; i < n; i++) { Now = Now.AddSeconds(stepSeconds); Mono += stepSeconds * 1000L; Handler.Tick(); } }
        public ServiceResponse Send(RequestKind kind, Tier tier = Tier.StandardUser, int? confirm = null) =>
            Handler.Handle(new ServiceRequest(ServiceProtocol.Version, kind) { Adapter = "Ethernet", ConfirmSeconds = confirm }, "PC\\carl (session 4)", tier);
    }

    [Fact]
    public void A_window_that_is_not_confirmed_is_reverted_at_the_deadline()
    {
        var r = new Rig();
        var resp = r.Apply(30);
        Assert.True(resp.Ok, resp.Error); Assert.True(r.Armed); Assert.Equal(0, r.Reverts);
        Assert.True(resp.Apply!.ConfirmArmed); Assert.Null(resp.Apply.ConfirmNotArmed); Assert.Equal(30, resp.Apply.ConfirmSeconds);
        Assert.Contains(r.Log, l => l.Contains("armed commit-confirm: 30 s on Ethernet (#7) for PC\\anna") && l.Contains("probes: gateway 192.168.1.1, dns 1.1.1.1"));
        var w = r.Handler.ArmedWindow()!;
        Assert.Equal(("Ethernet", 30, "PC\\anna (session 2)"), (w.Adapter, w.SecondsLeft, w.Caller)); Assert.Null(w.Reason);
        r.Ticks(10);                                            // 20 s, everything answers
        Assert.Equal(0, r.Reverts); Assert.Equal(10, r.Handler.ArmedWindow()!.SecondsLeft);
        r.Ticks(6);                                             // 32 s
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed); Assert.Null(r.Handler.ArmedWindow());
        var ended = r.Handler.LastWindow()!;   // Status tells a client that saw the window vanish what happened to the change
        Assert.Equal(("Ethernet", "lab", "reverted", "timeout", "service"), (ended.Adapter, ended.Title, ended.Outcome, ended.Reason, ended.By));
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
        Assert.Equal(("reverted", "liveness"), (r.Handler.LastWindow()!.Outcome, r.Handler.LastWindow()!.Reason));
        Assert.Contains(r.Log, l => l.Contains("for commit-confirm (liveness): ok"));
    }

    [Fact]
    public void A_confirm_closes_the_window_and_the_apply_stays()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        var c = r.Confirm();
        Assert.True(c.Ok, c.Error); Assert.False(r.Armed);
        Assert.Equal(("kept", "confirm", "PC\\bob (session 3)"), (r.Handler.LastWindow()!.Outcome, r.Handler.LastWindow()!.Reason, r.Handler.LastWindow()!.By));
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
        var plain = r.Apply(confirm: null);                     // the second apply carries no window
        Assert.True(plain.Ok); Assert.Null(plain.Apply!.ConfirmArmed);
        Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm window on Ethernet ('lab' by PC\\anna (session 2)) cancelled by a new apply"));
        Assert.Equal(("cancelled", "new-apply"), (r.Handler.LastWindow()!.Outcome, r.Handler.LastWindow()!.Reason));
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
        var raised = r.Apply(confirm: null);
        Assert.True(raised.Ok);
        Assert.True(raised.Apply!.ConfirmArmed); Assert.Equal(20, raised.Apply.ConfirmSeconds);   // the client learns the real window, not what it asked
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
        r.Handler = new IntentHandler(svc, Path.Combine(r.Dir, "service"), r.Log.Add, settleMs: 0, liveness: r.Live) { Clock = () => r.Now, Monotonic = () => r.Mono };
        var resp = r.Handler.Handle(new ServiceRequest(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = "Ethernet", AdapterId = Fx.Id("Ethernet"), ConfirmSeconds = 10 }, "PC\\anna (session 2)", Tier.StandardUser);
        Assert.True(resp.Ok, resp.Error);
        Assert.Contains(r.Log, l => l.Contains("armed commit-confirm: 10 s") && l.Contains("probes: none, timer only"));   // a DHCP plan defines no gateway or DNS to probe
        r.Ticks(6);
        Assert.Contains(r.Runner.Ran, s => s.Arguments.Contains("source=static address=192.168.1.10"));
        Assert.False(r.Armed);
    }

    // ---- review of #153 ------------------------------------------------------------------------------------------

    [Fact]
    public void A_renew_on_the_armed_adapter_keeps_the_window_and_the_timeout_still_reverts()
    {
        // The case the feature exists for: a bad apply, the user clicks Renew to get the network back; the window and its snapshot must survive that.
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        foreach (var kind in new[] { RequestKind.Renew, RequestKind.Release, RequestKind.ResetAdapter, RequestKind.Prefer })
        {
            var resp = r.Send(kind, Tier.NetworkConfigOperators);
            Assert.True(resp.Ok, $"{kind}: {resp.Error}"); Assert.True(r.Armed, kind.ToString());
        }
        Assert.DoesNotContain(r.Log, l => l.Contains("cancelled by"));
        Assert.Equal(30, r.Handler.ArmedWindow()!.SecondsLeft);
        r.Ticks(16);
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("commit-confirm timeout: reverting 'lab'"));
    }

    [Fact]
    public void A_lower_tier_intent_cannot_remove_a_window_its_caller_may_not_confirm()
    {
        var r = new Rig(tiers: new() { [PolicyIntent.ApplyProfile] = Tier.NetworkConfigOperators });
        Assert.True(r.Apply(30, Tier.NetworkConfigOperators).Ok);
        Assert.False(r.Confirm(Tier.StandardUser).Ok);
        Assert.True(r.Send(RequestKind.Renew, Tier.StandardUser).Ok);   // Renew is StandardUser by default and runs; the window is not its to drop
        Assert.True(r.Armed);
        Assert.True(r.Send(RequestKind.Dhcp, Tier.NetworkConfigOperators).Ok);   // a kind that arms windows itself replaces it (new pre-state)
        Assert.False(r.Armed);
        Assert.Contains(r.Log, l => l.Contains("cancelled by a new apply ('DHCP'"));
    }

    [Fact]
    public void A_half_written_pending_file_is_kept_aside_reported_once_and_does_not_stop_the_service()
    {
        var r = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(r.Handler.PendingPath)!);
        File.WriteAllText(r.Handler.PendingPath, "[{\"Adapter\":\"Ethernet\",\"Id\":\"{0E");   // the power went while the old, non-atomic write ran
        r.Handler = r.NewHandler();
        Assert.Null(r.Handler.Interrupted());
        Assert.Contains(r.Log, l => l.Contains("confirm-pending.json is unreadable") && l.Contains("kept as confirm-pending.unreadable-"));
        Assert.False(File.Exists(r.Handler.PendingPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(r.Handler.PendingPath)!, "confirm-pending.unreadable-*.json"));
        r.Ticks(3);
        Assert.Equal(1, r.Log.Count(l => l.Contains("is unreadable")));   // reported once, not every tick
        Assert.True(r.Apply(30).Ok); Assert.True(r.Armed);               // and the service arms again, atomically: no temp file left behind
        Assert.False(File.Exists(r.Handler.PendingPath + ".tmp"));
        Assert.NotNull(JsonSerializer.Deserialize<List<IntentHandler.Pending>>(File.ReadAllText(r.Handler.PendingPath), ServiceProtocol.Json));
    }

    [Fact]
    public void A_revert_that_throws_is_retried_on_the_next_ticks_and_succeeds()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        var throws = 0;
        r.Runner.ThrowOn = s => s.Arguments.Contains("source=dhcp") && throws++ < 2 ? new IOException("the disk went away") : null;
        r.Ticks(16);                                            // 32 s: attempt 1 throws, stays "reverting"
        Assert.Equal(0, r.Reverts); Assert.True(File.Exists(r.Handler.PendingPath));
        Assert.Contains(r.Log, l => l.Contains("FAILED (the disk went away); attempt 1 of 3, retried on the next tick"));
        r.Ticks(1);                                             // attempt 2 throws
        Assert.Contains(r.Log, l => l.Contains("attempt 2 of 3"));
        r.Ticks(1);                                             // attempt 3 succeeds
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed); Assert.False(File.Exists(r.Handler.PendingPath));
        Assert.Contains(r.Log, l => l.Contains("reverted ok"));
        r.Ticks(5);
        Assert.Equal(1, r.Reverts);
    }

    [Fact]
    public void A_revert_that_keeps_throwing_is_given_up_after_three_attempts_and_never_re_armed()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        r.Runner.ThrowOn = s => s.Arguments.Contains("source=dhcp") ? new System.ComponentModel.Win32Exception("netsh would not start") : null;
        r.Ticks(18);                                            // 32 s + two retries
        Assert.Equal(0, r.Reverts); Assert.False(r.Armed); Assert.False(File.Exists(r.Handler.PendingPath));
        Assert.Contains(r.Log, l => l.Contains("given up after 3 attempts") && l.Contains("kept for RevertLastApply"));
        var attempts = r.Log.Count(l => l.Contains("FAILED (netsh would not start)"));
        r.Ticks(10); r.Handler.Interrupted();
        Assert.Equal(attempts, r.Log.Count(l => l.Contains("FAILED (netsh would not start)")));   // nothing retries a given-up window
        Assert.Equal(3, attempts);
        var la = r.Handler.LastApply()!;                        // the manual revert is still possible and returns to the state before 'lab', not to 'lab'
        Assert.True(la.Before.Dhcp); Assert.Equal("lab", la.Title);
    }

    [Fact]
    public void A_revert_that_throws_at_start_up_does_not_stop_the_service_and_is_retried_by_the_ticks()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        r.Now = r.Now.AddSeconds(45);
        var throws = 0;
        r.Runner.ThrowOn = s => s.Arguments.Contains("source=dhcp") && throws++ < 1 ? new IOException("NIC not enumerated yet") : null;
        r.Handler = r.NewHandler();
        Assert.Null(r.Handler.Interrupted());                   // OnStart would have failed before this fix
        Assert.Equal(0, r.Reverts); Assert.Contains(r.Log, l => l.Contains("attempt 1 of 3"));
        r.Ticks(1);
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
    }

    [Fact]
    public void A_failed_apply_arms_nothing_and_says_so_in_the_report()
    {
        var r = new Rig();
        r.Runner.FailContaining = "source=static";
        var resp = r.Apply(30);
        Assert.False(resp.Ok); Assert.False(r.Armed);
        Assert.False(resp.Apply!.ConfirmArmed); Assert.Contains("the apply did not succeed, so nothing was armed", resp.Apply.ConfirmNotArmed);
        Assert.Contains(resp.Apply.Warnings, w => w.StartsWith("commit-confirm was NOT armed: the apply did not succeed"));
        Assert.NotNull(r.Handler.LastApply());                  // the snapshot for a manual revert was written before the first step
    }

    [Fact]
    public void A_window_that_could_never_revert_is_refused_before_anything_runs()
    {
        // A static pre-state is an ad-hoc profile; with AllowUserProfiles=false the service itself could not restore it, so the window is refused up front.
        var r = new Rig(storePolicy: new() { ["AllowUserProfiles"] = false });
        r.Nics.Current = Fx.Adapter(dhcp: false, gw: "192.168.1.1");
        var resp = r.Send(RequestKind.Dhcp, Tier.NetworkConfigOperators, confirm: 30);
        Assert.False(resp.Ok); Assert.Contains("the pre-apply state of Ethernet is static and policy AllowUserProfiles=false forbids restoring it; nothing changed", resp.Error);
        Assert.Empty(r.Runner.Ran); Assert.False(r.Armed);
        Assert.True(r.Send(RequestKind.Dhcp, Tier.NetworkConfigOperators).Ok);   // without a window the same change is fine
    }

    [Fact]
    public void The_deadline_runs_on_the_in_process_clock_so_a_wall_clock_jump_neither_reverts_early_nor_late()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        r.Ticks(5);                                             // 10 s
        r.Now = r.Now.AddHours(1); r.Handler.Tick();            // w32time jumps forward after connectivity came back
        Assert.Equal(0, r.Reverts); Assert.True(r.Armed); Assert.Equal(20, r.Handler.ArmedWindow()!.SecondsLeft);
        r.Now = r.Now.AddHours(-2); r.Ticks(9);                 // and backward; 28 s on the in-process clock
        Assert.Equal(0, r.Reverts); Assert.True(r.Armed);
        r.Ticks(2);                                             // 32 s
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
    }

    [Fact]
    public void A_confirm_after_the_deadline_is_too_late()
    {
        var r = new Rig();
        Assert.True(r.Apply(30).Ok);
        r.Mono += 31_000; r.Now = r.Now.AddSeconds(31);         // past the deadline, before the next tick ran
        var c = r.Confirm();
        Assert.False(c.Ok); Assert.Contains("too late: the commit-confirm window on Ethernet has expired", c.Error);
        Assert.True(r.Armed);
        r.Handler.Tick();
        Assert.Equal(1, r.Reverts); Assert.False(r.Armed);
    }
}
