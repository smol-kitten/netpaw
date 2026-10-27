using System.Diagnostics;
using System.ServiceProcess;
using NetPaw.Ipc;
using NetPaw.ServiceHost;

// netpaw-svc: the only NetPaw component that changes network state (docs/PLAN-v0.14.md). Runs as
// LocalSystem under the service control manager; `netpaw-svc --console` runs it in the foreground for
// debugging on a test machine (as an admin, logging to the console).
if (args.Contains("--console"))
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await new PipeServer(Console.WriteLine, Startup.Intents(Console.WriteLine)).Run(stop.Token);
    return 0;
}
ServiceBase.Run(new NetPawWindowsService());
return 0;

sealed class NetPawWindowsService : ServiceBase
{
    const string Source = "NetPaw";
    readonly CancellationTokenSource _stop = new();
    Task? _run;

    public NetPawWindowsService() { ServiceName = "NetPaw"; CanStop = true; CanShutdown = true; }

    protected override void OnStart(string[] args)
    {
        if (!EventLog.SourceExists(Source)) EventLog.CreateEventSource(Source, "Application");
        SetRecoveryActions();
        Action<string> log = msg => EventLog.WriteEntry(Source, msg, EventLogEntryType.Information);
        log("service token: " + TokenInfo.Describe());   // the token in use, not the SCM config: privileges + service SID
        IntentHandler? intents = null;
        try { intents = Startup.Intents(log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Without its store the service still answers Status (so the tray can say what is wrong) but changes nothing.
            EventLog.WriteEntry(Source, "service store unavailable, change requests disabled: " + ex.Message, EventLogEntryType.Error);
        }
        var server = new PipeServer(log, intents);
        _run = Task.Run(async () =>
        {
            try { await server.Run(_stop.Token); }
            catch (Exception ex)
            {
                // A dead listener must not leave a "running" service behind: stop with exit code 1; failureflag 1
                // (SetRecoveryActions) makes the SCM treat that as a failure and restart it.
                EventLog.WriteEntry(Source, "pipe server failed: " + ex, EventLogEntryType.Error);
                ExitCode = 1;
                Stop();
            }
        });
    }

    /// <summary>Restart twice after a crash or a failed stop (5 s), then stay stopped; the tray falls back to user mode.
    /// Set here, not in the MSI: Windows Installer's MsiServiceConfigFailureActions fails with access denied
    /// (error 1939) for restart actions. Idempotent; a failure is logged and does not stop the service.</summary>
    /// <summary>The privileges the service keeps (sc privs). SeChangeNotify = bypass traverse checking, which every
    /// process needs to open paths. The pipe client is read at Identification level (no SeImpersonate needed) and no
    /// Global\ object is created (no SeCreateGlobal) — VM pass 5, 2026-10-16, measured a working apply with this set.</summary>
    public const string Privileges = "SeChangeNotifyPrivilege";

    static void SetRecoveryActions()
    {
        // failureflag 1: also act when the service stops itself with a non-zero exit code (a dead listener
        // below), not only on a crash; without it the SCM treats Stop() with ExitCode=1 as a clean stop.
        // G2 hardening (audit t-db6795), idempotent (every start re-applies the same SCM config; the writes succeed
        // because the token is not restricted), takes effect at the NEXT start. sidtype unrestricted only adds
        // NT SERVICE\NetPaw to the token. NOT "restricted": VM-measured 2026-10-16 (pass 3) a write-restricted token is
        // refused by Windows for the one thing this service exists for — `netsh interface ipv4 set address ...
        // source=static` -> "Zugriff verweigert" — while its own file, Event Log and pipe writes succeeded once their
        // DACLs named the service SID. The token actually in use is logged at start ("service token: ...").
        foreach (var args in new[] { "failure NetPaw reset= 86400 actions= restart/5000/restart/5000//0", "failureflag NetPaw 1",
                                     "privs NetPaw " + Privileges, "sidtype NetPaw unrestricted" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10_000) || p.ExitCode != 0) EventLog.WriteEntry(Source, $"could not set the service recovery ({args}): {output.Result.Trim()}", EventLogEntryType.Warning);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EventLog.WriteEntry(Source, $"could not set the service recovery ({args}): {ex.Message}", EventLogEntryType.Warning);
            }
        }
    }

    protected override void OnStop() { _stop.Cancel(); try { _run?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { } }
    protected override void OnShutdown() => OnStop();
}

static class Startup
{
    /// <summary>The running build's three-part version, what a CatWinget update must be newer than.</summary>
    static Version CatWingetSourceVersion()
    {
        var v = typeof(Startup).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    /// <summary>The change-request handler over the machine's service store; reports an apply that a crash interrupted.</summary>
    public static IntentHandler Intents(Action<string> log)
    {
        // G1: the service never searches PATH and never runs a binary from outside System32.
        NetPaw.Planning.SystemTool.Strict = true;
        // Environment.SystemDirectory redirects to SysWOW64 in a 32-bit process; the service is x64-only.
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("the NetPaw service must run as a 64-bit process");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPaw");
        var dir = ServiceFolder.Ensure(root, log);
        // PR 4 tiers: read HKLM policy, cache the last-known-good in the (SYSTEM/Admins-owned) service folder, audit to the log.
        var policy = new NetPaw.Ipc.PolicyTiers(new RegistryPolicySource(), Path.Combine(dir, "policy.cache.json"), log);
        var current = CatWingetSourceVersion();
        var installer = new NetPaw.Updates.UpdateInstaller(new WindowsUpdateEnvironment(dir, log), current, log);
        var handler = new IntentHandler(NetPaw.NetPawService.CreateDefault(dir), dir, log, policy: policy, installer: installer);
        handler.Interrupted();
        return handler;
    }
}
