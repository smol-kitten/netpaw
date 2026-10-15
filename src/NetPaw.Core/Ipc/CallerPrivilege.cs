namespace NetPaw.Ipc;

/// <summary>
/// Decides whether a pipe caller counts as privileged (administrator / Network Configuration Operator) from
/// the groups in its access token. Works on SID strings so it is unit-tested without Windows;
/// <c>PipeServer</c> reads the token (TOKEN_GROUPS) and passes each group's SID value + attributes. A group
/// grants privilege ONLY when it is enabled and NOT deny-only: a UAC-filtered admin token carries the
/// Administrators SID as deny-only, and such a caller must be treated as unprivileged (it uses the
/// admin-unlock helper instead, PR 5).
/// </summary>
/// <summary>What a caller may do, lowest to highest. PR 4 policy tiers gate each intent on a minimum of these.</summary>
public enum Tier { StandardUser, NetworkConfigOperators, Admin }

public static class CallerPrivilege
{
    public const uint SE_GROUP_ENABLED = 0x4;
    public const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x10;

    /// <summary>Well-known SID strings that mean "may run the restricted intents".</summary>
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string NetworkConfigurationOperatorsSid = "S-1-5-32-556";
    public static readonly string[] PrivilegedSids = [AdministratorsSid, NetworkConfigurationOperatorsSid];
    /// <summary>NETWORK (S-1-5-2): present in the token of a network logon, which is what an SMB open of the pipe
    /// (remote or loopback) is supposed to produce. Logged per request so the pipe's NETWORK deny can be measured.</summary>
    public const string NetworkSid = "S-1-5-2";

    /// <summary>True when the token carries the NETWORK SID at all (enabled or deny-only: a deny ACE matches both).</summary>
    public static bool IsNetworkLogon(IEnumerable<(string Sid, uint Attributes)> groups) => groups.Any(g => g.Sid == NetworkSid);

    public static bool IsPrivileged(IEnumerable<(string Sid, uint Attributes)> groups, IReadOnlyCollection<string>? privilegedSids = null)
    {
        var want = privilegedSids ?? PrivilegedSids;
        return groups.Any(g => (g.Attributes & SE_GROUP_ENABLED) != 0
                               && (g.Attributes & SE_GROUP_USE_FOR_DENY_ONLY) == 0
                               && want.Contains(g.Sid));
    }

    /// <summary>The caller's tier: Admin if the Administrators SID is enabled and not deny-only, else NCO if the
    /// Network Configuration Operators SID is, else StandardUser. Same enabled-and-not-deny-only rule as
    /// <see cref="IsPrivileged"/>, so a UAC-filtered admin token reads as StandardUser.</summary>
    public static Tier TierOf(IEnumerable<(string Sid, uint Attributes)> groups)
    {
        bool Has(string sid) => groups.Any(g => (g.Attributes & SE_GROUP_ENABLED) != 0
                                                && (g.Attributes & SE_GROUP_USE_FOR_DENY_ONLY) == 0
                                                && g.Sid == sid);
        return Has(AdministratorsSid) ? Tier.Admin
             : Has(NetworkConfigurationOperatorsSid) ? Tier.NetworkConfigOperators
             : Tier.StandardUser;
    }
}
