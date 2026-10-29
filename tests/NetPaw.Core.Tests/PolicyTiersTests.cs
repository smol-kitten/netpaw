using NetPaw.Ipc;
using Xunit;

namespace NetPaw.Tests;

/// <summary>PR 4 policy tiers: default table == today, overrides tighten/loosen within a floor, last-known-good on
/// an unreadable policy, and a tier computed from the caller's token groups.</summary>
public class PolicyTiersTests
{
    sealed class FakeSource(IReadOnlyDictionary<PolicyIntent, Tier> overrides, bool valid) : ITierPolicySource
    {
        public IReadOnlyDictionary<PolicyIntent, Tier> Overrides = overrides;
        public bool Valid = valid;
        public string? Reason = "test: invalid";
        public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read() => (Overrides, Valid, Valid ? null : Reason);
    }

    static PolicyTiers Make(ITierPolicySource src, out List<string> audit)
    {
        var a = new List<string>();
        audit = a;
        return new PolicyTiers(src, Path.Combine(Directory.CreateTempSubdirectory().FullName, "policy.cache.json"), a.Add);
    }

    static (string, uint)[] Groups(params string[] enabledSids) =>
        [.. enabledSids.Select(s => (s, CallerPrivilege.SE_GROUP_ENABLED))];

    // ---- caller tier ----
    [Fact]
    public void TierOf_reads_the_highest_enabled_group()
    {
        Assert.Equal(Tier.Admin, CallerPrivilege.TierOf(Groups(CallerPrivilege.AdministratorsSid)));
        Assert.Equal(Tier.NetworkConfigOperators, CallerPrivilege.TierOf(Groups(CallerPrivilege.NetworkConfigurationOperatorsSid)));
        Assert.Equal(Tier.StandardUser, CallerPrivilege.TierOf(Groups("S-1-5-21-1-2-3-1001")));
        // a UAC-filtered admin carries the Administrators SID deny-only -> StandardUser
        Assert.Equal(Tier.StandardUser, CallerPrivilege.TierOf([(CallerPrivilege.AdministratorsSid, CallerPrivilege.SE_GROUP_USE_FOR_DENY_ONLY)]));
    }

    // ---- golden: default table equals pre-PR4 behaviour ----
    static readonly PolicyIntent[] WasPrivilegedOnly =
        [PolicyIntent.ClearTemp, PolicyIntent.ResetAdapter, PolicyIntent.Release, PolicyIntent.Prefer, PolicyIntent.SetMtu];   // DeleteRoute moved to Admin (operator 2026-10-29)

    [Fact]
    public void Default_table_matches_today()
    {
        var t = Make(new FakeSource(new Dictionary<PolicyIntent, Tier>(), valid: true), out _);
        var overrides = t.Resolve();
        foreach (PolicyIntent intent in Enum.GetValues<PolicyIntent>())
        {
            var required = PolicyTiers.Required(intent, overrides);
            // today: the old PrivilegedOnly set needed NCO/Admin; VirtualUnlock was managed-or-privileged (NCO); all else StandardUser.
            // InstallUpdate (0.17, CatWinget) is the one Admin-by-default intent: the service installs software.
            var expected = intent is PolicyIntent.InstallUpdate or PolicyIntent.DeleteRoute ? Tier.Admin
                : WasPrivilegedOnly.Contains(intent) || intent == PolicyIntent.VirtualUnlock ? Tier.NetworkConfigOperators : Tier.StandardUser;
            Assert.Equal(expected, required);
            // a StandardUser is allowed exactly the StandardUser intents; an NCO is allowed everything below Admin.
            Assert.Equal(expected == Tier.StandardUser, t.Decide(intent, Tier.StandardUser, overrides).Allowed);
            Assert.Equal(expected != Tier.Admin, t.Decide(intent, Tier.NetworkConfigOperators, overrides).Allowed);
        }
    }

    // ---- overrides tighten and loosen ----
    [Fact]
    public void An_override_tightens_or_loosens_within_the_floor()
    {
        var o = new Dictionary<PolicyIntent, Tier> { [PolicyIntent.ResetAdapter] = Tier.Admin, [PolicyIntent.ClearTemp] = Tier.StandardUser };
        var t = Make(new FakeSource(o, valid: true), out _);
        var ov = t.Resolve();
        Assert.False(t.Decide(PolicyIntent.ResetAdapter, Tier.NetworkConfigOperators, ov).Allowed);  // tightened to Admin
        Assert.True(t.Decide(PolicyIntent.ResetAdapter, Tier.Admin, ov).Allowed);
        Assert.True(t.Decide(PolicyIntent.ClearTemp, Tier.StandardUser, ov).Allowed);               // loosened to StandardUser
    }

