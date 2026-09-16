using NetPaw.Model;

namespace NetPaw.Adapters;

public sealed record AdapterInfo(
    string Name,
    string Description,
    string Id,
    int Index,
    bool Up,
    bool IsPhysical,
    bool Dhcp,
    IReadOnlyList<IpAddr> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> Dns,
    string Mac,
    long SpeedBps = 0)
{
    /// <summary>A Hyper-V "vEthernet (…)" adapter: the host's own foot on an external virtual switch. Host-facing, so it is a valid work adapter.</summary>
    public bool HyperVVirtual { get; init; }
    /// <summary>802.11 adapter. Static-IP work happens on the cable, so a wired adapter with a gateway wins over Wi-Fi.</summary>
    public bool Wireless { get; init; }
    /// <summary>Connection-specific DNS suffix (from DHCP option 15 or the profile).</summary>
    public string? DnsSuffix { get; init; }
    /// <summary>DHCP server that handed out the lease (Windows reports it); empty for static.</summary>
    public IReadOnlyList<string> DhcpServers { get; init; } = [];
    /// <summary>A physical NIC bound to an external Hyper-V switch: it carries the switch and has no host address by design. Not a fault.</summary>
    public bool VSwitchUplink { get; init; }
    /// <summary>Adapters an admin actually configures: physical NICs and Hyper-V host vEthernet adapters.</summary>
    public bool HostFacing => IsPhysical || HyperVVirtual;
    public bool HasRealAddress => RealAddress is not null;
    /// <summary>First address that is not self-assigned (169.254.x.x).</summary>
    public IpAddr? RealAddress => Addresses.FirstOrDefault(a => !a.Address.StartsWith("169.254."));
    /// <summary>"1 Gbit/s", "100 Mbit/s" or "" when unknown.</summary>
    public string SpeedText => SpeedBps <= 0 ? "" : SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1_000_000_000.0:0.#} Gbit/s" : $"{SpeedBps / 1_000_000} Mbit/s";
    public IpAddr? Primary => Addresses.Count > 0 ? Addresses[0] : null;
    public bool HasGateway => Gateways.Count > 0;
    public bool Covers(string ip) => Addresses.Any(a => a.Contains(ip));

    public string Summary() => !Up ? "down"
        : VSwitchUplink ? "Hyper-V switch uplink (no host address here)"
        : Dhcp ? $"DHCP {Primary?.ToString() ?? "(no address)"}"
        : Addresses.Count == 0 ? "no address"
        : string.Join(", ", Addresses.Select(a => a.ToString())) + (HasGateway ? $" gw {Gateways[0]}" : "");
}

public interface IAdapterProvider
{
    IReadOnlyList<AdapterInfo> GetAdapters();
}

public static class AdapterSelector
{
    /// <summary>
    /// Picks the adapter NetPaw should act on: the configured name, else the host-facing adapter that
    /// is up and has a gateway (physical before vEthernet, wired before Wi-Fi), else one with a real address, else the first
    /// physical one that is up and is not a vSwitch uplink. On a Hyper-V host the physical NIC bound to
    /// the external switch has no address; the host's address is on "vEthernet (…)", which wins here.
    /// </summary>
    public static AdapterInfo? Pick(IReadOnlyList<AdapterInfo> adapters, string? preferredName)
    {
        if (preferredName is not null)
        {
            var named = adapters.FirstOrDefault(a => string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }
        // vEthernet adapters qualify only when they carry a gateway (external switch shared with the host);
        // internal switches (WSL, Default Switch, Docker) have NAT addresses and must never become the work adapter.
        var host = adapters.Where(a => a.IsPhysical || (a.HyperVVirtual && a.HasGateway)).OrderBy(a => a.IsPhysical ? 0 : 1).ThenBy(a => a.Wireless ? 1 : 0).ToList();
        return host.FirstOrDefault(a => a.Up && a.HasGateway && !a.VSwitchUplink)
            ?? host.FirstOrDefault(a => a.Up && a.HasRealAddress && !a.VSwitchUplink)
            ?? host.FirstOrDefault(a => a.Up && !a.VSwitchUplink)
            ?? host.FirstOrDefault(a => a.Up)
            ?? host.FirstOrDefault();
    }

    /// <summary>Adapters worth watching besides the work adapter: up, host-facing, not a vSwitch uplink, carrying a real address. Work adapter excluded.</summary>
    public static IReadOnlyList<AdapterInfo> Secondaries(IReadOnlyList<AdapterInfo> adapters, AdapterInfo? work) =>
        adapters.Where(a => a.Up && (a.IsPhysical || (a.HyperVVirtual && a.HasGateway)) && !a.VSwitchUplink && a.HasRealAddress
                            && (work is null || !string.Equals(a.Name, work.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.HasGateway ? 0 : 1).ThenBy(a => a.Name).ToList();

    /// <summary>
    /// Marks physical NICs that are bound to an external Hyper-V switch: up with no IPv4 at all while a
    /// vEthernet adapter that is up and has a gateway exists (an external switch shared with the host).
    /// Internal switches (WSL, Default Switch) do not count, so a NIC waiting for DHCP is not mis-tagged.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> TagVSwitchUplinks(IReadOnlyList<AdapterInfo> adapters)
    {
        if (!adapters.Any(a => a.HyperVVirtual && a.Up && a.HasGateway)) return adapters;
        return adapters.Select(a => a.IsPhysical && a.Up && a.Addresses.Count == 0 ? a with { VSwitchUplink = true } : a).ToList();
    }
}
