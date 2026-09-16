using NetPaw.Adapters;
using NetPaw.Arp;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;

namespace NetPaw.Scan;

/// <summary>
/// Auto-switch: when a link comes up, identify the network by its gateway MAC (+ DHCP server + subnet)
/// and apply the one profile whose learned fingerprint matches. Rules, all enforced here: only profiles
/// with AutoSwitch on; exactly one match (ambiguity → nothing); not if that profile already runs; at most
/// one decision per link-up. Static candidates without a lease are identified by borrowing their own
/// address for one ARP, like the router finder. All I/O is injected, so the rules are unit-tested.
/// </summary>
public sealed class AutoSwitcher(IArpProvider arp, Func<IpAddr, CancellationToken, Task<bool>> addTemp, Func<IpAddr, CancellationToken, Task> removeTemp)
{
    public sealed record Decision(Profile? Profile, string Reason, NetworkFingerprint? Observed);

    /// <summary>Fingerprint of what the adapter runs right now (needs a gateway to ARP). Null when there is nothing to identify.</summary>
    public async Task<NetworkFingerprint?> Learn(AdapterInfo live, CancellationToken ct = default)
    {
        var addr = live.RealAddress;
        var gw = live.Gateways.FirstOrDefault();
        if (addr is null || gw is null) return null;
        var (mac, _) = await arp.Probe(gw, addr.Address, ct);
        return mac is null ? null : new NetworkFingerprint(mac, live.DhcpServers.FirstOrDefault(), $"{addr.Network}/{addr.PrefixLength}");
    }

    public async Task<Decision> Decide(AdapterInfo live, IReadOnlyList<Profile> profiles, CancellationToken ct = default) => await Decide(live, profiles, [], ct);

    /// <param name="declinedIds">Profiles the user declined on this machine — never offered again.</param>
    public async Task<Decision> Decide(AdapterInfo live, IReadOnlyList<Profile> profiles, IReadOnlyCollection<string> declinedIds, CancellationToken ct = default)
    {
        var candidates = profiles.Where(p => p.AutoSwitch && p.Fingerprint is not null && !p.Temporary && !declinedIds.Contains(p.Id)
                                            && (p.Adapter is null || string.Equals(p.Adapter, live.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.AdapterMac, live.Mac, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return new Decision(null, "no auto-switch profiles for this adapter", null);

        // With a lease (or any address + gateway): one ARP tells us where we are.
        var observed = await Learn(live, ct);
        if (observed is not null)
        {
            var hits = candidates.Where(p => p.Fingerprint!.Matches(observed)).ToList();
            return hits.Count switch
            {
                0 => new Decision(null, $"observed {observed} matches no profile", observed),
                1 when ApplyPlanner.Matches(hits[0], live) => new Decision(null, $"'{hits[0].Name}' already applied", observed),
                1 => new Decision(hits[0], $"observed {observed}", observed),
                _ => new Decision(null, $"ambiguous: {string.Join(", ", hits.Select(h => h.Name))}", observed),
            };
        }

        // No lease: static networks. Borrow each static candidate's own primary address and ARP its gateway (max 5).
        foreach (var p in candidates.Where(p => !p.Dhcp && p.Gateway is not null && p.Primary is not null).Take(5))
        {
            ct.ThrowIfCancellationRequested();
            var temp = p.Primary!;
            if (!await addTemp(temp, ct)) continue;
            try
            {
                var (mac, _) = await arp.Probe(p.Gateway!, temp.Address, ct);
                if (mac is not null && string.Equals(mac, p.Fingerprint!.GatewayMac, StringComparison.OrdinalIgnoreCase))
                    return new Decision(p, $"gateway {p.Gateway} answered with {mac}", new NetworkFingerprint(mac, null, $"{temp.Network}/{temp.PrefixLength}"));
            }
            finally { await removeTemp(temp, CancellationToken.None); }
        }
        return new Decision(null, "no static candidate's gateway answered", null);
    }
}