    [Fact]
    public void Manual_route_delete_is_admin_by_default_and_never_below_NCO_while_profiles_stay_standard()
    {
        var t = Make(new FakeSource(new Dictionary<PolicyIntent, Tier> { [PolicyIntent.DeleteRoute] = Tier.StandardUser }, valid: true), out _);
        var ov = t.Resolve();
        Assert.Equal(Tier.NetworkConfigOperators, PolicyTiers.Required(PolicyIntent.DeleteRoute, ov));   // a policy may open it to NCO, not further
        Assert.Equal(Tier.Admin, PolicyTiers.DefaultFor(PolicyIntent.DeleteRoute));
        Assert.Equal(Tier.StandardUser, PolicyTiers.DefaultFor(PolicyIntent.ApplyProfile));             // routes inside a profile ride on ApplyProfile
    }

    [Fact]
    public void An_override_cannot_loosen_a_floor_intent_below_its_floor()
    {
        var o = new Dictionary<PolicyIntent, Tier> { [PolicyIntent.VirtualUnlock] = Tier.StandardUser };   // try to loosen below NCO
        var t = Make(new FakeSource(o, valid: true), out _);
        var ov = t.Resolve();
        Assert.Equal(Tier.NetworkConfigOperators, PolicyTiers.Required(PolicyIntent.VirtualUnlock, ov));    // clamped up to the floor
        Assert.False(t.Decide(PolicyIntent.VirtualUnlock, Tier.StandardUser, ov).Allowed);
        Assert.True(t.Decide(PolicyIntent.VirtualUnlock, Tier.NetworkConfigOperators, ov).Allowed);
    }

    // ---- last-known-good ----
    [Fact]
    public void A_tightened_policy_then_unreadable_stays_tight()
    {
        var src = new FakeSource(new Dictionary<PolicyIntent, Tier> { [PolicyIntent.Dhcp] = Tier.Admin }, valid: true);
        var t = Make(src, out var audit);
        t.Resolve();                                  // valid -> cached
        src.Valid = false; src.Overrides = new Dictionary<PolicyIntent, Tier>();
        var ov = t.Resolve();                         // unreadable -> last-known-good
        Assert.Equal(Tier.Admin, PolicyTiers.Required(PolicyIntent.Dhcp, ov));
        Assert.Contains(audit, l => l.Contains("last-known-good"));
    }

    [Fact]
    public void First_run_with_an_unreadable_policy_uses_defaults()
    {
        var t = Make(new FakeSource(new Dictionary<PolicyIntent, Tier>(), valid: false), out var audit);
        var ov = t.Resolve();                         // no cache -> defaults (empty overrides)
        Assert.Equal(Tier.Admin, PolicyTiers.Required(PolicyIntent.DeleteRoute, ov));
        Assert.Equal(Tier.StandardUser, PolicyTiers.Required(PolicyIntent.Dhcp, ov));
        Assert.Contains(audit, l => l.Contains("no cache"));
    }

    [Theory]
    [InlineData("Admin", true, Tier.Admin)]
    [InlineData("admin", true, Tier.Admin)]
    [InlineData("NetworkConfigOperators", true, Tier.NetworkConfigOperators)]
    [InlineData("StandardUser", true, Tier.StandardUser)]
    [InlineData("5", false, default(Tier))]                       // numeric -> unreachable tier -> rejected
    [InlineData("StandardUser,Admin", false, default(Tier))]      // flags-style comma -> rejected
    [InlineData("garbage", false, default(Tier))]
    [InlineData("", false, default(Tier))]
    public void TryParseTier_accepts_only_defined_names(string input, bool ok, Tier expected)
    {
        Assert.Equal(ok, PolicyTiers.TryParseTier(input, out var t));
        if (ok) Assert.Equal(expected, t);
    }

    [Fact]
    public void An_invalid_policy_reason_is_logged_once_per_change()
    {
        var src = new FakeSource(new Dictionary<PolicyIntent, Tier>(), valid: false) { Reason = "PolicySchemaVersion 2 unsupported, this build: 1" };
        var t = Make(src, out var audit);
        t.Resolve(); t.Resolve();
        Assert.Single(audit, l => l.StartsWith("policy invalid: PolicySchemaVersion 2"));
        src.Reason = "Tier.Dhcp = '5' is not a tier name"; t.Resolve();
        Assert.Single(audit, l => l.StartsWith("policy invalid: Tier.Dhcp"));
    }

    [Fact]
    public void Every_decision_writes_one_audit_line()
    {
        var t = Make(new FakeSource(new Dictionary<PolicyIntent, Tier>(), valid: true), out var audit);
        var ov = t.Resolve();
        audit.Clear();
        t.Decide(PolicyIntent.DeleteRoute, Tier.StandardUser, ov);
        Assert.Single(audit);
        Assert.Contains("intent=DeleteRoute", audit[0]);
        Assert.Contains("DENY", audit[0]);
    }
}

public class PolicySchemaVersionTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("2", false)]
    [InlineData("0", false)]
    [InlineData("x", false)]
    public void Only_the_known_schema_is_supported(string? value, bool ok) => Assert.Equal(ok, PolicyTiers.SchemaSupported(value));
}
