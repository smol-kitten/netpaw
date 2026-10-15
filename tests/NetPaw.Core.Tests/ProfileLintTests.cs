using NetPaw.Tests;
using NetPaw.Model;
using Xunit;

namespace NetPaw.Core.Tests;

public class ProfileLintTests
{
    static Profile P(string name, string? adapter = null, params string[] addrs) =>
        new() { Id = name, Name = name, Adapter = adapter, Addresses = addrs.Select(IpAddr.Parse).ToList() };

    [Fact]
    public void Address_live_on_another_adapter_warns()
    {
        var w = ProfileLint.Warnings(P("lab", "Ethernet", "192.0.2.10/24"), [], [Fx.Nic("Ethernet 2", addrs: ["192.0.2.10/24"])]);
        Assert.Single(w, x => x.Contains("already live on 'Ethernet 2'"));
    }

    [Fact]
    public void Address_live_on_the_profile_s_own_adapter_is_not_a_warning()
    {
        Assert.Empty(ProfileLint.Warnings(P("lab", "Ethernet", "192.0.2.10/24"), [], [Fx.Nic("Ethernet", addrs: ["192.0.2.10/24"])]));
    }

    [Fact]
    public void Address_in_another_profile_warns_but_the_profile_itself_does_not()
    {
        var me = P("lab", null, "192.0.2.10/24");
        Assert.Empty(ProfileLint.Warnings(me, [me], []));
        Assert.Single(ProfileLint.Warnings(me, [P("lab-2", null, "192.0.2.10/24")], []), x => x.Contains("also in profile 'lab-2'"));
    }

    [Fact]
    public void Gateway_equal_to_own_address_warns_and_dhcp_profiles_never_warn()
    {
        var p = P("lab", null, "192.0.2.10/24"); p.Gateway = "192.0.2.10";
        Assert.Single(ProfileLint.Warnings(p, [], []), x => x.Contains("own addresses"));
        var d = new Profile { Id = "d", Name = "d", Dhcp = true, Addresses = [IpAddr.Parse("192.0.2.10/24")] };
        Assert.Empty(ProfileLint.Warnings(d, [P("x", null, "192.0.2.10/24")], [Fx.Nic("Ethernet 2", addrs: ["192.0.2.10/24"])]));
    }
}
