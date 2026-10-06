namespace NetPaw;

/// <summary>
/// Detects the "installed with someone else's admin credentials" trap: a per-machine installer (or an
/// elevated tray) can end up running as an administrator account that is NOT the user sat at the screen.
/// When that happens, everything per-user (settings in %APPDATA%, the autostart task) would be written
/// for the wrong person. This is the platform-neutral decision half; the tray feeds in the two account
/// names it reads from Windows.
/// </summary>
public static class UserContextGuard
{
    /// <summary>
    /// Compares the effective (process) user with the interactive session user. Returns a human-readable
    /// warning when they differ and the interactive user is known; null when they match or cannot be told apart.
    /// </summary>
    public static string? MismatchWarning(string? effectiveUser, string? interactiveUser)
    {
        if (string.IsNullOrWhiteSpace(interactiveUser)) return null;      // no console session / can't read it — assume fine
        if (string.IsNullOrWhiteSpace(effectiveUser)) return null;
        if (string.Equals(effectiveUser.Trim(), interactiveUser.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return $"NetPaw is running as {effectiveUser}, not {interactiveUser}. " +
               "Settings and autostart would be saved for " + interactiveUser + " instead of you, " +
               "so autostart has not been registered. Close NetPaw and start it again as " + interactiveUser + ".";
    }
}
