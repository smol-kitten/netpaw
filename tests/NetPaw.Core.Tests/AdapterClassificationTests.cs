using NetPaw.Adapters;
using NetPaw.Model;
using Xunit;

namespace NetPaw.Core.Tests;

/// <summary>t-dd3d27: only the host's vEthernet on an EXTERNAL Hyper-V switch is host-facing.</summary>
public class AdapterClassificationTests
{
    static AdapterInfo Nic(string name, bool physical, string mac, string[]? addrs = null, string? gw = null, bool up = true) =>
        new(name, physical ? "Intel I219" : "Hyper-V Virtual Ethernet Adapter", "{" + name + "}", 1, up, physical, false,
            (addrs ?? []).Select(IpAddr.Parse).ToList(), gw is null ? [] : [gw], [], mac) { HyperVVirtual = !physical };

    [Fact]
    public void Default_switch_and_wsl_are_not_host_facing_even_untagged()
    {
        Assert.False(Nic("vEthernet (Default Switch)", false, "00:15:5D:00:01:02", ["172.28.0.1/20"]).HostFacing);
        Assert.False(Nic("vEthernet (WSL (Hyper-V firewall))", false, "00:15:5D:00:01:03", ["172.29.0.1/20"]).HostFacing);
        Assert.False(Nic("vEthernet (nat)", false, "00:15:5D:00:01:04", ["192.168.200.1/24"]).HostFacing);
        // a name-internal switch stays internal even if someone put a gateway on it
        Assert.False(Nic("vEthernet (Default Switch)", false, "00:15:5D:00:01:02", ["172.28.0.1/20"], "172.28.0.254").HostFacing);
    }

    [Fact]
    public void External_vethernet_with_a_gateway_is_host_facing_untagged()
    {
        Assert.True(Nic("vEthernet (Lab)", false, "00:15:5D:00:02:01", ["192.0.2.42/24"], "192.0.2.1").HostFacing);
    }

    [Fact]
    public void External_vethernet_without_gateway_is_host_facing_when_it_shares_the_uplink_mac()
    {
        // A lab segment with no gateway: Hyper-V gave the management vNIC the physical NIC's MAC.
        var all = AdapterSelector.TagVSwitchUplinks(
        [
            Nic("Ethernet", true, "3C:7C:3F:11:22:33"),                                   // bound to the external switch: no address
            Nic("vEthernet (Lab)", false, "3C:7C:3F:11:22:33", ["10.9.0.5/24"]),
            Nic("vEthernet (Default Switch)", false, "00:15:5D:00:01:02", ["172.28.0.1/20"]),
        ]);
        Assert.True(all[1].ExternalVSwitch); Assert.True(all[1].HostFacing);
        Assert.False(all[2].ExternalVSwitch); Assert.False(all[2].HostFacing);
        Assert.True(all[0].VSwitchUplink);                                              // the uplink is tagged from the MAC signal alone
        Assert.Equal("vEthernet (Lab)", AdapterSelector.Pick(all, null)!.Name);
    }

    [Fact]
    public void A_short_test_mac_never_counts_as_shared()
    {
        var all = AdapterSelector.TagVSwitchUplinks([Nic("Ethernet", true, "AA", ["169.254.1.1/16"]), Nic("vEthernet (Something)", false, "AA", ["172.30.0.1/20"])]);
        Assert.False(all[1].ExternalVSwitch); Assert.False(all[1].HostFacing); Assert.False(all[0].VSwitchUplink);
    }

    [Fact]
    public void Internal_switch_is_never_picked_or_a_secondary()
    {
        var all = AdapterSelector.TagVSwitchUplinks(
        [
            Nic("Ethernet", true, "3C:7C:3F:11:22:33", ["192.168.1.10/24"], "192.168.1.1"),
            Nic("vEthernet (WSL)", false, "00:15:5D:00:01:03", ["172.29.0.1/20"]),
        ]);
        Assert.Equal("Ethernet", AdapterSelector.Pick(all, null)!.Name);
        Assert.Empty(AdapterSelector.Secondaries(all, all[0]));
    }
}
