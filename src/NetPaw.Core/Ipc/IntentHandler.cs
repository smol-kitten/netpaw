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
public sealed class IntentHandler(NetPawService svc, string journalDirectory, Action<string> log, int settleMs = 2500)
{
    readonly Lock _gate = new();
    public string JournalPath { get; } = Path.Combine(journalDirectory, "inflight.json");

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

    /// <summary>Intents a non-privileged caller may not use until PR 4's per-action tiers exist. Resetting an
    /// adapter or deleting a route can take an uplink down, so until there is a policy tier for them the service
    /// allows them only for administrators and Network Configuration Operators. This keeps 0.14 from granting a
    /// standard user more than the elevated 0.13 tray did.</summary>
    static readonly HashSet<RequestKind> PrivilegedOnly = [RequestKind.DeleteRoute, RequestKind.ResetAdapter, RequestKind.ClearTemp, RequestKind.Release, RequestKind.Prefer, RequestKind.SetMtu];

    public ServiceResponse Handle(ServiceRequest r, string caller, bool privileged)
    {
        lock (_gate)
        {
            try
            {
                if (PrivilegedOnly.Contains(r.Kind) && !privileged)
                    throw new Rejected($"{r.Kind} needs an administrator or a Network Configuration Operator (until policy tiers arrive)");
                svc.Reload();
                var report = r.Kind switch
                {
                    RequestKind.ApplyProfile => ApplyProfile(r, caller),
                    RequestKind.Dhcp => Run(ApplyPlanner.PlanDhcp(Adapter(r)), caller, require: Capability.Dhcp),
                    RequestKind.Reach => Reach(r, caller),
                    RequestKind.ClearTemp => ClearTemp(),
                    RequestKind.DeleteRoute => DeleteRoute(r, caller),
                    RequestKind.ResetAdapter => Run(ApplyPlanner.PlanResetAdapter(Adapter(r)), caller),
                    RequestKind.Renew => Run(Connectivity.Advisor.PlanRenew(Adapter(r)), caller, require: Capability.Dhcp),
                    RequestKind.Release => Run(Connectivity.Advisor.PlanRelease(Adapter(r)), caller),
                    RequestKind.Prefer => Run(Connectivity.Advisor.PlanPrefer(Adapter(r), svc.GetAdapters()), caller),
                    RequestKind.SetMtu => SetMtu(r, caller),
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

    ApplyReport ApplyProfile(ServiceRequest r, string caller)
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

    ApplyReport Journaled(ApplyPlan plan, string caller, Func<ApplyOutcome?> apply)
    {
        if (plan.IsEmpty) return ApplyReport.From(plan, null);
        Directory.CreateDirectory(journalDirectory);
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
