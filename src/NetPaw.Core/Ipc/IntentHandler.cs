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
public sealed class IntentHandler(NetPawService svc, string journalDirectory, Action<string> log, int settleMs = 2500, PolicyTiers? policy = null)
{
    readonly Lock _gate = new();
    // PR 4: the one authorization point. When no policy is injected, an empty-but-valid source means the default
    // table applies — i.e. exactly today's behaviour. The cache lives in the (SYSTEM/Admins-owned) service folder.
    readonly PolicyTiers _policy = policy ?? new PolicyTiers(new EmptyPolicySource(), Path.Combine(journalDirectory, "policy.cache.json"), log);
    public string JournalPath { get; } = Path.Combine(journalDirectory, "inflight.json");
    /// <summary>G13: the adapter state captured just before the last journaled apply, what <see cref="RequestKind.RevertLastApply"/> restores.</summary>
    public string PreApplyPath { get; } = Path.Combine(journalDirectory, "last-apply.json");

    public sealed record Journal(string Title, string Adapter, string Caller, DateTimeOffset At, IReadOnlyList<string> Commands);

    /// <summary>A change that was running when the service stopped. The file is kept (renamed) for the admin.</summary>
    public Journal? Interrupted()
    {
        if (!File.Exists(JournalPath)) return null;
        Journal? j = null;
        try { j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath), ServiceProtocol.Json); } catch (JsonException) { }
        var keep = Path.Combine(journalDirectory, $"interrupted-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.Move(JournalPath, keep, overwrite: true);
        log($"interrupted apply found at start: {j?.Title ?? "unreadable journal"} on {j?.Adapter} by {j?.Caller}; kept as {Path.GetFileName(keep)}");
        return j;
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
        _ => throw new Rejected($"{k} is not a change request"),
    };

    /// <summary>Test-only shim (internal): a bool "privileged" maps to Network Configuration Operators, absent it
    /// to StandardUser. It deliberately cannot express Admin, so production uses the Tier overload; the pipe server
    /// always passes the real caller tier.</summary>
    internal ServiceResponse Handle(ServiceRequest r, string caller, bool privileged) =>
        Handle(r, caller, privileged ? Tier.NetworkConfigOperators : Tier.StandardUser);

    public ServiceResponse Handle(ServiceRequest r, string caller, Tier callerTier)
    {
        lock (_gate)
        {
            try
            {
                var overrides = _policy.Resolve();
                if (!_policy.Decide(IntentFor(r.Kind), callerTier, overrides, caller).Allowed)
                    throw new Rejected($"{r.Kind} needs tier {PolicyTiers.Required(IntentFor(r.Kind), overrides)} or higher; you are {callerTier} (the audit line is in the Application event log, source NetPaw: 'tier decision')");
                svc.Reload();
                var report = r.Kind switch
                {
                    RequestKind.ApplyProfile => ApplyProfile(r, caller, callerTier, overrides),
                    RequestKind.Dhcp => Run(ApplyPlanner.PlanDhcp(Adapter(r)), caller, require: Capability.Dhcp),
                    RequestKind.Reach => Reach(r, caller),
                    RequestKind.ClearTemp => ClearTemp(),
                    RequestKind.DeleteRoute => DeleteRoute(r, caller),
                    RequestKind.ResetAdapter => Run(ApplyPlanner.PlanResetAdapter(Adapter(r)), caller),
                    RequestKind.Renew => Run(Connectivity.Advisor.PlanRenew(Adapter(r)), caller, require: Capability.Dhcp),
                    RequestKind.Release => Run(Connectivity.Advisor.PlanRelease(Adapter(r)), caller),
                    RequestKind.Prefer => Run(Connectivity.Advisor.PlanPrefer(Adapter(r), svc.GetAdapters()), caller),
                    RequestKind.SetMtu => SetMtu(r, caller),
                    RequestKind.RevertLastApply => RevertLastApply(caller),
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

    AdapterInfo Adapter(ServiceRequest r)
    {
        if (r.Adapter is { Length: > 256 }) throw new Rejected("adapter name too long");
        var a = svc.ResolveAdapter(r.Adapter);
        // A resolved name is still untrusted (a user can name a VPN phonebook entry anything). Every repair
        // plan below puts the name straight into a command, so refuse an unsafe one centrally.
        if (!ProfileGuard.IsSafeAdapterName(a.Name)) throw new Rejected($"adapter name '{a.Name}' contains characters that are not allowed");
        return a;
    }

    ApplyReport ApplyProfile(ServiceRequest r, string caller, Tier callerTier, IReadOnlyDictionary<PolicyIntent, Tier> overrides)
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
        var adapter = r.Adapter is null && p.Adapter is not null ? svc.ResolveAdapter(p.Adapter) : Adapter(r);
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
        return Journaled(plan, caller, () => svc.ApplyAndVerify(plan, settleMs));
    }

    ApplyReport Reach(ServiceRequest r, string caller)
    {
        if (!ProfileGuard.IsIPv4OrPrefix(r.Target)) throw new Rejected("reach target must be an IPv4 address or a.b.c.d/n");
        var adapter = Adapter(r);
        var d = svc.ResolveReach(r.Target!, adapter);
        var (plan, _) = svc.Reach(d, adapter, dryRun: true, r.ReplacePrimary);
        return Journaled(plan, caller, () => svc.Reach(d, adapter, dryRun: false, r.ReplacePrimary).Outcome);
    }

    ApplyReport ClearTemp()
    {
        var outcome = svc.ClearTemp();
        var plan = new ApplyPlan { Title = "clear temporary addresses", Adapter = "" };
        if (outcome is null) plan.Warnings.Add("No temporary addresses to remove.");
        return ApplyReport.From(plan, outcome);
    }

    ApplyReport DeleteRoute(ServiceRequest r, string caller)
    {
        if (r.RoutePrefix is null || !r.RoutePrefix.Contains('/') || !ProfileGuard.IsIPv4OrPrefix(r.RoutePrefix)) throw new Rejected("route prefix must be a.b.c.d/n");
        if (r.RouteGateway is not null && !Net.IpMath.IsIPv4(r.RouteGateway)) throw new Rejected("route gateway must be an IPv4 address");
        var adapter = Adapter(r);
        return Run(ApplyPlanner.PlanDeleteRoute(new RouteEntry(r.RoutePrefix, adapter.Index, r.RouteGateway, 0), adapter.Name), caller);
    }

    ApplyReport SetMtu(ServiceRequest r, string caller)
    {
        if (r.Mtu is not (>= 576 and <= 9000)) throw new Rejected("MTU must be 576..9000");
        return Run(Connectivity.Advisor.PlanSetMtu(Adapter(r), r.Mtu!.Value), caller);
    }

    ApplyReport Run(ApplyPlan plan, string caller, Capability? require = null)
    {
        if (require is { } c) svc.Require(c);
        return Journaled(plan, caller, () => svc.Apply(plan));
    }

    // ---- G13: pre-apply snapshot + revert ------------------------------------------------------------------------

    /// <summary>What the adapter looked like right before an apply: enough to put it back (DHCP, or addresses + gateway + DNS).
    /// VLAN and routes are not captured (routes are additive and VLAN is driver-specific).</summary>
    public sealed record PreApply(string Adapter, string Mac, string Title, string Caller, DateTimeOffset At, Model.Profile Before);

    void SavePreState(ApplyPlan plan, string caller)
    {
        if (string.IsNullOrEmpty(plan.Adapter)) return;   // clear-temp and the like: nothing adapter-shaped to put back
        try
        {
            var a = svc.GetAdapters().FirstOrDefault(x => string.Equals(x.Name, plan.Adapter, StringComparison.OrdinalIgnoreCase));
            if (a is null) return;
            var before = new Model.Profile { Name = "pre-apply", Adapter = a.Name, AdapterMac = a.Mac, Dhcp = a.Dhcp };
            if (!a.Dhcp) { before.Addresses = [.. a.Addresses]; before.Gateway = a.Gateways.FirstOrDefault(); before.Dns = [.. a.Dns]; }
            File.WriteAllText(PreApplyPath, JsonSerializer.Serialize(new PreApply(a.Name, a.Mac, plan.Title, caller, DateTimeOffset.Now, before), ServiceProtocol.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("pre-apply snapshot not written: " + ex.Message); }
    }

    /// <summary>The recorded pre-apply state, or null when there is none (or it is unreadable).</summary>
    public PreApply? LastApply()
    {
        try { return File.Exists(PreApplyPath) ? JsonSerializer.Deserialize<PreApply>(File.ReadAllText(PreApplyPath), ServiceProtocol.Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>One line for Status / the tray menu: "DHCP on Ethernet 2 at 00:58 by PC\anna (session 2)".</summary>
    public string? LastApplySummary() => LastApply() is { } la ? $"{la.Title} on {la.Adapter} at {la.At:HH:mm} by {la.Caller}" : null;

    ApplyReport RevertLastApply(string caller)
    {
        var pre = LastApply() ?? throw new Rejected("nothing to revert: no apply has been recorded");
        var adapter = Adapter(new ServiceRequest(ServiceProtocol.Version, RequestKind.RevertLastApply) { Adapter = pre.Adapter });
        if (!string.Equals(adapter.Mac, pre.Mac, StringComparison.OrdinalIgnoreCase))
            throw new Rejected($"'{pre.Adapter}' is not the adapter the last apply changed any more (MAC differs); nothing reverted");
        log($"revert to the pre-apply state for {caller}: undoing '{pre.Title}' on {pre.Adapter} ({pre.At:HH:mm}, by {pre.Caller})");
        if (pre.Before.Dhcp)
        {
            svc.Require(Capability.Dhcp);
            return Journaled(ApplyPlanner.PlanDhcp(adapter), caller, () => svc.Apply(ApplyPlanner.PlanDhcp(adapter)));
        }
        var plan = svc.PlanProfile(pre.Before, adapter);
        return Journaled(plan, caller, () => svc.ApplyAndVerify(plan, settleMs));
    }

    ApplyReport Journaled(ApplyPlan plan, string caller, Func<ApplyOutcome?> apply)
    {
        if (plan.IsEmpty) return ApplyReport.From(plan, null);
        Directory.CreateDirectory(journalDirectory);
        SavePreState(plan, caller);   // G13: written before the first command runs, so a revert is possible even after a half-done apply
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(new Journal(plan.Title, plan.Adapter, caller, DateTimeOffset.Now,
            plan.Steps.Select(s => s.CommandLine).ToList()), ServiceProtocol.Json));
        ApplyOutcome? outcome;
        try { outcome = apply(); }
        finally { File.Delete(JournalPath); }
        var report = ApplyReport.From(plan, outcome);
        log($"{plan.Title} on {plan.Adapter} for {caller}: {(report.Success ? "ok" : "FAILED")}{(report.VerifyDiffs.Count > 0 ? " (verify: " + string.Join("; ", report.VerifyDiffs) + ")" : "")}");
        return report;
    }
}
