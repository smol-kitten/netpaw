using NetPaw.Adapters;
using NetPaw.Model;

namespace NetPaw.Planning;

/// <summary>
/// netsh returning 0 does not mean Windows adopted the change: the documented "static applied, ipconfig
/// still says DHCP / two gateways" state exists. So after an apply NetPaw re-reads the adapter and lists
/// what differs from the profile. A non-empty list is shown with a *Reset adapter* action.
/// </summary>
public static class ApplyVerifier
{
    /// <param name="strict">true = post-apply verification (extra addresses, second gateway and DNS count); false = "is this profile active" for menu ticks.</param>
    /// <param name="routes">Live routes on the adapter (null = not read; route checks skipped).</param>
    /// <param name="interfaceMetric">Live interface metric (null = not read).</param>
    public static List<string> Compare(Profile p, AdapterInfo live, bool strict = true, IReadOnlyList<RouteEntry>? routes = null, int? interfaceMetric = null)
    {
        var diffs = new List<string>();
        if (p.Dhcp)
        {
            if (!live.Dhcp) diffs.Add("adapter still reports a static configuration");
            return diffs;
        }
        if (live.Dhcp) diffs.Add("adapter still reports DHCP enabled");
        if (p.Primary is null || live.Primary is null || p.Primary != live.Primary) diffs.Add($"primary is {live.Primary?.ToString() ?? "none"}, expected {p.Primary?.ToString() ?? "none"}");
        var want = p.Addresses.Select(a => a.ToString()).ToHashSet();
        var have = live.Addresses.Select(a => a.ToString()).ToHashSet();
        foreach (var a in want.Except(have).Where(a => a != p.Primary?.ToString())) diffs.Add($"address {a} missing");
        if (strict) foreach (var a in have.Except(want).Where(x => !x.StartsWith("169.254."))) diffs.Add($"unexpected address {a}");
        if (p.Gateway is null && live.HasGateway) diffs.Add($"unexpected gateway {live.Gateways[0]}");
        if (p.Gateway is not null && !live.Gateways.Contains(p.Gateway)) diffs.Add($"gateway {p.Gateway} missing");
        if (strict && live.Gateways.Count > 1) diffs.Add($"two default gateways ({string.Join(", ", live.Gateways)}) — the stale one usually comes from the previous DHCP lease");
        if (strict && p.Dns.Count > 0 && !p.Dns.SequenceEqual(live.Dns)) diffs.Add($"DNS is {(live.Dns.Count == 0 ? "empty" : string.Join(", ", live.Dns))}, expected {string.Join(", ", p.Dns)}");
        if (strict && routes is not null)
            foreach (var r in p.Routes)
            {
                var got = routes.FirstOrDefault(x => x.Prefix == r.Prefix);
                if (got is null) { diffs.Add($"route {r.Prefix} missing"); continue; }
                if (r.Gateway is not null && got.Gateway != r.Gateway) diffs.Add($"route {r.Prefix} goes via {got.Gateway ?? "on-link"}, expected {r.Gateway}");
                if (r.Metric is not null && got.Metric != r.Metric) diffs.Add($"route {r.Prefix} has metric {got.Metric}, expected {r.Metric}");
            }
        if (strict && p.InterfaceMetric is not null && interfaceMetric is not null && interfaceMetric != p.InterfaceMetric) diffs.Add($"interface metric is {interfaceMetric}, expected {p.InterfaceMetric}");
        return diffs;
    }
}
