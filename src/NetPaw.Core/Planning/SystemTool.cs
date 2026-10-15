namespace NetPaw.Planning;

/// <summary>
/// Resolves the Windows tools NetPaw launches (netsh, ipconfig, arp, pktmon, powershell, sc, route, schtasks) to
/// their full <c>System32</c> paths, so a process never resolves a bare name through PATH (audit G1,
/// t-414e12). Plans keep the short name for display, logs and tests; the path is applied at launch time only.
///
/// <see cref="Strict"/> is set once at service start. In strict mode an unknown bare name, or a rooted path
/// outside the system directory, is refused instead of handed to CreateProcess: a LocalSystem process must
/// never search PATH or run a binary from elsewhere. The tray and the CLI (user context) leave it off, so their
/// diagnostics keep the pass-through.
/// </summary>
public static class SystemTool
{
    public static bool Strict { get; set; }

    public static string Resolve(string fileName) => Resolve(fileName, OperatingSystem.IsWindows(), Environment.SystemDirectory, Strict);

    internal static string Resolve(string fileName, bool isWindows, string systemDirectory, bool strict = false)
    {
        if (!isWindows || string.IsNullOrEmpty(fileName)) return fileName;
        if (IsRooted(fileName))
        {
            if (strict && !fileName.StartsWith(systemDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"refusing to run '{fileName}': the service only runs tools from {systemDirectory}");
            return fileName;
        }
        var name = (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName).ToLowerInvariant();
        var mapped = name switch
        {
            "powershell" => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "netsh" or "ipconfig" or "arp" or "pktmon" or "sc" or "route" or "schtasks" => Path.Combine(systemDirectory, name + ".exe"),
            _ => null,
        };
        if (mapped is not null) return mapped;
        if (strict) throw new InvalidOperationException($"refusing to run '{fileName}': not a known system tool (the service never searches PATH)");
        return fileName;
    }

    // Rooted on the host OR a Windows drive/UNC root: the service runs on Windows, but the rule must read the
    // same way on the Linux test host, where Path.IsPathRooted("C:\...") is false.
    static bool IsRooted(string p) =>
        Path.IsPathRooted(p)
        || (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/'))
        || p.StartsWith(@"\\", StringComparison.Ordinal);
}
