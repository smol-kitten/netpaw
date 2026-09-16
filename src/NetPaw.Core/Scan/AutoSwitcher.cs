using NetPaw.Adapters;
using NetPaw.Arp;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;

namespace NetPaw.Scan;

/// <summary>
/// Auto-switch: when a link comes up, identify the network (gateway MAC, DHCP server, subnet, DNS suffix,
/// ARP neighbours, and when known the LLDP switch port and Wi-Fi BSSID) and apply the one profile whose
/// learned fingerprint scores best. Rules, all enforced here: only profiles with AutoSwitch on; best score
/// at or above <see cref="NetworkFingerprint.MatchThreshold"/> and ahead of the runner-up by
/// <see cref="NetworkFingerprint.Margin"/> (else ambiguous → nothing); not if that profile already runs; at most
/// one decision per link-up. Static candidates without a lease are identified by borrowing their own
/// address for one ARP, like the router finder. All I/O is injected, so the rules are unit-tested.
/// </summary>
public sealed class AutoSwitcher(IArpProvider arp, Func<IpAddr, CancellationToken, Task<bool>> addTemp, Func<IpAddr, CancellationToken, Task> removeTemp)
{
    public sealed record Decision(Profile? Profile, string Reason, NetworkFingerprint? Observed);

    /// <summary>Optional: SSID/BSSID for a wireless adapter (null = not available on this platform / not wireless).</summary>
    public Func<AdapterInfo, CancellationToken, Task<WlanInfo?>>? Wlan { get; init; }
    /// <summary>Optional: the switch neighbour last seen on this adapter, if recent enough to trust (the caller decides the age).</summary>
    public Func<string, Discovery.SwitchNeighbor?>? Switch { get; init; }

    /// <summary>Fingerprint of what the adapter runs right now (needs a gateway to ARP). Null when there is nothing to identify.</summary>
    public async Task<NetworkFingerprint?> Learn(AdapterInfo live, CancellationToken ct = default)
    {
        var addr = live.RealAddress;
        var gw = live.Gateways.FirstOrDefault();
        if (addr is null || gw is null) return null;
        var (mac, _) = await arp.Probe(gw, addr.Address, ct);
        if (mac is null) return null;
        var fp = new NetworkFingerprint(mac, live.DhcpServers.FirstOrDefault(), $"{addr.Network}/{addr.PrefixLength}") { DnsSuffix = live.DnsSuffix };
        var sw = Switch?.Invoke(live.Name);
        if (sw is not null) fp = fp with { SwitchChassis = sw.ChassisId ?? sw.SystemName, SwitchPort = sw.PortId };
        if (live.Wireless && Wlan is not null && await Wlan(live, ct) is { Bssid: not null } w) fp = fp with { Ssid = w.Ssid, Bssid = w.Bssid };
        var neighbours = NeighbourMacs(addr, gw, mac);
        return neighbours.Count > 0 ? fp with { NeighbourMacs = neighbours } : fp;
    }

    /// <summary>Up to three other unicast MACs on the subnet from the passive ARP cache, lowest IP first so the pick is stable.</summary>
    List<string> NeighbourMacs(IpAddr self, string gw, string gwMac)
    {
        try
        {
            return arp.ReadCache()
                .Where(e => self.Contains(e.Ip) && e.Ip != gw && e.Ip != self.Address && !string.Equals(e.Mac, gwMac, StringComparison.OrdinalIgnoreCase) && !IsMulticastOrBroadcast(e.Mac))
                .OrderBy(e => IpMath.ToUInt(e.Ip)).Select(e => e.Mac.ToLowerInvariant()).Distinct().Take(3).ToList();
        }
        catch (Exception) { return []; }
    }

    static bool IsMulticastOrBroadcast(string mac)
    {
        var m = mac.Replace('-', ':').ToLowerInvariant();
        return m.StartsWith("ff:ff:ff") || m.StartsWith("01:00:5e") || m.StartsWith("33:33");
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
            var scored = candidates.Select(p => (p, score: p.Fingerprint!.Score(observed))).Where(x => x.score >= NetworkFingerprint.MatchThreshold).OrderByDescending(x => x.score).ToList();
            if (scored.Count == 0) return new Decision(null, $"observed {observed} matches no profile", observed);
            var (best, bestScore) = scored[0];
            if (scored.Count > 1 && bestScore - scored[1].score < NetworkFingerprint.Margin)
                return new Decision(null, $"ambiguous: {string.Join(", ", scored.Select(x => $"{x.p.Name} ({x.score})"))}", observed);
            return ApplyPlanner.Matches(best, live)
                ? new Decision(null, $"'{best.Name}' already applied", observed)
                : new Decision(best, $"observed {observed} → '{best.Name}' scores {bestScore}", observed);
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
