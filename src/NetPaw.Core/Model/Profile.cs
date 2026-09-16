namespace NetPaw.Model;

/// <summary>
/// A network profile. The first address is the primary; the rest are added as secondaries
/// (Windows "multiple IP addresses on one adapter"), so one profile can sit in several subnets.
/// </summary>
/// <summary>What identifies a wired network without any config on it: the gateway's MAC (unique per box, VRRP aside), the DHCP server, the subnet.</summary>
public sealed record NetworkFingerprint(string GatewayMac, string? DhcpServer, string Subnet)
{
    /// <summary>Every field present on both sides must match; a missing DHCP server on one side is not a mismatch.
    /// A virtual-router MAC (VRRP/HSRP/CARP) repeats across sites, so it only matches when the DHCP server confirms it.</summary>
    public bool Matches(NetworkFingerprint other) =>
        string.Equals(GatewayMac, other.GatewayMac, StringComparison.OrdinalIgnoreCase) && Subnet == other.Subnet
        && (IsVirtualRouterMac(GatewayMac)
            ? DhcpServer is not null && other.DhcpServer is not null && DhcpServer == other.DhcpServer
            : DhcpServer is null || other.DhcpServer is null || DhcpServer == other.DhcpServer);

    /// <summary>VRRP 00:00:5e:00:01:xx / IPv6 VRRP 00:00:5e:00:02:xx, HSRP v1 00:00:0c:07:ac:xx, HSRP v2 00:00:0c:9f:fx:xx. Not a per-box identity.</summary>
    public static bool IsVirtualRouterMac(string mac)
    {
        var m = mac.Replace('-', ':').ToLowerInvariant();
        return m.StartsWith("00:00:5e:00:01:") || m.StartsWith("00:00:5e:00:02:") || m.StartsWith("00:00:0c:07:ac:") || m.StartsWith("00:00:0c:9f:f");
    }
    public override string ToString() => $"gw {GatewayMac} · {Subnet}{(DhcpServer is null ? "" : " · dhcp " + DhcpServer)}";
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Adapter name (alias) or null = the configured work adapter.</summary>
    public string? Adapter { get; set; }
    /// <summary>MAC of that adapter, so the binding survives "Ethernet" → "Ethernet 3" after a dock/driver change. Resolved before the name.</summary>
    public string? AdapterMac { get; set; }
    public bool Dhcp { get; set; }
    public List<IpAddr> Addresses { get; set; } = [];
    public string? Gateway { get; set; }
    public int? GatewayMetric { get; set; }
    public int? InterfaceMetric { get; set; }
    public List<string> Dns { get; set; } = [];
    public string? DnsSuffix { get; set; }
    /// <summary>802.1Q VLAN id set on the adapter (driver "VlanID" property); null = leave untouched, 0 = untagged.</summary>
    public int? VlanId { get; set; }
    public List<StaticRoute> Routes { get; set; } = [];
    /// <summary>Global hotkey, e.g. "Ctrl+Alt+1".</summary>
    public string? Hotkey { get; set; }
    public string? Note { get; set; }
    /// <summary>Created by reach-mode / presets; listed separately and safe to purge.</summary>
    public bool Temporary { get; set; }
    /// <summary>Network fingerprint learned while this profile was applied; used by auto-switch.</summary>
    public NetworkFingerprint? Fingerprint { get; set; }
    /// <summary>Apply automatically when the fingerprint matches on link-up. Off by default; the first automatic switch asks once.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool AutoSwitch { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool AutoSwitchConfirmed { get; set; }
    /// <summary>Deployed machine-wide (profiles.d / policy): visible and applicable, never editable from the UI. Not persisted in the user file.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Managed { get; set; }
    /// <summary>Where the profile came from when not the user's own file: "managed", a repo name, … Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Source { get; set; }

    public IpAddr? Primary => Addresses.Count > 0 ? Addresses[0] : null;
    public IEnumerable<IpAddr> Secondaries => Addresses.Skip(1);

    public bool Covers(string ip) =>
        Addresses.Any(a => a.Contains(ip)) || Routes.Any(r => r.Contains(ip));

    public string Summary() => Dhcp
        ? "DHCP"
        : string.Join(", ", Addresses.Select(a => a.ToString())) +
          (Gateway is null ? "" : $" gw {Gateway}") +
          (VlanId is null ? "" : $" vlan {VlanId}");

    public Profile Clone()
    {
        var c = (Profile)MemberwiseClone();
        c.Addresses = [.. Addresses];
        c.Dns = [.. Dns];
        c.Routes = [.. Routes];
        return c;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Name is required.");
        if (!Dhcp)
        {
            if (Addresses.Count == 0) errors.Add("A static profile needs at least one address.");
            if (Gateway is not null && !Net.IpMath.IsIPv4(Gateway)) errors.Add($"Gateway '{Gateway}' is not an IPv4 address.");
            if (Gateway is not null && Addresses.Count > 0 && !Addresses.Any(a => a.Contains(Gateway)))
                errors.Add($"Gateway {Gateway} is not inside any of the profile's subnets.");
            foreach (var d in Dns) if (!Net.IpMath.IsIPv4(d)) errors.Add($"DNS '{d}' is not an IPv4 address.");
            var dup = Addresses.GroupBy(a => a.Address).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) errors.Add($"Address {dup.Key} is listed twice.");
        }
        if (VlanId is < 0 or > 4094) errors.Add("VLAN id must be 0..4094.");
        if (Hotkey is not null && !Hotkeys.HotkeyParser.TryParse(Hotkey, out _)) errors.Add($"Hotkey '{Hotkey}' is not valid (e.g. Ctrl+Alt+1).");
        return errors;
    }
}
