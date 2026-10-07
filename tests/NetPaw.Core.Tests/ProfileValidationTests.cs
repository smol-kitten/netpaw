using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

/// <summary>0.13.3: every profile is checked before it becomes commands, and managed profiles are trusted only
/// from files an admin deployed.</summary>
public class ProfileValidationTests
{
    static string ManagedDir(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "corp.json"), json);
        return dir;
    }

    [Theory]
    [InlineData("corp.local")]
    [InlineData("a-b.example.com")]
    public void Plain_dns_suffix_passes(string suffix)
    {
        var p = Fx.Static("x"); p.DnsSuffix = suffix;
        Assert.Empty(NetPaw.Ipc.ProfileGuard.Check(p));
    }

    [Theory]
    [InlineData("corp.local'x")]
    [InlineData("corp.local’x")]
    [InlineData("corp local")]
    public void Dns_suffix_with_quotes_or_spaces_is_refused_at_plan_time(string suffix)
    {
        var p = Fx.Static("x"); p.DnsSuffix = suffix;
        var svc = new NetPawService(new JsonStore(Directory.CreateTempSubdirectory().FullName), new FakeAdapters(Fx.Adapter()), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Directory.CreateTempSubdirectory().FullName), () => Policy.None);
        var ex = Assert.Throws<InvalidOperationException>(() => svc.PlanProfile(p));
        Assert.Contains("DNS suffix", ex.Message);
    }

    [Fact]
    public void Route_fields_must_be_ipv4()
    {
        var p = Fx.Static("x"); p.Routes = [new StaticRoute("10.0.0.0", 8, "10.0.0.1 x", null)];
        Assert.NotEmpty(NetPaw.Ipc.ProfileGuard.Check(p));
    }

    [Fact]
    public void Invalid_managed_profile_is_skipped_and_reported()
    {
        var dir = ManagedDir("""[{ "name": "Good", "addresses": [{ "address": "10.1.0.5", "prefixLength": 16 }] }, { "name": "Bad", "dnsSuffix": "a'b", "addresses": [{ "address": "10.1.0.6", "prefixLength": 16 }] }]""");
        var store = new MachineStore(dir) { OwnerCheck = null };
        var loaded = store.Load();
        Assert.Equal(["Good"], loaded.Select(p => p.Name));
        Assert.Contains(store.Errors, e => e.Contains("'Bad' skipped"));
    }

    [Fact]
    public void Auto_switch_confirmation_is_never_taken_from_a_file()
    {
        var dir = ManagedDir("""[{ "name": "Corp", "autoSwitch": true, "autoSwitchConfirmed": true, "addresses": [{ "address": "10.1.0.5", "prefixLength": 16 }] }]""");
        var p = Assert.Single(new MachineStore(dir) { OwnerCheck = null }.Load());
        Assert.True(p.AutoSwitch);
        Assert.False(p.AutoSwitchConfirmed);
    }

    [Fact]
    public void File_not_deployed_by_an_admin_is_skipped()
    {
        var dir = ManagedDir("""[{ "name": "Corp", "addresses": [{ "address": "10.1.0.5", "prefixLength": 16 }] }]""");
        var store = new MachineStore(dir) { OwnerCheck = _ => "skipped: owned by S-1-5-21-1-2-3-1001, not SYSTEM or Administrators" };
        Assert.Empty(store.Load());
        Assert.Contains(store.Errors, e => e.Contains("not SYSTEM or Administrators"));
    }
}
