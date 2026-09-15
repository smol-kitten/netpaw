#if TELEMETRY
using System.Reflection;
using NetPaw.Telemetry;

namespace NetPaw.Tray;

/// <summary>
/// Only compiled into the telemetry build (-p:Telemetry=true). Wraps the client with NetPaw's
/// privacy rules: events carry categories and counts, never addresses, names or profile contents.
/// </summary>
static class TelemetryHost
{
    public static TelemetryClient? Client { get; private set; }
    public static bool IsTelemetryBuild => true;
    public static string Token => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TelemetryToken")?.Value ?? "";
    public static string Endpoint => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TelemetryEndpoint")?.Value is { Length: > 0 } e ? e : TelemetryClient.DefaultEndpoint;

    public static void Init(NetPawService svc)
    {
        if (Token.Length == 0) { svc.Store.Log("telemetry build without a token — nothing will be sent"); return; }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        Client = new TelemetryClient(Endpoint, Token, version, svc.InstallId()) { Log = svc.Store.Log, Enabled = Active(svc) };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) { Client.Error(ex.GetType().FullName ?? "Exception", ex.Message, "critical", ex: ex); Client.FlushAsync().GetAwaiter().GetResult(); } };
        _ = Client.Heartbeat("healthy", new() { ["adapters"] = svc.GetAdapters().Count(a => a.IsPhysical), ["profiles"] = svc.UserProfiles.Count(), ["managed"] = svc.ManagedProfiles.Count() });
    }

    public static bool Active(NetPawService svc) => svc.Settings.TelemetryEnabled && svc.Allowed(Capability.Telemetry);
    public static void Refresh(NetPawService svc) { if (Client is not null) Client.Enabled = Active(svc); }

    /// <summary>Outcome of an apply: what kind, how many steps, which step failed (description only — no command output).</summary>
    public static void Apply(string kind, Planning.ApplyPlan plan, Planning.ApplyOutcome? outcome)
    {
        if (Client is null) return;
        var failed = outcome?.Failures.Select(f => f.Step.Description.Split(' ')[0]).ToList() ?? [];
        _ = Client.Event("apply", kind, outcome is null ? "crashed" : outcome.Success ? "ok" : "failed", plan.Steps.Count,
            new() { ["steps"] = plan.Steps.Count, ["warnings"] = plan.Warnings.Count, ["failed_steps"] = failed.Count == 0 ? null : string.Join(",", failed) });
        if (outcome is { Success: false }) Client.Error("ApplyFailed", $"{kind}: {string.Join(", ", failed)} failed", "error", new() { ["kind"] = kind });
    }

    public static void Event(string category, string action, string? label = null, double? value = null) => _ = Client?.Event(category, action, label, value);
    public static void Error(Exception ex, string where) => Client?.Error(ex.GetType().FullName ?? "Exception", ex.Message, "error", new() { ["where"] = where }, ex);
}
#else
namespace NetPaw.Tray;

/// <summary>Default build: every call compiles to nothing.</summary>
static class TelemetryHost
{
    public static bool IsTelemetryBuild => false;
    public static void Init(NetPawService svc) { }
    public static void Refresh(NetPawService svc) { }
    public static void Apply(string kind, Planning.ApplyPlan plan, Planning.ApplyOutcome? outcome) { }
    public static void Event(string category, string action, string? label = null, double? value = null) { }
    public static void Error(Exception ex, string where) { }
}
#endif
