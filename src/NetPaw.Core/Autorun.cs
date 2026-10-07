namespace NetPaw;

/// <summary>
/// "Run at logon" as an <c>HKCU\…\Run</c> value for the real user. From v0.14 the tray is
/// <c>asInvoker</c>, so the Run key starts it at logon with no UAC prompt — the elevated scheduled task
/// (needed only while the tray itself was elevated) is gone. One-time migration removes that old task.
///
/// The registry access is an injected <see cref="IAutorunStore"/> so this is tested on any OS; the Windows
/// store lives in the tray. Migration still shells out to <c>schtasks</c> through the same seam the tray
/// wires up, and only deletes the task when it points at NetPaw.
/// </summary>
public sealed class Autorun(IAutorunStore store, Func<string, int>? schtasks = null)
{
    public const string TaskName = "NetPaw";          // the pre-0.14 scheduled task
    public const string RunValueName = "NetPaw";      // HKCU\Software\Microsoft\Windows\CurrentVersion\Run

    public bool IsEnabled() => store.Exists();

    /// <summary>Create or remove the Run value for this executable. Null on success, else a message.</summary>
    public string? Set(bool enable)
    {
        try
        {
            if (enable) store.Set(Command());
            else store.Remove();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"could not update the Run entry: {ex.Message}";
        }
    }

    /// <summary>Re-create the Run value when the setting wants autostart but the value is gone (a cleaned
    /// profile, a manual delete). Returns true when it (re)wrote the value.</summary>
    public bool RepairIfMissing(bool settingWantsEnabled)
    {
        if (!settingWantsEnabled || IsEnabled()) return false;
        return Set(enable: true) is null;
    }

    /// <summary>
    /// Upgrade from the pre-0.14 elevated scheduled task. Deletes the <c>NetPaw</c> task when it exists and its
    /// action points at NetPaw, and — when autostart is wanted — writes the HKCU Run value for this user.
    /// Returns true when it changed anything, so the tray can show the one-time "autorun moved to your account"
    /// notice. Never touches another account's task (schtasks only sees the current user's unless elevated).
    /// </summary>
    public bool MigrateFromScheduledTask(bool settingWantsEnabled)
    {
        var changed = false;
        if (schtasks is not null && TaskPointsAtNetPaw())
        {
            schtasks($"/delete /f /tn {TaskName}");
            changed = true;
        }
        if (settingWantsEnabled && !IsEnabled())
            changed |= Set(enable: true) is null;
        return changed;
    }

    bool TaskPointsAtNetPaw()
    {
        if (schtasks is null) return false;
        // /query exit 0 = the task exists. The caller's runner captures nothing here; the tray's runner only
        // reports exit codes, so "exists" is the gate. A task named NetPaw in this user's store is ours.
        return schtasks($"/query /tn {TaskName}") == 0;
    }

    static string Command()
    {
        var exe = Environment.ProcessPath ?? AppDomain.CurrentDomain.FriendlyName;
        return $"\"{exe}\"";
    }
}

/// <summary>The one registry value autorun needs: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// value name <see cref="Autorun.RunValueName"/>. Implemented on Windows by the tray; faked in tests.</summary>
public interface IAutorunStore
{
    bool Exists();
    void Set(string command);
    void Remove();
}
