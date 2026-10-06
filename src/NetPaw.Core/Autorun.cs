namespace NetPaw;

/// <summary>
/// "Run at logon" through a highest-privilege scheduled task — the only way to autostart an
/// elevated app without a UAC prompt each login. The Windows-x64 tray wires this to `schtasks`;
/// Linux is used only for unit tests (the runner returns a sentinel "external/unknown" code).
///
/// Per-user by construction: registration must only happen from a process running as the
/// interactive user (see <see cref="UserContextGuard"/>), otherwise the onlogon task would be
/// created for the wrong account and never fire for the person at the screen.
/// </summary>
public sealed class Autorun : IDisposable
{
    /// <summary>Exit code meaning "the platform cannot tell" (e.g. non-Windows test host).</summary>
    public const int UnknownCode = -1;
    public const string TaskName = "NetPaw";

    readonly Func<string, int> _run;
    bool _disposed;

    public Autorun(Func<string, int> run) => _run = run;

    public bool IsEnabled() => Run($"/query /tn {TaskName}") == 0;

    /// <summary>Creates or deletes the onlogon task. Returns null on success, else an error message.</summary>
    public string? Set(bool enable)
    {
        var exe = Environment.ProcessPath ?? AppDomain.CurrentDomain.FriendlyName;
        var code = enable
            ? Run($"/create /f /tn {TaskName} /sc onlogon /rl highest /tr \"\\\"{exe}\\\"\"")   // no /ru: the current user, which must be the interactive one
            : IsEnabled() ? Run($"/delete /f /tn {TaskName}") : 0;
        return code == UnknownCode ? null : code == 0 ? null : $"schtasks failed (exit {code})";
    }

    /// <summary>
    /// Repair path: when the setting says autostart is on but the task has gone missing (an
    /// overwritten account, a re-signout, a failed install), re-create it. Only ever runs from
    /// a process already in the interactive user's context, so it cannot re-create it for the
    /// wrong account. Returns true when it (re)registered the task.
    /// </summary>
    public bool RepairIfMissing(bool settingWantsEnabled)
    {
        if (!settingWantsEnabled || IsEnabled()) return false;
        return Set(enable: true) is null;   // re-created → true; failure reported separately
    }

    int Run(string args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return _run(args); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return -1; }
    }

    public void Dispose() => _disposed = true;
}
