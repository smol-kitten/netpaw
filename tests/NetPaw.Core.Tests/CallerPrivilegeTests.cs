using NetPaw.Ipc;
using Xunit;

namespace NetPaw.Tests;

/// <summary>The privileged-caller decision (docs/PLAN-v0.14.md). The token reading is Windows-only and is
/// exercised on the pawsktop VM; this covers the rule: a group counts only when enabled, not deny-only, and
/// one of the privileged SIDs.</summary>
public class CallerPrivilegeTests
{
    const string Admins = CallerPrivilege.AdministratorsSid;        // S-1-5-32-544
    const string Nco = CallerPrivilege.NetworkConfigurationOperatorsSid;  // S-1-5-32-556
    const string Users = "S-1-5-32-545";
    const uint Enabled = CallerPrivilege.SE_GROUP_ENABLED;
    const uint DenyOnly = CallerPrivilege.SE_GROUP_USE_FOR_DENY_ONLY;

    [Fact]
    public void Enabled_admin_is_privileged()
        => Assert.True(CallerPrivilege.IsPrivileged([(Users, Enabled), (Admins, Enabled)]));

    [Fact]
    public void Enabled_network_configuration_operator_is_privileged()
        => Assert.True(CallerPrivilege.IsPrivileged([(Nco, Enabled)]));

    [Fact]
    public void Deny_only_admin_is_not_privileged()   // a UAC-filtered admin token
        => Assert.False(CallerPrivilege.IsPrivileged([(Users, Enabled), (Admins, DenyOnly)]));

    [Fact]
    public void Admin_present_but_not_enabled_is_not_privileged()
        => Assert.False(CallerPrivilege.IsPrivileged([(Admins, 0u)]));

    [Fact]
    public void Admin_both_enabled_and_deny_only_flag_is_not_privileged()   // belt and braces
        => Assert.False(CallerPrivilege.IsPrivileged([(Admins, Enabled | DenyOnly)]));

    [Fact]
    public void Standard_user_is_not_privileged()
        => Assert.False(CallerPrivilege.IsPrivileged([(Users, Enabled)]));

    [Fact]
    public void No_groups_is_not_privileged()
        => Assert.False(CallerPrivilege.IsPrivileged([]));
}
