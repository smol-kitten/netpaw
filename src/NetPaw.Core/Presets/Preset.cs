using NetPaw.Model;

namespace NetPaw.Presets;

/// <summary>Factory-default management address of a piece of gear, so you can talk to it out of the box.</summary>
public sealed class Preset
{
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Device address, e.g. 192.168.88.1.</summary>
    public string Ip { get; set; } = "";
    public int Prefix { get; set; } = 24;
    /// <summary>Optional fixed host address to use; otherwise one is picked from the top of the subnet.</summary>
    public string? HostIp { get; set; }
    public string? Note { get; set; }
    public string? Url { get; set; }
    /// <summary>Repository name when the preset came from an online pack; null for bundled/user presets. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Source { get; set; }

    public string Key => $"{Vendor}/{Model}";
    public IpAddr Device => new(Ip, Prefix);
    public override string ToString() => $"{Vendor} {Model} — {Ip}/{Prefix}";

    /// <summary>Builds the temporary profile for reaching this device: host in its subnet, device as gateway, no DNS.</summary>
    public Profile ToProfile(string? adapter, IEnumerable<string> avoid, string? hostOverride = null)
    {
        var host = hostOverride ?? HostIp ?? Net.IpMath.HostCandidates(Ip, Prefix, avoid).First();
        return new Profile
        {
            Name = $"{Vendor} {Model}",
            Adapter = adapter,
            Addresses = [new IpAddr(host, Prefix)],
            Gateway = Ip,
            Note = Note,
            Temporary = true,
        };
    }
}
