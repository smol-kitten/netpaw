using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPaw.Model;

namespace NetPaw.Adapters;

/// <summary>Adapter enumeration through System.Net.NetworkInformation (works on every OS, no WMI needed).</summary>
public sealed class NetworkInterfaceAdapterProvider : IAdapterProvider
{
    static readonly System.Text.RegularExpressions.Regex FilterSuffix = new(@"-\d{4}$");
    static readonly string[] VirtualHints = ["virtual", "hyper-v", "vmware", "vethernet", "loopback", "tap-", "tunnel", "wintun", "wireguard", "bluetooth", "npcap", "wan miniport", "isatap", "teredo"];

    static long SafeSpeed(NetworkInterface nic) { try { return nic.Speed; } catch (PlatformNotSupportedException) { return 0; } catch (NetworkInformationException) { return 0; } }

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // NDIS filter drivers (QoS Packet Scheduler, WFP LightWeight Filter, ...) show up as "<nic>-<filter>-0000" pseudo-interfaces.
            if (FilterSuffix.IsMatch(nic.Name)) continue;
            var props = nic.GetIPProperties();
            int index = -1; bool dhcp = false;
            try { var v4 = props.GetIPv4Properties(); index = v4.Index; dhcp = v4.IsDhcpEnabled; }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            var addrs = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => new IpAddr(u.Address.ToString(), u.PrefixLength))
                .ToList();
            var gws = props.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            var desc = nic.Description ?? "";
            var isPhysical = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
                             && !VirtualHints.Any(h => desc.Contains(h, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(h, StringComparison.OrdinalIgnoreCase));
            var hyperV = desc.Contains("Hyper-V Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase) || nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<string> dhcpServers = [];
            try { dhcpServers = props.DhcpServerAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(); } catch (PlatformNotSupportedException) { }
            list.Add(new AdapterInfo(nic.Name, desc, nic.Id, index, nic.OperationalStatus == OperationalStatus.Up, isPhysical, dhcp, addrs, gws, dns,
                string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))), SafeSpeed(nic)) { HyperVVirtual = hyperV, DhcpServers = dhcpServers });
        }
        return AdapterSelector.TagVSwitchUplinks(list.OrderByDescending(a => a.IsPhysical).ThenByDescending(a => a.HyperVVirtual).ThenByDescending(a => a.Up).ThenBy(a => a.Name).ToList());
    }
}
