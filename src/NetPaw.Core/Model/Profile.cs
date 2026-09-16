namespace NetPaw.Model;

/// <summary>
/// A network profile. The first address is the primary; the rest are added as secondaries
/// (Windows "multiple IP addresses on one adapter"), so one profile can sit in several subnets.
/// </summary>
/// <summary>
/// What identifies a network without any config on it. Several signals, each weighted: LLDP/CDP switch identity and a
/// Wi-Fi BSSID are near-unique; a DHCP server and a real gateway MAC are strong; DNS suffix, SSID, subnet and ARP
/// neighbours are weak. Virtual-router MACs (VRRP/HSRP/CARP) repeat across sites and count almost nothing.
/// The three positional fields are the v0.7 shape; the rest are optional so old JSON loads unchanged.
/// </summary>
public sealed record NetworkFingerprint(string GatewayMac, string? DhcpServer, string Subnet)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? DnsSuffix { get; init; }
    /// <summary>LLDP/CDP chassis id (or system name) of the switch the cable ends on.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? SwitchChassis { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? SwitchPort { get; init; }
    /// <summary>Up to three other MACs seen on the subnet (ARP cache) — printers, servers, other boxes that stay put.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? NeighbourMacs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Ssid { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Bssid { get; init; }

    /// <summary>Score at or above this = the stored fingerprint describes the observed network.</summary>
    public const int MatchThreshold = 5;
    /// <summary>The best candidate must beat the runner-up by this much, else the situation is ambiguous.</summary>
    public const int Margin = 3;

    /// <summary>
    /// Weighted agreement between this (stored) fingerprint and an observed one. A signal missing on either side is
    /// neutral; a strong signal present on both but different is a penalty. Weights: switch chassis+port 5 (chassis
    /// alone 3), BSSID 4, real gateway MAC 4, DHCP server 3, DNS suffix 2, SSID 2, subnet 1, virtual-router MAC 1,
    /// each shared neighbour MAC 1 (max 3).
    /// </summary>
    public int Score(NetworkFingerprint o)
    {
        var s = 0;
        var macEq = Eq(GatewayMac, o.GatewayMac);
        if (macEq) s += IsVirtualRouterMac(GatewayMac) ? 1 : 4;
        else if (!IsVirtualRouterMac(GatewayMac) && !IsVirtualRouterMac(o.GatewayMac)) s -= 3;
        s += Both(DhcpServer, o.DhcpServer, 3, -3);
        s += Both(SwitchChassis, o.SwitchChassis, 3, -3);
        if (SwitchChassis is not null && Eq(SwitchChassis, o.SwitchChassis)) s += Both(SwitchPort, o.SwitchPort, 2, -1);
        s += Both(Bssid, o.Bssid, 4, -3);
        s += Both(Ssid, o.Ssid, 2, -1);
        s += Both(DnsSuffix, o.DnsSuffix, 2, -1);
        if (Subnet == o.Subnet) s += 1;
        if (NeighbourMacs is { Count: > 0 } && o.NeighbourMacs is { Count: > 0 })
            s += Math.Min(3, NeighbourMacs.Count(n => o.NeighbourMacs.Any(m => Eq(m, n))));
        return s;

        static int Both(string? a, string? b, int hit, int miss) => a is null || b is null ? 0 : Eq(a, b) ? hit : miss;
    }

    public bool Matches(NetworkFingerprint other) => Score(other) >= MatchThreshold;

    static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>VRRP 00:00:5e:00:01:xx / IPv6 VRRP 00:00:5e:00:02:xx, HSRP v1 00:00:0c:07:ac:xx, HSRP v2 00:00:0c:9f:fx:xx. Not a per-box identity.</summary>
    public static bool IsVirtualRouterMac(string mac)
    {
        var m = mac.Replace('-', ':').ToLowerInvariant();
        return m.StartsWith("00:00:5e:00:01:") || m.StartsWith("00:00:5e:00:02:") || m.StartsWith("00:00:0c:07:ac:") || m.StartsWith("00:00:0c:9f:f");
    }

    public override string ToString() => $"gw {GatewayMac} · {Subnet}{(DhcpServer is null ? "" : " · dhcp " + DhcpServer)}{(SwitchChassis is null ? "" : $" · switch {SwitchChassis}{(SwitchPort is null ? "" : "/" + SwitchPort)}")}{(Bssid is null ? "" : $" · wifi {Ssid} {Bssid}")}{(DnsSuffix is null ? "" : " · " + DnsSuffix)}";
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
