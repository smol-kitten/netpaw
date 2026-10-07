using System.Net;
using NetPaw.Model;
using NetPaw.Repos;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

/// <summary>
/// Regression tests for the elevated-install user-context fixes (t-d31414):
///  a) the wrong-account guard never writes per-user state for a different user;
///  d) autostart stays per-user and self-repairs when dropped;
///  e) community items are de-duplicated by stable id.
/// </summary>
public class ElevatedUserContextTests
{
    // ---- (a) UserContextGuard ----------------------------------------------------------------

    [Fact]
    public void GuardAcceptsMatchingUsers()
    {
        Assert.Null(UserContextGuard.MismatchWarning("CORP\\alice", "corp\\alice")); // case-insensitive
        Assert.Null(UserContextGuard.MismatchWarning("alice", "alice"));
    }

    [Fact]
    public void GuardIsSilentWhenInteractiveUserIsUnknown()
    {
        Assert.Null(UserContextGuard.MismatchWarning("ADMIN\\bob", null));
        Assert.Null(UserContextGuard.MismatchWarning("ADMIN\\bob", "   "));
    }

    [Fact]
    public void GuardFlagsARealAccountMismatch()
    {
        var warn = UserContextGuard.MismatchWarning("ADMIN\\root", "Users\\alice");
        Assert.NotNull(warn);
        Assert.Contains("ADMIN\\root", warn);
        Assert.Contains("Users\\alice", warn);
        Assert.Contains("autostart", warn);
        Assert.Contains("saved for ADMIN\\root", warn);   // names the EFFECTIVE (elevated) user, not the interactive one
    }

    [Fact]
    public void GuardTreatsBareAndMachineQualifiedNamesAsTheSameAccount()
    {
        // WindowsIdentity.Name comes back "MACHINE\alice"; WTSUserName alone (no domain) comes back
        // bare "alice". Same account, must not be flagged as a mismatch either direction.
        Assert.Null(UserContextGuard.MismatchWarning("MACHINE\\alice", "alice"));
        Assert.Null(UserContextGuard.MismatchWarning("alice", "MACHINE\\alice"));
    }

    // ---- (d) Autorun register / delete / repair ----------------------------------------------

    [Fact]
    public void AutorunCreatesAndDeletesTheTask()
    {
        var calls = new List<string>();
        using var aut = new Autorun(args => { calls.Add(args); return 0; });   // schtasks always "ok"
        Assert.Null(aut.Set(enable: true));
        Assert.Contains($"/create /f /tn {Autorun.TaskName} /sc onlogon /rl highest", calls[0]);   // per-user onlogon task, no /ru override
        Assert.Null(aut.Set(enable: false));
        Assert.Equal($"/delete /f /tn {Autorun.TaskName}", calls[^1]);
    }

    [Fact]
    public void AutorunRepairRecreatesAMissingTaskWhenAsked()
    {
        // First /query says "not found" (wrong account / dropped task), so IsEnabled() is false.
        var created = 0;
        using var aut = new Autorun(args =>
        {
            if (args.Contains("/create")) { created++; return 0; }
            return 1;                                  // /query -> 1 = task missing
        });
        Assert.False(aut.IsEnabled());
        Assert.True(aut.RepairIfMissing(settingWantsEnabled: true));
        Assert.Equal(1, created);
    }

    [Fact]
    public void AutorunDoesNotRepairWhenSettingIsOffOrTaskIsPresent()
    {
        var created = 0;
        using var off = new Autorun(_ =>
        {
            if (_.Contains("/create")) created++;
            return 1;
        });
        Assert.False(off.RepairIfMissing(settingWantsEnabled: false));   // setting off -> no repair
        Assert.Equal(0, created);

        var createdPresent = 0;
        using var present = new Autorun(args =>
        {
            if (args.Contains("/create")) createdPresent++;
            return 0;                                                   // /query returns 0 = present
        });
        Assert.False(present.RepairIfMissing(settingWantsEnabled: true));
        Assert.Equal(0, createdPresent);                                 // never attempted a create
    }

