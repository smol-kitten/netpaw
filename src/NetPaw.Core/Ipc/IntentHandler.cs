using System.Text.Json;
using NetPaw.Adapters;
using NetPaw.Planning;

namespace NetPaw.Ipc;

/// <summary>
/// The service side of every change request (docs/PLAN-v0.14.md, "IPC"). Runs in netpaw-svc as LocalSystem
/// against a <see cref="NetPawService"/> whose store is the machine's service folder. Everything here is
/// plain Core code, so it is tested on any OS with fake adapters and a recording runner.
/// <list type="bullet">
/// <item>One change at a time: network changes are machine-wide and must not interleave.</item>
/// <item>Policy and managed profiles are re-read before every request (an admin may have changed them).</item>
/// <item>The adapter must be a live adapter; its name in the commands comes from Windows, not the request.</item>
/// <item>Before a plan runs it is written to <see cref="JournalPath"/> and removed afterwards, so a crash in
/// the middle is visible at the next start (<see cref="Interrupted"/>).</item>
/// </list>
/// </summary>
public sealed class IntentHandler(NetPawService svc, string journalDirectory, Action<string> log, int settleMs = 2500, PolicyTiers? policy = null, Updates.UpdateInstaller? installer = null, Connectivity.ILiveness? liveness = null)
{
    readonly Lock _gate = new();
    // PR 4: the one authorization point. When no policy is injected, an empty-but-valid source means the default
    // table applies — i.e. exactly today's behaviour. The cache lives in the (SYSTEM/Admins-owned) service folder.
    readonly PolicyTiers _policy = policy ?? new PolicyTiers(new EmptyPolicySource(), Path.Combine(journalDirectory, "policy.cache.json"), log);
    public string JournalPath { get; } = Path.Combine(journalDirectory, "inflight.json");
    /// <summary>G13: the adapter state captured just before the last journaled apply, what <see cref="RequestKind.RevertLastApply"/> restores.</summary>
    public string PreApplyPath { get; } = Path.Combine(journalDirectory, "last-apply.json");
    /// <summary>G4: the commit-confirm windows that are armed or being reverted, one entry per adapter id (docs/DESIGN-G4-commit-confirm.md).</summary>
    public string PendingPath { get; } = Path.Combine(journalDirectory, "confirm-pending.json");
    readonly Connectivity.ILiveness _liveness = liveness ?? new Connectivity.ProbeLiveness(new Connectivity.NetworkProbe());

    public sealed record Journal(string Title, string Adapter, string Caller, DateTimeOffset At, IReadOnlyList<string> Commands);

    /// <summary>Where Listen captures land: a folder the service owns (SYSTEM + Administrators ACL), created on demand
    /// by the host. Null = no safe folder, and Listen refuses rather than fall back to the temp folder.</summary>
    public Func<string>? CaptureDirectory { get; set; }
    /// <summary>Per-caller gap between Listen requests, so one user cannot keep pktmon busy at 120 s on repeat.</summary>
    public TimeSpan ListenCooldown { get; set; } = TimeSpan.FromSeconds(60);
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>G4: the in-process clock the deadline and the settle run on (ms, never jumps); the wall-clock deadline in the file is for restarts only,
    /// because an apply that restores connectivity can trigger a w32time correction (review of #153, item 5).</summary>
    public Func<long> Monotonic { get; set; } = () => Environment.TickCount64;
    readonly Dictionary<string, DateTimeOffset> _lastListen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Start-up: reports a change that was running when the service stopped (the file is kept, renamed, for the admin) and
    /// settles the commit-confirm windows (<see cref="Resume"/>).</summary>
    public Journal? Interrupted()
    {
        var j = InterruptedApply();
        Resume();
        return j;
    }

