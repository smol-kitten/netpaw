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
    /// Upgrade from the pre-0.14 elevated scheduled task. When the <c>NetPaw</c> task exists, delete it and
    /// confirm it is really gone before moving autostart to this user's HKCU Run value.
    ///
    /// The old task ran with highest privileges; the <c>asInvoker</c> tray cannot delete it, and
    /// <c>schtasks /delete</c> can report success spuriously, so we re-query rather than trust the exit code.
    /// If it is still registered we return <see cref="MigrationResult.TaskDeleteFailed"/> and write nothing —
    /// claiming the elevated task was removed when it was not would leave it launching NetPaw.exe elevated from a
    /// user-writable portable folder (an elevation path), and a duplicate Run value would double-start NetPaw.
    /// The tray then tells the user it needs an administrator. Never touches another account's task (schtasks
    /// only sees the current user's unless elevated).
    /// </summary>
    public MigrationResult MigrateFromScheduledTask(bool settingWantsEnabled)
    {
        var changed = false;
        if (schtasks is not null && TaskPointsAtNetPaw())
        {
            var code = schtasks($"/delete /f /tn {TaskName}");
            if (code != 0 || TaskPointsAtNetPaw()) return MigrationResult.TaskDeleteFailed;
            changed = true;
        }
        if (settingWantsEnabled && !IsEnabled())
            changed |= Set(enable: true) is null;
        return changed ? MigrationResult.Migrated : MigrationResult.NothingToDo;
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

/// <summary>What the one-time pre-0.14 autostart migration did. <see cref="TaskDeleteFailed"/> means the old
/// elevated task is still registered (deletion needs an administrator) and nothing was moved.</summary>
public enum MigrationResult { NothingToDo, Migrated, TaskDeleteFailed }

/// <summary>The one registry value autorun needs: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// value name <see cref="Autorun.RunValueName"/>. Implemented on Windows by the tray; faked in tests.</summary>
public interface IAutorunStore
{
    bool Exists();
    void Set(string command);
    void Remove();
}
