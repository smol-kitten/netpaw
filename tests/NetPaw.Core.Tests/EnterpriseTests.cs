using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class MachineStoreTests
{
    static string TempDir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void LoadsManagedProfilesAndSkipsBrokenFiles()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "10-base.json"), """[{"name":"HQ LAN","addresses":[{"address":"10.1.0.20","prefixLength":16}],"gateway":"10.1.0.1"}]""");
        File.WriteAllText(Path.Combine(dir, "20-broken.json"), "{not json");
        var ms = new MachineStore(dir);
        var list = ms.Load();
        var p = Assert.Single(list);
        Assert.True(p.Managed); Assert.Equal("managed", p.Source); Assert.StartsWith("m-10-base-", p.Id);
        Assert.Single(ms.Errors);
        Assert.Empty(new MachineStore(Path.Combine(dir, "missing")).Load());
    }

    [Fact]
    public void MergeManagedWinsOnNameClash()
    {
        var managed = new Profile { Name = "Lab", Managed = true, Gateway = "10.0.0.1" };
        var user1 = new Profile { Name = "lab", Gateway = "192.168.1.1" };
        var user2 = new Profile { Name = "Home" };
        var merged = MachineStore.Merge([managed], [user1, user2]);
        Assert.Equal(["Lab", "Home"], merged.Select(p => p.Name));
        Assert.True(merged[0].Managed);
    }
}

public class PolicyTests
{
    [Fact]
    public void UnsetPolicyIsAllAllowed()
    {
        var p = PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>()));
        Assert.False(p.Any); Assert.True(p.AllowReach); Assert.Null(p.PanelHotkey); Assert.Empty(p.RepoUrls);
    }

    [Fact]
    public void ReadsValuesAndTracksWhichAreSet()
    {
        var p = PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object>
        {
            ["PanelHotkey"] = "Ctrl+Alt+P", ["AllowReach"] = 0, ["AllowDhcp"] = 1, ["ConfirmBeforeApply"] = 1,
            ["RepoUrls"] = new[] { "https://intranet/netpaw/index.json", "" },
        }));
        Assert.Equal("Ctrl+Alt+P", p.PanelHotkey); Assert.False(p.AllowReach); Assert.True(p.AllowDhcp); Assert.True(p.ConfirmBeforeApply);
        Assert.Single(p.RepoUrls);
        Assert.True(p.IsSet("PanelHotkey")); Assert.True(p.IsSet("AllowReach")); Assert.False(p.IsSet("WorkAdapter"));
        Assert.True(p.AllowUserProfiles);
    }
}

public class PolicyEnforcementTests
{
    static NetPawService Make(Dictionary<string, object> policy, string? machineDir = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new JsonStore(dir);
        store.SaveProfiles([Fx.Static("mine")]);
        store.SaveSettings(new Settings { PanelHotkey = "Ctrl+Alt+N", WorkAdapter = "Wi-Fi" });
        return new NetPawService(store, new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(machineDir ?? Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(policy)));
    }

    [Fact]
    public void PolicyOverridesSettings()
    {
        var svc = Make(new() { ["PanelHotkey"] = "Ctrl+Alt+P", ["WorkAdapter"] = "Ethernet" });
        Assert.Equal("Ctrl+Alt+P", svc.Settings.PanelHotkey);
        Assert.Equal("Ethernet", svc.Settings.WorkAdapter);
        Assert.Equal("Ethernet", svc.WorkAdapter()!.Name);
    }

    [Fact]
    public void DeniedCapabilitiesThrow()
    {
        var svc = Make(new() { ["AllowReach"] = 0, ["AllowDhcp"] = 0, ["AllowUserProfiles"] = 0 });
        Assert.Throws<DeniedByPolicyException>(() => svc.ResolveReach("10.0.0.1"));
        Assert.Throws<DeniedByPolicyException>(() => svc.ApplyDhcp());
        Assert.Throws<DeniedByPolicyException>(() => svc.SaveProfiles());
        Assert.Throws<DeniedByPolicyException>(() => svc.Capture("x", svc.WorkAdapter()!));
        Assert.Empty(svc.UserProfiles); // user file is not even loaded
        var ok = Make(new() { ["AllowTempAddresses"] = 0 });
        Assert.Throws<DeniedByPolicyException>(() => ok.Reach(ok.ResolveReach("172.20.5.1")));
        var (_, outcome) = ok.Reach(ok.ResolveReach("192.168.1.99")); // covered by the adapter? no: 192.168.1.10/24 covers it → AlreadyReachable, no temp needed
        Assert.Null(outcome);
    }

    [Fact]
    public void ManagedProfilesLoadAndSurviveSave()
    {
        var mdir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(mdir, "base.json"), """[{"name":"HQ","dhcp":true}]""");
        var svc = Make(new(), mdir);
        Assert.Equal(["HQ", "mine"], svc.Profiles.Select(p => p.Name));
        Assert.True(svc.FindProfile("HQ")!.Managed);
        svc.Profiles.Add(Fx.Static("another"));
        svc.SaveProfiles();
        svc.Reload();
        Assert.Equal(["HQ", "mine", "another"], svc.Profiles.Select(p => p.Name));
        var userFile = File.ReadAllText(svc.Store.ProfilesFile);
        Assert.DoesNotContain("HQ", userFile);
        Assert.DoesNotContain("managed", userFile); // JsonIgnore on Managed/Source
    }
}
