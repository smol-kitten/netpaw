using System.Diagnostics;

namespace NetPaw.Discovery;

/// <summary>
/// Listens for LLDP/CDP with Windows' built-in `pktmon` (10 2004+ / 11 / Server 2019+): filter on the two
/// multicast MACs, capture to ETL for N seconds, convert with `pktmon etl2pcapng`, decode. Passive —
/// nothing is sent. Needs elevation (the tray runs elevated; the CLI says so).
/// </summary>
public sealed class PktmonCapture
{
    public static bool Available => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "pktmon.exe"));

    static (int Code, string Out) Run(string args, int timeoutMs = 20_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pktmon", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, o);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }

    /// <summary>pktmon component id of an adapter (by name substring of `pktmon list`), or null = all NICs.</summary>
    public static int? ComponentId(string adapterName)
    {
        var (code, o) = Run("list");
        if (code != 0) return null;
        foreach (var line in o.Split('\n'))
        {
            // "  12  Intel(R) I219-LM              ..." — id is the first integer on a line that names the adapter
            var t = line.Trim();
            if (t.Length == 0 || !char.IsDigit(t[0]) || !t.Contains(adapterName, StringComparison.OrdinalIgnoreCase)) continue;
            var id = new string(t.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(id, out var n)) return n;
        }
        return null;
    }

    /// <summary>Capture for <paramref name="seconds"/> and return decoded neighbours. Errors come back as the string; an empty list with null error = nothing announced.</summary>
    public async Task<(List<SwitchNeighbor> Neighbors, string? Error)> Listen(string? adapterName, int seconds, IProgress<int>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!Available) return ([], "pktmon is not available on this Windows version (needs Windows 10 2004+ / 11)");
        var dir = Path.Combine(Path.GetTempPath(), "netpaw"); Directory.CreateDirectory(dir);
        var etl = Path.Combine(dir, "lldp.etl"); var pcap = Path.Combine(dir, "lldp.pcapng");
        foreach (var f in new[] { etl, pcap }) if (File.Exists(f)) File.Delete(f);
        Run("stop"); Run("filter remove");
        var comp = adapterName is null ? null : ComponentId(adapterName);
        try
        {
            Run("filter add NetPawLLDP -m 01-80-C2-00-00-0E"); Run("filter add NetPawCDP -m 01-00-0C-CC-CC-CC");
            var (code, o) = Run($"start --capture --pkt-size 0{(comp is { } c ? $" --comp {c}" : "")} -f \"{etl}\"");
            log?.Invoke($"pktmon start -> {code}: {o.Trim().Split('\n').FirstOrDefault()}");
            if (code != 0) return ([], "pktmon start failed: " + o.Trim());
            for (var i = 0; i < seconds; i++) { ct.ThrowIfCancellationRequested(); await Task.Delay(1000, ct); progress?.Report(i + 1); }
        }
        catch (OperationCanceledException) { /* fall through to stop + decode what we have */ }
        finally { Run("stop"); Run("filter remove"); }
        var (c2, o2) = Run($"etl2pcapng \"{etl}\" -o \"{pcap}\"", 60_000);
        if (c2 != 0 || !File.Exists(pcap)) return ([], "pktmon etl2pcapng failed: " + o2.Trim());
        var bytes = await File.ReadAllBytesAsync(pcap, CancellationToken.None);
        var n = PcapNg.Neighbors(bytes);
        log?.Invoke($"lldp/cdp capture: {bytes.Length} bytes, {n.Count} neighbour(s)");
        return (n, null);
    }
}
