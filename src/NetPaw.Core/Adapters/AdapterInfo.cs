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
    /// <summary>"1 Gbit/s", "100 Mbit/s" or "" when unknown.</summary>
    public string SpeedText => SpeedBps <= 0 ? "" : SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1_000_000_000.0:0.#} Gbit/s" : $"{SpeedBps / 1_000_000} Mbit/s";
    public IpAddr? Primary => Addresses.Count > 0 ? Addresses[0] : null;
    public bool HasGateway => Gateways.Count > 0;
    public bool Covers(string ip) => Addresses.Any(a => a.Contains(ip));

    public string Summary() => !Up ? "down"
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
    /// <summary>Picks the adapter NetPaw should act on: configured name, else the first physical one with a gateway, else the first physical one that is up.</summary>
    public static AdapterInfo? Pick(IReadOnlyList<AdapterInfo> adapters, string? preferredName)
    {
        if (preferredName is not null)
        {
            var named = adapters.FirstOrDefault(a => string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }
        var physical = adapters.Where(a => a.IsPhysical).ToList();
        return physical.FirstOrDefault(a => a.Up && a.HasGateway)
            ?? physical.FirstOrDefault(a => a.Up && a.Addresses.Count > 0)
            ?? physical.FirstOrDefault(a => a.Up)
            ?? physical.FirstOrDefault();
    }
}
