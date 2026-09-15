using NetPaw.Net;

namespace NetPaw.Model;

/// <summary>A persistent static route bound to the profile's adapter.</summary>
public sealed record StaticRoute(string Destination, int PrefixLength, string? Gateway, int? Metric)
{
    public string Prefix => $"{IpMath.NetworkOf(Destination, PrefixLength)}/{PrefixLength}";
    public bool Contains(string ip) => IpMath.Contains(Destination, PrefixLength, ip);

    public override string ToString() =>
        Prefix + (Gateway is null ? "" : $" via {Gateway}") + (Metric is null ? "" : $" metric {Metric}");

    /// <summary>Parses "10.0.0.0/8 via 192.168.1.1 metric 10" (gateway/metric optional).</summary>
    public static bool TryParse(string text, out StaticRoute route)
    {
        route = null!;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !IpMath.TryParseCidr(parts[0], out var dest)) return false;
        string? gw = null; int? metric = null;
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "via": if (!IpMath.IsIPv4(parts[i + 1])) return false; gw = parts[i + 1]; break;
                case "metric": if (!int.TryParse(parts[i + 1], out var m) || m < 0) return false; metric = m; break;
                default: return false;
            }
        }
        route = new StaticRoute(dest.Network, dest.PrefixLength, gw, metric);
        return true;
    }
}
