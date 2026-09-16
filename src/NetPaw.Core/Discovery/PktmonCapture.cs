using System.Collections.Concurrent;
using System.Diagnostics;
using NetPaw.Planning;

namespace NetPaw.Discovery;

/// <summary>
/// Listens for LLDP/CDP with Windows' built-in `pktmon` (10 2004+ / 11 / Server 2019+): filter on the LLDP
/// ethertype and the two multicast MACs, capture to ETL for N seconds, convert to pcapng, decode. Passive —
/// nothing is sent. Needs elevation. All process work is asynchronous with real timeouts; one capture at a
/// time per machine (pktmon state is global). The converter verb differs per Windows build (`pcapng` on
/// Windows 10, `etl2pcap` on Windows 11), so it is discovered from `pktmon help` once, not guessed.
/// </summary>
public sealed class PktmonCapture
{
    public static bool Available => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "pktmon.exe"));

    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static readonly ConcurrentDictionary<string, int?> ComponentCache = new(StringComparer.OrdinalIgnoreCase);
    static string? _convertVerb;
    static bool? _lldpAgentCmdlet;
    static readonly HashSet<string> AgentEnabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs pktmon (or another tool) without a window; stdout+stderr read concurrently; the timeout really kills the child.</summary>
    static async Task<(int Code, string Out)> RunAsync(string file, string args, int timeoutMs = 20_000, CancellationToken ct = default)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            var so = p.StandardOutput.ReadToEndAsync(cts.Token); var se = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, (await so.ConfigureAwait(false)) + (await se.ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }

    static Task<(int Code, string Out)> Pktmon(string args, int timeoutMs = 20_000, CancellationToken ct = default) => RunAsync("pktmon", args, timeoutMs, ct);

    /// <summary>The ETL→pcapng verb this build offers, from `pktmon help` (cached per process).</summary>
    public static async Task<string?> ConvertVerb(CancellationToken ct = default)
    {
        if (_convertVerb is not null) return _convertVerb;
        var (_, help) = await Pktmon("help", ct: ct).ConfigureAwait(false);
        foreach (var verb in new[] { "etl2pcap", "pcapng", "etl2pcapng" })
            if (System.Text.RegularExpressions.Regex.IsMatch(help, @"(?m)^\s*" + verb + @"\b")) return _convertVerb = verb;
        return null;
    }

    /// <summary>pktmon component id of an adapter, matched by MAC (the list shows driver descriptions and MACs, never the alias).</summary>
    public static async Task<int?> ComponentId(string mac, CancellationToken ct = default)
    {
        if (ComponentCache.TryGetValue(mac, out var cached)) return cached;
        var (code, o) = await Pktmon("list", ct: ct).ConfigureAwait(false);
        if (code != 0) return null;
        var wanted = mac.Replace(':', '-').ToUpperInvariant();
        foreach (var line in o.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || !char.IsDigit(t[0]) || !t.ToUpperInvariant().Contains(wanted)) continue;
            if (int.TryParse(new string(t.TakeWhile(char.IsDigit).ToArray()), out var n)) { ComponentCache[mac] = n; return n; }
        }
        ComponentCache[mac] = null;
        return null;
    }

    /// <summary>Some drivers pass the 01-80-C2-00-00-0E group to the stack only after an LLDP agent registered it; Windows' own agent (Hyper-V module) does that. Once per adapter per process.</summary>
    static async Task EnableLldpAgent(string adapterName, CancellationToken ct)
    {
        if (_lldpAgentCmdlet == false || AgentEnabled.Contains(adapterName)) return;
        var step = Step.PowerShell("LLDP agent", $"if (Get-Command Enable-NetLldpAgent -ErrorAction SilentlyContinue) {{ Enable-NetLldpAgent -NetAdapterName '{adapterName.Replace("'", "''")}' -ErrorAction SilentlyContinue; 'yes' }} else {{ 'no' }}");
        var (_, o) = await RunAsync(step.FileName, step.Arguments, 15_000, ct).ConfigureAwait(false);
        _lldpAgentCmdlet = o.Contains("yes");
        AgentEnabled.Add(adapterName);
    }

    /// <summary>Capture for <paramref name="seconds"/> and return decoded neighbours. Errors come back as the string; an empty list with null error = nothing announced.</summary>
    public async Task<(List<SwitchNeighbor> Neighbors, string? Error)> Listen(string? adapterName, string? adapterMac, int seconds, IProgress<int>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!Available) return ([], "pktmon is not available on this Windows version (needs Windows 10 2004+ / 11)");
        if (!await OneAtATime.WaitAsync(0, ct).ConfigureAwait(false)) return ([], "a capture is already running (link-up discovery or another Listen) — wait for it to finish");
        var dir = Path.Combine(Path.GetTempPath(), "netpaw"); Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, "lldp-" + Path.GetRandomFileName().Replace(".", ""));
        var etl = stem + ".etl"; var pcap = stem + ".pcapng";
        try
        {
            var verb = await ConvertVerb(ct).ConfigureAwait(false);
            if (verb is null) return ([], "this pktmon has no ETL→pcapng converter (pktmon help lists neither etl2pcap nor pcapng)");
            if (adapterName is not null) await EnableLldpAgent(adapterName, ct).ConfigureAwait(false);
            var comp = adapterMac is null ? null : await ComponentId(adapterMac, ct).ConfigureAwait(false);
            await Pktmon("stop", ct: ct).ConfigureAwait(false); await Pktmon("filter remove", ct: ct).ConfigureAwait(false);
            // LLDP by ethertype (catches unicast-addressed LLDP too); CDP is LLC/SNAP, so by its multicast MAC.
            await Pktmon("filter add NetPawLLDP -d 0x88CC", ct: ct).ConfigureAwait(false);
            await Pktmon("filter add NetPawLLDPm -m 01-80-C2-00-00-0E", ct: ct).ConfigureAwait(false);
            await Pktmon("filter add NetPawCDP -m 01-00-0C-CC-CC-CC", ct: ct).ConfigureAwait(false);
            var (code, o) = await Pktmon($"start --capture --pkt-size 0{(comp is { } c ? $" --comp {c}" : "")} -f \"{etl}\"", ct: ct).ConfigureAwait(false);
            log?.Invoke($"pktmon start -> {code}{(comp is { } cc ? $" (component {cc})" : " (all NICs)")}: {o.Trim().Split('\n').FirstOrDefault()}");
            if (code != 0) return ([], "pktmon start failed: " + o.Trim());
            try { for (var i = 0; i < seconds; i++) { await Task.Delay(1000, ct).ConfigureAwait(false); progress?.Report(i + 1); } }
            catch (OperationCanceledException) { /* stop and decode what we have */ }
            await Pktmon("stop").ConfigureAwait(false); await Pktmon("filter remove").ConfigureAwait(false);
            var (c2, o2) = await Pktmon($"{verb} \"{etl}\" -o \"{pcap}\"", 60_000).ConfigureAwait(false);
            if (c2 != 0 || !File.Exists(pcap)) return ([], $"pktmon {verb} failed: " + o2.Trim().Split('\n')[0]);
            var bytes = await File.ReadAllBytesAsync(pcap, CancellationToken.None).ConfigureAwait(false);
            var n = PcapNg.Neighbors(bytes);
            log?.Invoke($"lldp/cdp capture: {bytes.Length} bytes, {n.Count} neighbour(s)");
            return (n, null);
        }
        finally
        {
            foreach (var f in new[] { etl, pcap }) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } }
            OneAtATime.Release();
        }
    }
}
