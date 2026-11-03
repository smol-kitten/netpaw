using NetPaw.Adapters;
using NetPaw.Model;
using Xunit;

namespace NetPaw.Core.Tests;

/// <summary>t-dd3d27: only the host's vEthernet on an EXTERNAL Hyper-V switch is host-facing.</summary>
public class AdapterClassificationTests
{
    static AdapterInfo Nic(string name, bool physical, string mac, string[]? addrs = null, string? gw = null, bool up = true) =>
        new(name, physical ? "Intel I219" : "Hyper-V Virtual Ethernet Adapter", NetPaw.Tests.Fx.Id(name), 1, up, physical, false,
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

    [Fact]
    public void Find_takes_an_index_a_guid_a_mac_or_the_alias()
    {
        var eth = NetPaw.Tests.Fx.Adapter("Ethernet") with { Index = 7, Mac = "AA:BB:CC:00:00:01" };
        var dock = NetPaw.Tests.Fx.Adapter("Ethernet 3") with { Index = 12, Mac = "AA:BB:CC:00:00:99" };
        AdapterInfo[] all = [eth, dock];
        Assert.Equal("Ethernet 3", AdapterSelector.Find("12", all)!.Name);
        Assert.Equal("Ethernet 3", AdapterSelector.Find(dock.Id, all)!.Name);
        Assert.Equal("Ethernet 3", AdapterSelector.Find(dock.Id.Trim('{', '}').ToLowerInvariant(), all)!.Name);   // GUID without braces, any case
        Assert.Equal("Ethernet 3", AdapterSelector.Find("aa:bb:cc:00:00:99", all)!.Name);
        Assert.Equal("Ethernet", AdapterSelector.Find("ethernet", all)!.Name);
        Assert.Null(AdapterSelector.Find("99", all)); Assert.Null(AdapterSelector.Find("Wi-Fi", all)); Assert.Null(AdapterSelector.Find("0", all));
    }

    [Fact]
    public void A_mac_on_two_host_facing_adapters_is_refused_not_guessed()
    {
        var a = NetPaw.Tests.Fx.Adapter("Ethernet") with { Index = 7, Mac = "AA:BB:CC:00:00:01" };
        var b = NetPaw.Tests.Fx.Adapter("Ethernet 2") with { Index = 8, Mac = "AA:BB:CC:00:00:01" };
        var ex = Assert.Throws<InvalidOperationException>(() => AdapterSelector.ByMac("AA:BB:CC:00:00:01", [a, b]));
        Assert.Contains("on 2 adapters (Ethernet, Ethernet 2)", ex.Message);
        var uplink = a with { VSwitchUplink = true };
        Assert.Equal("Ethernet 2", AdapterSelector.ByMac("AA:BB:CC:00:00:01", [uplink, b])!.Name);   // the vSwitch uplink never counts
    }

    [Fact]
    public void A_request_mac_that_matches_nothing_refuses_a_profile_mac_falls_back_to_the_alias()
    {
        AdapterInfo[] all = [NetPaw.Tests.Fx.Adapter("Ethernet") with { Mac = "AA:BB:CC:00:00:01" }];
        Assert.Equal("Ethernet", AdapterSelector.Resolve(null, "AA:BB:CC:FF:FF:FF", "Ethernet", all)!.Name);
        var ex = Assert.Throws<InvalidOperationException>(() => AdapterSelector.Resolve(null, "AA:BB:CC:FF:FF:FF", "Ethernet", all, strictMac: true));
        Assert.Contains("MAC AA:BB:CC:FF:FF:FF ('Ethernet') is not present", ex.Message);
    }
}
