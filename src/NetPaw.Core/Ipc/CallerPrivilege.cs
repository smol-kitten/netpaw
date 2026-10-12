namespace NetPaw.Ipc;

/// <summary>
/// Decides whether a pipe caller counts as privileged (administrator / Network Configuration Operator) from
/// the groups in its access token. Works on SID strings so it is unit-tested without Windows;
/// <c>PipeServer</c> reads the token (TOKEN_GROUPS) and passes each group's SID value + attributes. A group
/// grants privilege ONLY when it is enabled and NOT deny-only: a UAC-filtered admin token carries the
/// Administrators SID as deny-only, and such a caller must be treated as unprivileged (it uses the
/// admin-unlock helper instead, PR 5).
/// </summary>
public static class CallerPrivilege
{
    public const uint SE_GROUP_ENABLED = 0x4;
    public const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x10;

    /// <summary>Well-known SID strings that mean "may run the restricted intents".</summary>
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string NetworkConfigurationOperatorsSid = "S-1-5-32-556";
    public static readonly string[] PrivilegedSids = [AdministratorsSid, NetworkConfigurationOperatorsSid];

    public static bool IsPrivileged(IEnumerable<(string Sid, uint Attributes)> groups, IReadOnlyCollection<string>? privilegedSids = null)
    {
        var want = privilegedSids ?? PrivilegedSids;
        return groups.Any(g => (g.Attributes & SE_GROUP_ENABLED) != 0
                               && (g.Attributes & SE_GROUP_USE_FOR_DENY_ONLY) == 0
                               && want.Contains(g.Sid));
    }
}