    [Fact]
    public void AutorunReportsANonUnknownNonZeroExitAsAnError()
    {
        // schtasks timing out (TrayApp's RunSchTasks kills it and reports -2, never Autorun.UnknownCode)
        // must be a reported failure, not treated as success.
        using var aut = new Autorun(_ => -2);
        Assert.Equal("schtasks failed (exit -2)", aut.Set(enable: true));
    }

    // ---- (e) community items de-duplicated by stable id --------------------------------------

    sealed class FakeFetcher2 : IPackFetcher
    {
        public string? Body;
        public Task<(HttpStatusCode, string?, string?)> Fetch(string url, string? etag, CancellationToken ct)
            => Task.FromResult((Body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, Body, (string?)"e1"));
    }

    static (NetPawService, FakeFetcher2) Make(Pack pack)
    {
        const string Url = "https://packs.example/index.json";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new JsonStore(dir);
        store.SaveSettings(new Settings { RepoUrls = [Url] });
        var f = new FakeFetcher2 { Body = PackJson.Serialize(pack) };
        var svc = new NetPawService(store, new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())), new RepoClient(Path.Combine(dir, "cache"), f));
        return (svc, f);
    }

    [Fact]
    public async Task CommunityPresetsDoNotDuplicateABundledVendorModel()
    {
        // A community pack whose preset has the same Vendor/Model key as a bundled entry: after
        // sync it must appear exactly once (the bundled one wins; no community copy appended).
        var pack = new Pack
        {
            Name = "community",
            Entries =
            [
                new PackEntry { Id = "as", Vendor = "ASUS", Model = "Router (older)", Kind = "preset", Ip = "192.168.50.1", Prefix = 24 },
                new PackEntry { Id = "lab", Vendor = "Acme", Model = "Lab", Kind = "profile", Profile = new Profile { Name = "Acme lab", Addresses = [IpAddr.Parse("10.9.0.5/16")], Gateway = "10.9.0.1" } },
            ],
        };
        var (svc, _) = Make(pack);
        Assert.Contains("ASUS/Router (older)", svc.Presets.Select(p => p.Key));          // bundled already has it
        await svc.SyncRepos();
        Assert.Equal(1, svc.Presets.Count(p => p.Key == "ASUS/Router (older)"));         // still exactly one
    }

    [Fact]
    public async Task CommunityProfilesKeepOnePerStableId()
    {
        // Two repo refs (e.g. a policy-pinned spec and the user's own copy of the same URL) that both
        // resolve to the same pack must still yield one r-<repo>-<id> profile, never two — the stable
        // id is keyed on pack name + entry id, not on which ref fetched it. Without the dedupe in
        // RebuildRepos this fails on main (both refs' entries get appended, so the profile shows twice).
        const string UrlA = "https://packs.example/index.json";
        const string UrlB = "https://packs.example/mirror.json";
        var pack = new Pack
        {
            Name = "community",
            Entries =
            [
                new PackEntry { Id = "lab", Vendor = "Acme", Model = "Lab", Kind = "profile", Profile = new Profile { Name = "Acme lab", Addresses = [IpAddr.Parse("10.9.0.5/16")], Gateway = "10.9.0.1" } },
            ],
        };
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new JsonStore(dir);
        store.SaveSettings(new Settings { RepoUrls = [UrlA, UrlB] });
        var f = new FakeFetcher2 { Body = PackJson.Serialize(pack) };   // same body for either URL
        var svc = new NetPawService(store, new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>())), new RepoClient(Path.Combine(dir, "cache"), f));
        await svc.SyncRepos();
        Assert.Equal(2, svc.RepoStates.Count(s => s.Pack is not null));   // both refs actually resolved
        Assert.Single(svc.RepoProfiles, p => p.Id == "r-community-lab");
    }
}