    Journal? InterruptedApply()
    {
        if (!File.Exists(JournalPath)) return null;
        Journal? j = null;
        try { j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath), ServiceProtocol.Json); } catch (JsonException) { }
        var keep = Path.Combine(journalDirectory, $"interrupted-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.Move(JournalPath, keep, overwrite: true);
        log($"interrupted apply found at start: {j?.Title ?? "unreadable journal"} on {j?.Adapter} by {j?.Caller}; kept as {Path.GetFileName(keep)}");
        return j;
    }

    /// <summary>Passive LLDP/CDP capture on behalf of an unelevated caller: same tier gate as every intent, the Capture
    /// capability, a fixed filter (LLDP ethertype + the two multicast MACs) and a clamped window. Runs outside the change
    /// gate; pktmon itself is one-at-a-time. Nothing is sent on the wire.</summary>
    ServiceResponse Listen(ServiceRequest r, string caller, Tier callerTier)
    {
        try
        {
            var overrides = _policy.Resolve();
            if (!_policy.Decide(PolicyIntent.Capture, callerTier, overrides, caller).Allowed)
                throw new Rejected($"Listen needs tier {PolicyTiers.Required(PolicyIntent.Capture, overrides)} or higher; you are {callerTier}");
            svc.Reload(); svc.Require(Capability.Capture);
            var adapter = Adapter(r);
            var seconds = Math.Clamp(r.Seconds ?? 35, 10, 120);
            lock (_lastListen)
            {
                var now = Clock();
                if (_lastListen.TryGetValue(caller, out var last) && now - last < ListenCooldown)
                    return ServiceResponse.Fail($"listen cooldown: wait {(int)Math.Ceiling((ListenCooldown - (now - last)).TotalSeconds)} s before the next capture");
                _lastListen[caller] = now;
            }
            var captureDir = CaptureDirectory?.Invoke();
            if (captureDir is null) return ServiceResponse.Fail("no service-owned capture folder is configured; refused");
            if (!Discovery.PktmonCapture.Available) return ServiceResponse.Fail("pktmon is not available on this Windows version");
            log($"listen: {seconds} s on {adapter.Name} for {caller}");
            var (found, err) = new Discovery.PktmonCapture().Listen(adapter.Name, adapter.Mac, seconds, null, log, options: new(Shared: true, WorkDir: captureDir)).GetAwaiter().GetResult();
            return err is not null ? ServiceResponse.Fail(err) : new ServiceResponse(ServiceProtocol.Version, true) { Neighbors = found };
        }
        catch (Rejected ex) { return ServiceResponse.Fail(ex.Message); }
        catch (DeniedByPolicyException ex) { return ServiceResponse.Fail("denied by policy: " + ex.Message); }
        catch (InvalidOperationException ex) { return ServiceResponse.Fail(ex.Message); }
    }

    /// <summary>Maps a change request to its policy intent (what the tier gate keys on).</summary>
    static PolicyIntent IntentFor(RequestKind k) => k switch
    {
        RequestKind.ApplyProfile => PolicyIntent.ApplyProfile,
        RequestKind.Dhcp => PolicyIntent.Dhcp,
        RequestKind.Reach => PolicyIntent.Reach,
        RequestKind.Renew => PolicyIntent.Renew,
        RequestKind.ClearTemp => PolicyIntent.ClearTemp,
        RequestKind.DeleteRoute => PolicyIntent.DeleteRoute,
        RequestKind.ResetAdapter => PolicyIntent.ResetAdapter,
        RequestKind.Release => PolicyIntent.Release,
        RequestKind.Prefer => PolicyIntent.Prefer,
        RequestKind.SetMtu => PolicyIntent.SetMtu,
        RequestKind.RevertLastApply => PolicyIntent.RevertLastApply,
        RequestKind.InstallUpdate => PolicyIntent.InstallUpdate,
        RequestKind.Listen => PolicyIntent.Capture,
        RequestKind.ConfirmApply => PolicyIntent.ConfirmApply,
        _ => throw new Rejected($"{k} is not a change request"),
    };

    /// <summary>Test-only shim (internal): a bool "privileged" maps to Network Configuration Operators, absent it
    /// to StandardUser. It deliberately cannot express Admin, so production uses the Tier overload; the pipe server
    /// always passes the real caller tier.</summary>
    internal ServiceResponse Handle(ServiceRequest r, string caller, bool privileged) =>
        Handle(r, caller, privileged ? Tier.NetworkConfigOperators : Tier.StandardUser);

    /// <summary>Handles one change request: tier gate, reload, dispatch by kind; every failure becomes a ServiceResponse, never an exception.</summary>
    public ServiceResponse Handle(ServiceRequest r, string caller, Tier callerTier)
    {
        if (r.Kind == RequestKind.Listen) return Listen(r, caller, callerTier);   // read-only and long (10..120 s): must not hold the change gate
        lock (_gate)
        {
            try
            {
                var overrides = _policy.Resolve();
                if (!_policy.Decide(IntentFor(r.Kind), callerTier, overrides, caller).Allowed)
                    throw new Rejected($"{r.Kind} needs tier {PolicyTiers.Required(IntentFor(r.Kind), overrides)} or higher; you are {callerTier} (the audit line is in the Application event log, source NetPaw: 'tier decision')");
                svc.Reload();
                var confirm = Window(r, overrides);   // G4: validated for every kind, so a window on an intent that cannot carry one is refused, not ignored
                var report = r.Kind switch
                {
                    RequestKind.ApplyProfile => ApplyProfile(r, caller, callerTier, overrides, confirm),
                    RequestKind.Dhcp => Run(ApplyPlanner.PlanDhcp(Adapter(r)), caller, require: Capability.Dhcp, confirm: confirm, replacesWindow: true),
                    RequestKind.Reach => Reach(r, caller, confirm),
                    RequestKind.ClearTemp => ClearTemp(),
                    RequestKind.DeleteRoute => DeleteRoute(r, caller),
                    RequestKind.ResetAdapter => Run(ApplyPlanner.PlanResetAdapter(Adapter(r)), caller),
                    RequestKind.Renew => Run(Connectivity.Advisor.PlanRenew(Adapter(r)), caller, require: Capability.Dhcp),
                    RequestKind.Release => Run(Connectivity.Advisor.PlanRelease(Adapter(r)), caller),
                    RequestKind.Prefer => Run(Connectivity.Advisor.PlanPrefer(Adapter(r), svc.GetAdapters()), caller),
                    RequestKind.SetMtu => SetMtu(r, caller),
                    RequestKind.RevertLastApply => RevertLastApply(caller, callerTier, overrides, confirm),
                    RequestKind.InstallUpdate => InstallUpdate(r, caller),
                    RequestKind.ConfirmApply => Confirm(r, caller, callerTier),
                    _ => throw new Rejected($"{r.Kind} is not a change request"),
                };
                return new(ServiceProtocol.Version, report.Success) { Apply = report, Error = report.Success ? null : "one or more steps failed" };
            }
            catch (Rejected ex) { return ServiceResponse.Fail(ex.Message); }
            catch (DeniedByPolicyException ex) { return ServiceResponse.Fail("denied by policy: " + ex.Message); }
            catch (InvalidOperationException ex) { return ServiceResponse.Fail(ex.Message); }   // e.g. adapter not found
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return ServiceResponse.Fail("request refused: " + ex.Message); }
        }
    }

    sealed class Rejected(string why) : Exception(why);

    /// <summary>AA:BB:CC:DD:EE:FF, either case; what Windows reports and what the journal stores.</summary>
    public static bool IsMac(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$");

    /// <summary>Resolves the request's adapter against the live list (G5: id first, MAC cross-checked, alias only for a request
    /// that carries no id, logged) and refuses an unsafe name.</summary>
    AdapterInfo Adapter(ServiceRequest r)
    {
        if (r.Adapter is { Length: > 256 }) throw new Rejected("adapter name too long");
        if (r.AdapterId is { Length: > 64 } || r.AdapterMac is { Length: > 32 }) throw new Rejected("adapter id or MAC too long");
        // Both are logged and compared, never run; still, only a GUID and a colon-hex MAC get that far (log forging, #150 review).
        if (r.AdapterId is { Length: > 0 } && !Guid.TryParse(r.AdapterId, out _)) throw new Rejected("adapter id must be a GUID");
        if (r.AdapterMac is { Length: > 0 } && !IsMac(r.AdapterMac)) throw new Rejected("adapter MAC must be six colon-separated hex pairs");
        var a = svc.ResolveAdapter(r.Adapter, id: r.AdapterId, mac: r.AdapterMac);
        if (r.AdapterId is null) log($"adapter '{a.Name}' resolved by {(r.AdapterMac is { Length: > 0 } ? "MAC" : "alias")}: the request carried no adapter id (a client older than 0.17, or a name-only command)");
        return Safe(a);
    }

    /// <summary>A resolved name is still untrusted (a user can name a VPN phonebook entry anything). The ipconfig steps, the
    /// VLAN step and every warning put the name into a command or a line, so refuse an unsafe one centrally.</summary>
    static AdapterInfo Safe(AdapterInfo a)
    {
        if (!ProfileGuard.IsSafeAdapterName(a.Name)) throw new Rejected($"adapter name '{a.Name}' contains characters that are not allowed");
        return a;
    }

    /// <summary>ApplyProfile: managed by id or inline profile, guard, appliesTo binding, virtual-adapter gate, then plan and apply.</summary>
    ApplyReport ApplyProfile(ServiceRequest r, string caller, Tier callerTier, IReadOnlyDictionary<PolicyIntent, Tier> overrides, ConfirmSpec? confirm = null)
    {
        Model.Profile p;
        if (r.ProfileId is not null)
        {
            if (r.Profile is not null) throw new Rejected("send a profile id or a profile, not both");
            p = svc.ManagedProfiles.FirstOrDefault(x => x.Id == r.ProfileId) ?? throw new Rejected($"no managed profile with id {r.ProfileId}");
            // Managed profiles are validated too: a file planted under 0.13.x survives the upgrade and is still
            // owned by the user who wrote it, so "it came from profiles.d" is not on its own a reason to trust it.
            var bad = ProfileGuard.Check(p);
            if (bad.Count > 0) throw new Rejected($"managed profile '{p.Name}' refused: {string.Join(" ", bad)}");
        }
        else
        {
            p = r.Profile ?? throw new Rejected("ApplyProfile needs a profile id or a profile");
            svc.Require(Capability.UserProfiles);
            p.Managed = false;
            var errors = ProfileGuard.Check(p);
            if (errors.Count > 0) throw new Rejected("profile refused: " + string.Join(" ", errors));
        }
        // No adapter in the request: the profile's own binding (G5: id, then MAC, then alias; a bound adapter that is not present refuses).
        var bound = p.AdapterId is { Length: > 0 } || p.AdapterMac is { Length: > 0 } || p.Adapter is not null;
        var adapter = r.Adapter is null && r.AdapterId is null && r.AdapterMac is null && bound ? Safe(svc.AdapterFor(p)) : Adapter(r);
        if (r.Adapter is null && r.AdapterId is null && r.AdapterMac is null && bound && p.AdapterId is null)
            log($"adapter '{adapter.Name}' resolved by the profile's {(p.AdapterMac is { Length: > 0 } ? "MAC" : "alias")}: profile '{p.Name}' carries no adapter id");
        // appliesTo binding + virtual-adapter guard (t-0e5511).
        var listed = p.AppliesTo is { IsEmpty: false } b && b.Matches(adapter.Name, adapter.Mac);
        // RESTRICT: when appliesTo is present it binds the profile for ANY caller — no match, no apply.
        if (p.AppliesTo is { IsEmpty: false } bind && !listed)
            throw new Rejected($"profile '{p.Name}' is restricted to [{bind.Describe()}] and does not match adapter '{adapter.Name}' ({adapter.Mac})");
        // UNLOCK a non-host-facing (virtual) adapter: a managed (admin-defined) profile may, or a caller that the
        // VirtualUnlock tier gate (default NCO, policy floor NCO) allows. A standard user's own inline appliesTo
        // must not let them reach a VPN/TAP adapter. Routed through Decide so the decision is audited.
        if (!adapter.HostFacing)
        {
            var mayUnlock = listed && (p.Managed || _policy.Decide(PolicyIntent.VirtualUnlock, callerTier, overrides, caller).Allowed);
            if (!mayUnlock)
                throw new Rejected($"'{adapter.Name}' is a virtual adapter; only a managed profile or a caller at tier {PolicyTiers.Required(PolicyIntent.VirtualUnlock, overrides)} may target it (via appliesTo)");
        }
        var plan = svc.PlanProfile(p, adapter);
        return Journaled(plan, caller, () => svc.ApplyAndVerify(plan, settleMs), confirm, replacesWindow: true);
    }

    /// <summary>Reach: validates the target, resolves the decision and applies the temporary address.</summary>
    ApplyReport Reach(ServiceRequest r, string caller, ConfirmSpec? confirm = null)
    {
        if (!ProfileGuard.IsIPv4OrPrefix(r.Target)) throw new Rejected("reach target must be an IPv4 address or a.b.c.d/n");
        var adapter = Adapter(r);
        var d = svc.ResolveReach(r.Target!, adapter);
        var (plan, _) = svc.Reach(d, adapter, dryRun: true, r.ReplacePrimary);
        return Journaled(plan, caller, () => svc.Reach(d, adapter, dryRun: false, r.ReplacePrimary).Outcome, confirm, replacesWindow: true);
    }

    /// <summary>ClearTemp: removes every temporary address the service tracks.</summary>
    /// <summary>Update from CatWinget: capability gate, then the installer does resolve → download → verify twice → msiexec.
    /// The request carries a version and a channel at most; never a URL or a path.</summary>
    ApplyReport InstallUpdate(ServiceRequest r, string caller)
    {
        svc.Require(Capability.CatWingetUpdate);
        if (installer is null) throw new Rejected("updates from CatWinget are not available in this service build");
        if (r.Channel is not null && r.Channel is not ("stable" or "prerelease")) throw new Rejected("channel must be stable or prerelease");
        log($"update: requested by {caller} (version {r.Version ?? "newest"}, channel {r.Channel ?? "stable"}, downgrade {(r.AllowDowngrade == true ? "allowed" : "refused")})");
        return installer.Install(r.Version, r.Channel, r.AllowDowngrade == true).GetAwaiter().GetResult();
    }

    ApplyReport ClearTemp()
    {
        var outcome = svc.ClearTemp();
        var plan = new ApplyPlan { Title = "clear temporary addresses", Adapter = "" };
        if (outcome is null) plan.Warnings.Add("No temporary addresses to remove.");
        return ApplyReport.From(plan, outcome);
    }

    /// <summary>DeleteRoute: validates prefix and gateway, then runs the delete plan.</summary>
    ApplyReport DeleteRoute(ServiceRequest r, string caller)
    {
        if (r.RoutePrefix is null || !r.RoutePrefix.Contains('/') || !ProfileGuard.IsIPv4OrPrefix(r.RoutePrefix)) throw new Rejected("route prefix must be a.b.c.d/n");
        if (r.RouteGateway is not null && !Net.IpMath.IsIPv4(r.RouteGateway)) throw new Rejected("route gateway must be an IPv4 address");
        var adapter = Adapter(r);
        return Run(ApplyPlanner.PlanDeleteRoute(new RouteEntry(r.RoutePrefix, adapter.Index, r.RouteGateway, 0), adapter), caller);
    }

    /// <summary>SetMtu: validates 576..9000, then runs the MTU plan.</summary>
    ApplyReport SetMtu(ServiceRequest r, string caller)
    {
        if (r.Mtu is not (>= 576 and <= 9000)) throw new Rejected("MTU must be 576..9000");
        return Run(Connectivity.Advisor.PlanSetMtu(Adapter(r), r.Mtu!.Value), caller);
    }

    /// <summary>Runs a plan through the journal, after an optional capability check.</summary>
    ApplyReport Run(ApplyPlan plan, string caller, Capability? require = null, ConfirmSpec? confirm = null, bool replacesWindow = false)
    {
        if (require is { } c) svc.Require(c);
        return Journaled(plan, caller, () => svc.Apply(plan), confirm, replacesWindow);
    }

    // ---- G13: pre-apply snapshot + revert ------------------------------------------------------------------------

    /// <summary>What the adapter looked like right before an apply: enough to put it back — DHCP on/off, IPv4 addresses, the
    /// first gateway and the DNS servers. NOT captured: metrics, DNS suffix, VLAN, routes, IPv6, and "DHCP address + static DNS"
    /// (a DHCP pre-state restores DNS from DHCP too, as PlanDhcp does).</summary>
    /// <param name="Id">G5: the adapter's NetCfgInstanceId; a revert resolves by it first, then checks the MAC. Null in a snapshot
    /// written by 0.16 (alias + MAC only).</param>
    public sealed record PreApply(string Adapter, string Mac, string Title, string Caller, DateTimeOffset At, Model.Profile Before, string? Id = null);

    /// <summary>Writes last-apply.json with the adapter's state before the first command; drops the previous snapshot on failure.
    /// Returns the snapshot (G4: the state a commit-confirm window returns to), null when none was written.</summary>
    PreApply? SavePreState(ApplyPlan plan, string caller)
    {
        if (string.IsNullOrEmpty(plan.Adapter)) return null;   // clear-temp and the like: no adapter is touched, the last snapshot stays valid
        try
        {
            // G5: by the id the plan was built from (a rename in between must not snapshot another adapter); by alias only for a plan without one.
            var live = svc.GetAdapters();
            var a = plan.AdapterId is { Length: > 0 } ? live.FirstOrDefault(x => string.Equals(x.Id, plan.AdapterId, StringComparison.OrdinalIgnoreCase))
                                                      : live.FirstOrDefault(x => string.Equals(x.Name, plan.Adapter, StringComparison.OrdinalIgnoreCase));
            if (a is null) { DropPreState("adapter not found"); return null; }   // never leave a PREVIOUS apply's snapshot as "last apply" (review of #105)
            var before = new Model.Profile { Name = "pre-apply", Adapter = a.Name, AdapterId = a.Id, AdapterMac = a.Mac, Dhcp = a.Dhcp };
            if (!a.Dhcp) { before.Addresses = [.. a.Addresses]; before.Gateway = a.Gateways.FirstOrDefault(); before.Dns = [.. a.Dns]; }
            var pre = new PreApply(a.Name, a.Mac, plan.Title, caller, DateTimeOffset.Now, before, a.Id);
            File.WriteAllText(PreApplyPath, JsonSerializer.Serialize(pre, ServiceProtocol.Json));
            return pre;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DropPreState(ex.Message); return null; }
    }

    /// <summary>Deletes the previous snapshot and logs why nothing can be reverted after this apply.</summary>
    void DropPreState(string why)
    {
        try { File.Delete(PreApplyPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("stale pre-apply snapshot could not be removed: " + ex.Message); }
        log("pre-apply snapshot not written (" + why + "); nothing to revert after this apply");
    }

    /// <summary>The recorded pre-apply state, or null when there is none (or it is unreadable).</summary>
    public PreApply? LastApply()
    {
        try { return File.Exists(PreApplyPath) ? JsonSerializer.Deserialize<PreApply>(File.ReadAllText(PreApplyPath), ServiceProtocol.Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>One line for Status / the tray menu: "DHCP on Ethernet 2 at 00:58 by PC\anna (session 2)".</summary>
    public string? LastApplySummary() => LastApply() is { } la ? $"{la.Title} on {la.Adapter} at {la.At:HH:mm} by {la.Caller}" : null;

    /// <summary>RevertLastApply: re-applies the snapshot to the same adapter (G5: by id, MAC cross-checked; a 0.16 snapshot by alias + MAC),
    /// with the virtual-adapter and user-profile gates.</summary>
    ApplyReport RevertLastApply(string caller, Tier callerTier, IReadOnlyDictionary<PolicyIntent, Tier> overrides, ConfirmSpec? confirm = null)
    {
        var pre = LastApply() ?? throw new Rejected("nothing to revert: no apply has been recorded");
        return RevertTo(pre, caller, callerTier, overrides, confirm);
    }

    /// <summary>Re-applies one snapshot: the manual revert (the caller's tier) and the G4 commit-confirm revert (the service for itself, tier Admin).</summary>
    ApplyReport RevertTo(PreApply pre, string caller, Tier callerTier, IReadOnlyDictionary<PolicyIntent, Tier> overrides, ConfirmSpec? confirm = null)
    {
        AdapterInfo adapter;
        try { adapter = Adapter(new ServiceRequest(ServiceProtocol.Version, RequestKind.RevertLastApply) { Adapter = pre.Adapter, AdapterId = pre.Id, AdapterMac = pre.Mac }); }
        catch (InvalidOperationException ex) { throw new Rejected($"'{pre.Adapter}' is not the adapter the last apply changed any more ({ex.Message}); nothing reverted"); }
        if (!string.Equals(adapter.Mac, pre.Mac, StringComparison.OrdinalIgnoreCase))
            throw new Rejected($"'{pre.Adapter}' is not the adapter the last apply changed any more (MAC differs); nothing reverted");
        // A managed profile may have targeted a VPN/TAP adapter (appliesTo); a revert must not let a lower tier flip that
        // non-host-facing adapter between two states — same gate as the apply, audited (review of #105).
        if (!adapter.HostFacing && !_policy.Decide(PolicyIntent.VirtualUnlock, callerTier, overrides, caller).Allowed)
            throw new Rejected($"'{adapter.Name}' is a virtual adapter; reverting it needs tier {PolicyTiers.Required(PolicyIntent.VirtualUnlock, overrides)} or higher; you are {callerTier}");
        log($"revert to the pre-apply state for {caller}: undoing '{pre.Title}' on {pre.Adapter} ({pre.At:HH:mm}, by {pre.Caller})");
        if (pre.Before.Dhcp)
        {
            svc.Require(Capability.Dhcp);
            return Journaled(ApplyPlanner.PlanDhcp(adapter), caller, () => svc.Apply(ApplyPlanner.PlanDhcp(adapter)), confirm, replacesWindow: true);
        }
        // A static pre-state is an ad-hoc profile: with AllowUserProfiles=false only a DHCP pre-state can be restored.
        svc.Require(Capability.UserProfiles);
        var plan = svc.PlanProfile(pre.Before, adapter);
        return Journaled(plan, caller, () => svc.ApplyAndVerify(plan, settleMs), confirm, replacesWindow: true);
    }

    /// <summary>Writes the pre-state and inflight.json, runs the apply, removes inflight.json, logs the result; G4: arms the confirm window after a successful apply.</summary>
    /// <param name="replacesWindow">True for the kinds that can arm a window themselves (ApplyProfile, Dhcp, Reach, RevertLastApply): their snapshot is the new
    /// pre-state, so an armed window on the adapter is cancelled. False for Renew, Release, ResetAdapter, SetMtu, Prefer and the rest: the armed window stays and
    /// still reverts to the snapshot that predates them (review of #153: a Renew after a bad apply must not drop the safety net, and a lower-tier intent must
    /// not be able to remove a window its caller may not confirm).</param>
    ApplyReport Journaled(ApplyPlan plan, string caller, Func<ApplyOutcome?> apply, ConfirmSpec? confirm = null, bool replacesWindow = false)
    {
        if (plan.IsEmpty) return ApplyReport.From(plan, null);
        Directory.CreateDirectory(journalDirectory);
        if (replacesWindow) CancelPending(plan.AdapterId, $"a new apply ('{plan.Title}' by {caller})");   // G4: the snapshot taken next is the new pre-state, so the old window would restore the wrong one
        var pre = SavePreState(plan, caller);   // G13: written before the first command runs, so a revert is possible even after a half-done apply
        if (confirm is not null) RefuseUnlessRevertible(plan, pre);   // G4: a window that could never revert is refused before anything changes
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(new Journal(plan.Title, plan.Adapter, caller, DateTimeOffset.Now,
            plan.Steps.Select(s => s.CommandLine).ToList()), ServiceProtocol.Json));
        ApplyOutcome? outcome;
        try { outcome = apply(); }
        finally { File.Delete(JournalPath); }
        var report = ApplyReport.From(plan, outcome);
        log($"{plan.Title} on {plan.AdapterLabel} for {caller}: {(report.Success ? "ok" : "FAILED")}{(report.VerifyDiffs.Count > 0 ? " (verify: " + string.Join("; ", report.VerifyDiffs) + ")" : "")}");
        if (confirm is not { } c) return report;
        var notArmed = report.Success ? Arm(plan, pre!, c, caller) : "the apply did not succeed, so nothing was armed; the pre-apply snapshot is kept for RevertLastApply";
        if (notArmed is null) return report with { ConfirmArmed = true };
        plan.Warnings.Add("commit-confirm was NOT armed: " + notArmed);
        return ApplyReport.From(plan, outcome) with { ConfirmArmed = false, ConfirmNotArmed = notArmed };
    }

    /// <summary>A requested window needs a snapshot the service can restore: one was written, and a static pre-state is not barred by
    /// AllowUserProfiles=false (the revert runs as the service, but that capability gate still applies to an ad-hoc profile).</summary>
    void RefuseUnlessRevertible(ApplyPlan plan, PreApply? pre)
    {
        if (pre?.Id is null) throw new Rejected($"commit-confirm refused for '{plan.Title}': no pre-apply snapshot could be taken, so the service could not revert; nothing changed (apply without a window, or fix the service store)");
        if (!pre.Before.Dhcp && !svc.Allowed(Capability.UserProfiles))
            throw new Rejected($"commit-confirm refused for '{plan.Title}': the pre-apply state of {pre.Adapter} is static and policy AllowUserProfiles=false forbids restoring it; nothing changed");
    }

    // ---- G4: commit-confirmed apply (docs/DESIGN-G4-commit-confirm.md) ---------------------------------------------

    public const int MinConfirmSeconds = 5, MaxConfirmSeconds = 600;
    /// <summary>Consecutive ticks in which every defined probe failed before the service reverts for lost liveness.</summary>
    public const int FailedTicksToRevert = 3;
    const string Armed = "armed", Reverting = "reverting";

    /// <summary>The window an apply runs under: seconds, the tier a confirm needs (the larger of the ConfirmApply tier and the tier the
    /// apply itself needed, review decision 1), and "policy" when <c>CommitConfirmMinSeconds</c> raised the request.</summary>
    public sealed record ConfirmSpec(int Seconds, Tier Tier, string? Reason);

    /// <summary>One armed window as persisted: enough to revert without any other file (its own snapshot), what to probe, who may confirm.</summary>
    public sealed record Pending(string Adapter, string Id, string Mac, string Title, string Caller, DateTimeOffset ArmedAt, DateTimeOffset Deadline,
        string? Gateway, IReadOnlyList<string> Dns, [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Tier>))] Tier ConfirmTier,
        PreApply Before, string State = Armed, string? Reason = null, int FailedTicks = 0, string? RevertReason = null, int RevertAttempts = 0)
    {
        public bool HasProbes => Gateway is { Length: > 0 } || Dns.Count > 0;
        public string Probes => HasProbes ? string.Join(", ", new[] { Gateway is { Length: > 0 } ? "gateway " + Gateway : null, Dns.Count > 0 ? "dns " + Dns[0] : null }.Where(x => x is not null)) : "none, timer only";
    }

    static bool SameNic(Pending p, string id) => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Revert attempts before a window is given up (logged); an attempt fails on any exception, e.g. the adapter not enumerated yet at boot.</summary>
    public const int MaxRevertAttempts = 3;

    /// <summary>In-process deadlines on <see cref="Monotonic"/> per adapter id: when the window times out and until when probes do not count. Filled at
    /// Arm and at Resume; a window the file knows but this dictionary does not (never, unless the file was edited) falls back to the wall clock.</summary>
    sealed record Clocks(long DeadlineMs, long SettleUntilMs);
    readonly Dictionary<string, Clocks> _clocks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Revert attempts per adapter id in memory too, so the limit holds even when the file cannot record them (a dead disk).</summary>
    readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    bool Due(Pending p, DateTimeOffset now) => _clocks.TryGetValue(p.Id, out var c) ? Monotonic() >= c.DeadlineMs : now >= p.Deadline;
    bool Settling(Pending p, DateTimeOffset now) => _clocks.TryGetValue(p.Id, out var c) ? Monotonic() < c.SettleUntilMs : now < p.ArmedAt.AddMilliseconds(settleMs);
    int SecondsLeft(Pending p) => Math.Max(0, (int)Math.Ceiling((_clocks.TryGetValue(p.Id, out var c) ? (c.DeadlineMs - Monotonic()) / 1000.0 : (p.Deadline - Clock()).TotalSeconds)));

    /// <summary>The client's ConfirmSeconds validated (0/null = off, else 5..600), raised to the policy minimum, on the four intents that
    /// can carry one. Null = no window.</summary>
    ConfirmSpec? Window(ServiceRequest r, IReadOnlyDictionary<PolicyIntent, Tier> overrides)
    {
        var asked = r.ConfirmSeconds ?? 0;
        var eligible = r.Kind is RequestKind.ApplyProfile or RequestKind.Dhcp or RequestKind.Reach or RequestKind.RevertLastApply;
        if (asked != 0 && !eligible) throw new Rejected($"a confirm window applies to ApplyProfile, Dhcp, Reach and RevertLastApply, not {r.Kind}");
        if (asked != 0 && asked is < MinConfirmSeconds or > MaxConfirmSeconds) throw new Rejected($"confirm window must be {MinConfirmSeconds}..{MaxConfirmSeconds} s (0 = off)");
        if (!eligible) return null;
        var min = Math.Clamp(svc.Policy.CommitConfirmMinSeconds ?? 0, 0, MaxConfirmSeconds);
        if (min is > 0 and < MinConfirmSeconds) min = MinConfirmSeconds;
        var seconds = asked; string? reason = null;
        if (min > asked) { seconds = min; reason = "policy"; }
        if (seconds == 0) return null;
        var tier = (Tier)Math.Max((int)PolicyTiers.Required(PolicyIntent.ConfirmApply, overrides), (int)PolicyTiers.Required(IntentFor(r.Kind), overrides));
        return new ConfirmSpec(seconds, tier, reason);
    }

    List<Pending> LoadPending()
    {
        try { return File.Exists(PendingPath) ? JsonSerializer.Deserialize<List<Pending>>(File.ReadAllText(PendingPath), ServiceProtocol.Json) ?? [] : []; }
        catch (JsonException ex)
        {
            // Not written by SavePending (that write is atomic): kept aside for the admin, like an interrupted journal, so it is reported once, not every tick.
            var keep = Path.Combine(journalDirectory, $"confirm-pending.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            try { File.Move(PendingPath, keep, overwrite: true); } catch (Exception mv) when (mv is IOException or UnauthorizedAccessException) { keep = "(could not be moved aside: " + mv.Message + ")"; }
            log($"confirm-pending.json is unreadable, no window is armed: {ex.Message}; kept as {Path.GetFileName(keep)}");
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("confirm-pending.json is unreadable, no window is armed: " + ex.Message); return []; }
    }

    /// <summary>Atomic: a temp file in the same folder, flushed to disk, then moved over the old one. A power cut mid-write (likely right after a change
    /// broke connectivity) leaves either the previous list or the new one, never a half file that would lose the revert.</summary>
    void SavePending(List<Pending> list)
    {
        if (list.Count == 0) { File.Delete(PendingPath); return; }
        Directory.CreateDirectory(journalDirectory);
        var tmp = PendingPath + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(JsonSerializer.SerializeToUtf8Bytes(list, ServiceProtocol.Json));
            f.Flush(flushToDisk: true);
        }
        File.Move(tmp, PendingPath, overwrite: true);
    }

    /// <summary>Arms the window after a successful apply. Null when armed; otherwise the reason (the file could not be written) — the caller is told in
    /// <see cref="ApplyReport.ConfirmNotArmed"/>. The snapshot itself was checked before the apply ran (<see cref="RefuseUnlessRevertible"/>).</summary>
    string? Arm(ApplyPlan plan, PreApply pre, ConfirmSpec c, string caller)
    {
        var now = Clock();
        var p = new Pending(pre.Adapter, pre.Id!, pre.Mac, plan.Title, caller, now, now.AddSeconds(c.Seconds), plan.Profile?.Gateway, plan.Profile?.Dns ?? [], c.Tier, pre, Reason: c.Reason);
        try
        {
            var list = LoadPending(); list.RemoveAll(x => SameNic(x, pre.Id!)); list.Add(p); SavePending(list);
            var mono = Monotonic();
            _clocks[p.Id] = new Clocks(mono + c.Seconds * 1000L, mono + settleMs);
            log($"armed commit-confirm: {c.Seconds} s on {plan.AdapterLabel} for {caller} ({c.Reason ?? "requested"}; confirm needs tier {c.Tier}; probes: {p.Probes})");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var why = $"the window could not be written ({ex.Message}); the change is in place with no automatic revert, revert by hand (RevertLastApply) if needed";
            log($"commit-confirm not armed for '{plan.Title}': {why}"); return why;
        }
    }

    /// <summary>Drops the armed window on this adapter (a new apply or a manual revert replaces its pre-state). A window being reverted is left alone.</summary>
    void CancelPending(string? adapterId, string why)
    {
        if (adapterId is null) return;
        var list = LoadPending();
        var gone = list.Where(x => x.State == Armed && SameNic(x, adapterId)).ToList();
        if (gone.Count == 0) return;
        list.RemoveAll(gone.Contains);
        SavePending(list);
        foreach (var p in gone) { _clocks.Remove(p.Id); _attempts.Remove(p.Id); log($"commit-confirm window on {p.Adapter} ('{p.Title}' by {p.Caller}) cancelled by {why}"); }
    }

    /// <summary>ConfirmApply: the window on the named adapter (id, then MAC, then alias; the only one when none is named) is closed by a caller
    /// of the stored tier or higher. The audit line names both callers.</summary>
    ApplyReport Confirm(ServiceRequest r, string caller, Tier callerTier)
    {
        if (r.Adapter is { Length: > 256 } || r.AdapterId is { Length: > 64 } || r.AdapterMac is { Length: > 32 }) throw new Rejected("adapter name, id or MAC too long");
        if (r.AdapterId is { Length: > 0 } && !Guid.TryParse(r.AdapterId, out _)) throw new Rejected("adapter id must be a GUID");
        if (r.AdapterMac is { Length: > 0 } && !IsMac(r.AdapterMac)) throw new Rejected("adapter MAC must be six colon-separated hex pairs");
        if (r.Adapter is { Length: > 0 } && !ProfileGuard.IsSafeAdapterName(r.Adapter)) throw new Rejected("adapter name contains characters that are not allowed");
        var list = LoadPending();
        var armed = list.Where(x => x.State == Armed).ToList();
        if (armed.Count == 0) throw new Rejected("nothing to confirm: no commit-confirm window is armed");
        static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var p = r.AdapterId is { Length: > 0 } ? armed.FirstOrDefault(x => Eq(x.Id, r.AdapterId))
              : r.AdapterMac is { Length: > 0 } ? armed.FirstOrDefault(x => Eq(x.Mac, r.AdapterMac))
              : r.Adapter is { Length: > 0 } ? armed.FirstOrDefault(x => Eq(x.Adapter, r.Adapter))
              : armed.Count == 1 ? armed[0] : throw new Rejected("more than one commit-confirm window is armed: name the adapter");
        if (p is null) throw new Rejected($"no commit-confirm window is armed on adapter {r.AdapterId ?? r.AdapterMac ?? r.Adapter}");
        if (callerTier < p.ConfirmTier) throw new Rejected($"confirming '{p.Title}' on {p.Adapter} needs tier {p.ConfirmTier} or higher; you are {callerTier}");
        if (Due(p, Clock())) throw new Rejected($"too late: the commit-confirm window on {p.Adapter} has expired; the service reverts '{p.Title}' on its next tick");
        var left = SecondsLeft(p);
        list.Remove(p); SavePending(list); _clocks.Remove(p.Id); _attempts.Remove(p.Id);
        log($"commit-confirm confirmed by {caller}: '{p.Title}' on {p.Adapter} stays (armed by {p.Caller}, {left} s were left)");
        return ApplyReport.From(new ApplyPlan { Title = $"confirm '{p.Title}'", Adapter = p.Adapter, AdapterId = p.Id }, null);
    }

    /// <summary>For Status: the armed window (alias, seconds left, who armed it, "policy" when the minimum did), or null.</summary>
    public PendingConfirm? ArmedWindow()
    {
        lock (_gate)   // never reads the file while a change or a tick writes it (a false "unreadable" otherwise)
        {
            var p = LoadPending().FirstOrDefault(x => x.State == Armed);
            return p is null ? null : new PendingConfirm(p.Adapter, SecondsLeft(p), p.Caller, p.Reason);
        }
    }

    /// <summary>The service timer's call (every 2 s; tests call it by hand with a fake clock): a window past its deadline is reverted
    /// (reason timeout); after the settle, a window whose every defined probe failed on three consecutive ticks is reverted (reason
    /// liveness); a window left in "reverting" by a crash is reverted once more. Never throws. Skips the tick while a change holds the gate.</summary>
    public void Tick()
    {
        if (!_gate.TryEnter()) return;
        try { TickLocked(); }
        catch (Exception ex) { log("commit-confirm tick failed: " + ex.Message); }   // broad on purpose: a timer callback must never take the service down
        finally { _gate.Exit(); }
    }

    void TickLocked()
    {
        var list = LoadPending();
        if (list.Count == 0) return;
        var now = Clock();
        var changed = false;
        foreach (var p in list.ToList())
        {
            if (p.State == Reverting) { Revert(list, p, p.RevertReason ?? "interrupted"); continue; }
            if (Due(p, now)) { Revert(list, p, "timeout"); continue; }
            if (!p.HasProbes || Settling(p, now)) continue;   // R5 timer-only; settle: a lease or a link renegotiation is not a loss
            var failed = Alive(p) ? 0 : p.FailedTicks + 1;
            if (failed == p.FailedTicks) continue;
            var updated = p with { FailedTicks = failed };
            list[list.IndexOf(p)] = updated; changed = true;
            if (failed >= FailedTicksToRevert) { Revert(list, updated, "liveness"); changed = false; }
        }
        if (changed) SavePending(list);
    }

    /// <summary>One probe round: the gateway by ICMP and the first DNS server by one query; alive when any defined probe answers.</summary>
    bool Alive(Pending p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if (p.Gateway is { Length: > 0 } && _liveness.GatewayUp(p.Gateway, cts.Token).GetAwaiter().GetResult()) return true;
        if (p.Dns.Count > 0 && _liveness.DnsUp(p.Dns[0], cts.Token).GetAwaiter().GetResult()) return true;
        return false;
    }

    /// <summary>Marks the window "reverting" on disk (attempt counted), re-applies its snapshot as the service itself (tier Admin), removes the window.
    /// A service that dies in between finds "reverting" at start and reverts once more (re-applying a snapshot is idempotent). An attempt that throws
    /// — anything: the adapter not enumerated yet at boot, a journal write, a tool that would not start — leaves the window in "reverting" for the next
    /// tick (2 s) or start; after <see cref="MaxRevertAttempts"/> it is given up and logged, never re-armed (R6). A revert whose steps ran but failed
    /// is also final. Never throws.</summary>
    void Revert(List<Pending> list, Pending p, string reason)
    {
        var attempt = Math.Max(p.RevertAttempts, _attempts.GetValueOrDefault(p.Id)) + 1;
        _attempts[p.Id] = attempt;
        list[list.IndexOf(p)] = p with { State = Reverting, RevertReason = reason, RevertAttempts = attempt };
        var caller = $"commit-confirm ({reason})";
        try { SavePending(list); }   // the marker matters less than the revert: on a disk that will not take it, revert anyway
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"commit-confirm: could not mark the window on {p.Adapter} as reverting ({ex.Message}); reverting anyway"); }
        try
        {
            log($"commit-confirm {reason}: reverting '{p.Title}' on {p.Adapter} (armed by {p.Caller} at {p.ArmedAt:HH:mm:ss}, deadline {p.Deadline:HH:mm:ss}{(attempt > 1 ? $", attempt {attempt} of {MaxRevertAttempts}" : "")})");
            svc.Reload();
            var report = RevertTo(p.Before, caller, Tier.Admin, _policy.Resolve());
            log($"commit-confirm {reason}: '{p.Title}' on {p.Adapter} reverted {(report.Success ? "ok" : "with FAILED steps; the window is closed, nothing re-armed")}");
        }
        catch (Exception ex)
        {
            if (attempt < MaxRevertAttempts)
            {
                log($"commit-confirm {reason}: revert of '{p.Title}' on {p.Adapter} FAILED ({ex.Message}); attempt {attempt} of {MaxRevertAttempts}, retried on the next tick");
                return;   // stays "reverting" on disk
            }
            log($"commit-confirm {reason}: revert of '{p.Title}' on {p.Adapter} FAILED ({ex.Message}); given up after {attempt} attempts, the window is closed, nothing re-armed; the pre-apply snapshot is kept for RevertLastApply");
            // Each attempt went through Journaled, which snapshots the state before the revert (the bad change) into last-apply.json; put the
            // window's own snapshot back so that a RevertLastApply by hand returns to the state before the confirmed apply, not to the bad change.
            try { File.WriteAllText(PreApplyPath, JsonSerializer.Serialize(p.Before, ServiceProtocol.Json)); }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException) { log($"commit-confirm: could not restore last-apply.json ({io.Message})"); }
        }
        list.RemoveAll(x => SameNic(x, p.Id)); _clocks.Remove(p.Id); _attempts.Remove(p.Id);
        try { SavePending(list); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"commit-confirm: could not update confirm-pending.json after the revert ({ex.Message}); it is retried on the next tick"); }
    }

    /// <summary>Start-up (review decisions 3 + 4): a window in "reverting" is reverted once more; an armed window past its deadline is reverted now
    /// (timeout); every other window is re-armed with its remaining time and a fresh settle, because probes right after a boot are not evidence.</summary>
    void Resume()
    {
        try
        {
            var list = LoadPending();
            if (list.Count == 0) return;
            var now = Clock();
            foreach (var p in list.ToList())
            {
                if (p.State == Reverting) Revert(list, p, p.RevertReason ?? "interrupted");
                else if (now >= p.Deadline) Revert(list, p, "timeout");
                else
                {
                    var leftMs = (long)(p.Deadline - now).TotalMilliseconds;
                    _clocks[p.Id] = new Clocks(Monotonic() + leftMs, Monotonic() + settleMs);   // from here on the in-process clock decides
                    list[list.IndexOf(p)] = p with { ArmedAt = now, FailedTicks = 0 };
                    log($"re-armed commit-confirm: {leftMs / 1000} s left on {p.Adapter} for {p.Caller} (service restarted inside the window; probes count after the settle)");
                }
            }
            SavePending(list);
        }
        catch (Exception ex) { log("commit-confirm resume failed, the next tick retries: " + ex.Message); }   // start-up must not take the service down
    }
}
