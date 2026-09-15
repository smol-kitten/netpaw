using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPaw.Model;

namespace NetPaw.Adapters;

/// <summary>Adapter enumeration through System.Net.NetworkInformation (works on every OS, no WMI needed).</summary>
public sealed class NetworkInterfaceAdapterProvider : IAdapterProvider
{
    static readonly string[] VirtualHints = ["virtual", "hyper-v", "vmware", "vethernet", "loopback", "tap-", "tunnel", "wintun", "wireguard", "bluetooth", "npcap", "wan miniport", "isatap", "teredo"];

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
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
            list.Add(new AdapterInfo(nic.Name, desc, nic.Id, index, nic.OperationalStatus == OperationalStatus.Up, isPhysical, dhcp, addrs, gws, dns,
                string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")))));
        }
        return list.OrderByDescending(a => a.IsPhysical).ThenByDescending(a => a.Up).ThenBy(a => a.Name).ToList();
    }
}
