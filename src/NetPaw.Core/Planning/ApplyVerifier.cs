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
    public static List<string> Compare(Profile p, AdapterInfo live)
    {
        var diffs = new List<string>();
        if (p.Dhcp)
        {
            if (!live.Dhcp) diffs.Add("adapter still reports a static configuration");
            return diffs;
        }
        if (live.Dhcp) diffs.Add("adapter still reports DHCP enabled");
        var want = p.Addresses.Select(a => a.ToString()).ToHashSet();
        var have = live.Addresses.Select(a => a.ToString()).ToHashSet();
        foreach (var a in want.Except(have)) diffs.Add($"address {a} missing");
        foreach (var a in have.Except(want).Where(x => !x.StartsWith("169.254."))) diffs.Add($"unexpected address {a}");
        if (p.Gateway is null && live.HasGateway) diffs.Add($"unexpected gateway {live.Gateways[0]}");
        if (p.Gateway is not null && !live.Gateways.Contains(p.Gateway)) diffs.Add($"gateway {p.Gateway} missing");
        if (live.Gateways.Count > 1) diffs.Add($"two default gateways ({string.Join(", ", live.Gateways)}) — the stale one usually comes from the previous DHCP lease");
        if (p.Dns.Count > 0 && !p.Dns.SequenceEqual(live.Dns)) diffs.Add($"DNS is {(live.Dns.Count == 0 ? "empty" : string.Join(", ", live.Dns))}, expected {string.Join(", ", p.Dns)}");
        return diffs;
    }
}
