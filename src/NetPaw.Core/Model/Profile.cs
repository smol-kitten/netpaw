namespace NetPaw.Model;

/// <summary>
/// A network profile. The first address is the primary; the rest are added as secondaries
/// (Windows "multiple IP addresses on one adapter"), so one profile can sit in several subnets.
/// </summary>
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
