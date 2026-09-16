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
    public static LogExporter? Exporter { get; private set; }
    public static bool IsTelemetryBuild => true;
    public static string Token => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TelemetryToken")?.Value ?? "";
    public static string Endpoint => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TelemetryEndpoint")?.Value is { Length: > 0 } e ? e : TelemetryClient.DefaultEndpoint;

    public static void Init(NetPawService svc)
    {
        Refresh(svc); // exporter is independent of the hub token
        if (Token.Length == 0) { svc.Store.Log("telemetry build without a token — nothing will be sent to the hub"); return; }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        Client = new TelemetryClient(Endpoint, Token, version, svc.InstallId()) { Log = svc.Store.Log, Enabled = Active(svc) };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) { Client.Error(ex.GetType().FullName ?? "Exception", ex.Message, "critical", ex: ex); Client.FlushAsync().GetAwaiter().GetResult(); } };
        _ = Client.Heartbeat("healthy", new() { ["adapters"] = svc.GetAdapters().Count(a => a.IsPhysical), ["profiles"] = svc.UserProfiles.Count(), ["managed"] = svc.ManagedProfiles.Count() });
    }

    public static bool Active(NetPawService svc) => svc.Settings.TelemetryEnabled && svc.Allowed(Capability.Telemetry);
    public static void Refresh(NetPawService svc)
    {
        if (Client is not null) Client.Enabled = Active(svc);
        Exporter?.Dispose(); Exporter = null;
        var le = svc.Settings.LogExport;
        if (le.Enabled) Exporter = new LogExporter(le, Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0", Environment.MachineName) { Log = svc.Store.Log };
    }

    /// <summary>User-configured OTLP/syslog export of a structured record (adapter name and state text allowed here; still no addresses).</summary>
    public static void Export(NetPawService svc, string severity, string body, Dictionary<string, object?> attrs, bool isIncident)
    {
        var le = svc.Settings.LogExport;
        if (Exporter is null || (isIncident ? !le.ExportIncidents : !le.ExportActions)) return;
        _ = Exporter.Send(new LogRecord(DateTimeOffset.Now, severity, body, attrs));
    }

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
    /// <summary>Error with context (state, adapter, uptime, the last 40 log lines) from <see cref="NetPaw.Diagnostics.Guard.Context"/>.</summary>
    public static void Error(Exception ex, string where, Dictionary<string, object?> context) => Client?.Error(ex.GetType().FullName ?? "Exception", ex.Message, "error", context, ex);
}
#else
namespace NetPaw.Tray;

/// <summary>Default build: every call compiles to nothing.</summary>
static class TelemetryHost
{
    public static bool IsTelemetryBuild => false;
    public static void Init(NetPawService svc) { }
    public static void Refresh(NetPawService svc) { }
    public static void Export(NetPawService svc, string severity, string body, Dictionary<string, object?> attrs, bool isIncident) { }
    public static void Apply(string kind, Planning.ApplyPlan plan, Planning.ApplyOutcome? outcome) { }
    public static void Event(string category, string action, string? label = null, double? value = null) { }
    public static void Error(Exception ex, string where) { }
    public static void Error(Exception ex, string where, Dictionary<string, object?> context) { }
}
#endif
