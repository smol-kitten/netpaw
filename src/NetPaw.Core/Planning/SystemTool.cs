namespace NetPaw.Planning;

/// <summary>
/// Resolves the Windows tools NetPaw launches (netsh, ipconfig, arp, pktmon, powershell, sc, route, schtasks) to
/// their full <c>System32</c> paths, so a process that runs as LocalSystem never resolves a bare name through
/// PATH (audit G1, t-414e12). Plans keep the short name for display, logs and tests; the path is applied at
/// launch time only. Unknown names and rooted paths pass through unchanged.
/// </summary>
public static class SystemTool
{
    public static string Resolve(string fileName) => Resolve(fileName, OperatingSystem.IsWindows(), Environment.SystemDirectory);

    internal static string Resolve(string fileName, bool isWindows, string systemDirectory)
    {
        if (!isWindows || string.IsNullOrEmpty(fileName) || Path.IsPathRooted(fileName)) return fileName;
        var name = (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName).ToLowerInvariant();
        return name switch
        {
            "powershell" => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "netsh" or "ipconfig" or "arp" or "pktmon" or "sc" or "route" or "schtasks" => Path.Combine(systemDirectory, name + ".exe"),
            _ => fileName,
        };
    }
}
