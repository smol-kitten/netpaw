using NetPaw.Ipc;

namespace NetPaw.Discovery;

/// <summary>One entry point for "which switch port am I on?": pktmon needs SYSTEM/elevation, the tray and the CLI run
/// as the user (0.14 report: "pktmon start failed" / "access denied"). Elevated → capture here; otherwise ask the service
/// (RequestKind.Listen, its own tier gate + AllowCapture); no service → say what is missing.</summary>
public static class SwitchListen
{
    public static async Task<(List<SwitchNeighbor> Neighbors, string? Error)> Run(string adapterName, string? adapterMac, int seconds, bool elevated,
        IProgress<int>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!PktmonCapture.Available) return ([], "pktmon is not available on this Windows version (needs Windows 10 2004+ / 11)");
        if (elevated) return await new PktmonCapture().Listen(adapterName, adapterMac, seconds, progress, log, ct).ConfigureAwait(false);
        if (!ServiceChange.ServiceAnswers()) return ([], "pktmon needs elevation: install the NetPaw service (MSI) so it captures for you, or run NetPaw as administrator");
        seconds = Math.Clamp(seconds, 10, 120);
        var tick = progress is null ? null : Task.Run(async () => { for (var i = 1; i <= seconds && !ct.IsCancellationRequested; i++) { await Task.Delay(1000, ct).ConfigureAwait(false); progress.Report(i); } }, ct);
        var (resp, err) = await ServiceClient.Send(ServiceChange.Listen(adapterName, seconds), timeoutMs: seconds * 1000 + 15_000, ct).ConfigureAwait(false);
        if (resp is null) return ([], "service did not answer the listen request: " + (err ?? "no response"));
        if (!resp.Ok) return ([], resp.Error ?? "service refused the listen request");
        log?.Invoke($"lldp/cdp via service: {resp.Neighbors?.Count ?? 0} neighbour(s)");
        return (resp.Neighbors ?? [], null);
    }
}
