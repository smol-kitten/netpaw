using System.Diagnostics;
using System.ServiceProcess;
using NetPaw.ServiceHost;

// netpaw-svc: the only NetPaw component that changes network state (docs/PLAN-v0.14.md). Runs as
// LocalSystem under the service control manager; `netpaw-svc --console` runs it in the foreground for
// debugging on a test machine (as an admin, logging to the console).
if (args.Contains("--console"))
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await new PipeServer(Console.WriteLine).Run(stop.Token);
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
        var server = new PipeServer(msg => EventLog.WriteEntry(Source, msg, EventLogEntryType.Information));
        _run = Task.Run(async () =>
        {
            try { await server.Run(_stop.Token); }
            catch (Exception ex)
            {
                // A dead listener must not leave a "running" service behind: stop, so the SCM restarts it.
                EventLog.WriteEntry(Source, "pipe server failed: " + ex, EventLogEntryType.Error);
                ExitCode = 1;
                Stop();
            }
        });
    }

    protected override void OnStop() { _stop.Cancel(); try { _run?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { } }
    protected override void OnShutdown() => OnStop();
}
