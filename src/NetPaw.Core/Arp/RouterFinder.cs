using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Presets;

namespace NetPaw.Arp;

/// <summary>A subnet worth trying on an unknown port, with the addresses a router would typically answer on.</summary>
public sealed record RouterCandidate(string Network, int Prefix, IReadOnlyList<string> GatewayGuesses, IReadOnlyList<string> Vendors)
{
    public string Cidr => $"{Network}/{Prefix}";
}

public sealed record RouterHit(RouterCandidate Candidate, string Ip, string Mac, int Ms, IReadOnlyList<string> Vendors);

/// <summary>
/// "What network is on this cable?" — puts a temporary address into each common subnet, ARPs the usual
/// gateway addresses (.1, .254 and every preset default in that subnet), and reports who answered.
/// Nothing but ARP leaves the machine; each temporary address is removed before the next subnet.
/// The add/remove and probe steps are injected, so the candidate list and aggregation are unit-tested.
/// </summary>
public sealed class RouterFinder(Func<IpAddr, CancellationToken, Task<bool>> addTemp, Func<IpAddr, CancellationToken, Task> removeTemp, IArpProvider arp)
{
    /// <summary>Well-known home/SOHO/enterprise defaults not already covered by a preset.</summary>
    public static readonly string[] CommonNetworks =
    [
        "192.168.0.0/24", "192.168.1.0/24", "192.168.2.0/24", "192.168.10.0/24", "192.168.100.0/24", "192.168.178.0/24", "192.168.88.0/24", "192.168.50.0/24",
        "10.0.0.0/24", "10.0.1.0/24", "10.1.1.0/24", "10.10.10.0/24", "10.1.10.0/24", "172.16.0.0/24", "172.16.1.0/24", "172.16.16.0/24",
    ];

    public static List<RouterCandidate> Candidates(IEnumerable<Preset> presets, IEnumerable<string>? extraCidrs = null)
    {
        var map = new Dictionary<string, (HashSet<string> Gw, HashSet<string> Vendors)>();
        void Add(string network, int prefix, string? gw, string? vendor)
        {
            var key = $"{network}/{prefix}";
            if (!map.TryGetValue(key, out var e)) map[key] = e = ([], []);
            if (gw is not null) e.Gw.Add(gw);
            if (vendor is not null) e.Vendors.Add(vendor);
        }
        foreach (var c in CommonNetworks.Concat(extraCidrs ?? []))
            if (IpMath.TryParseCidr(c, out var a)) Add(a.Network, a.PrefixLength, null, null);
        foreach (var p in presets)
        {
            if (!IpMath.IsIPv4(p.Ip) || p.Prefix < 22) continue; // /8 and /16 defaults (D-Link 10.90.90.90/8) are probed as a /24 around the address
            Add(IpMath.NetworkOf(p.Ip, Math.Max(p.Prefix, 24)), Math.Max(p.Prefix, 24), p.Ip, p.Vendor);
        }
        foreach (var p in presets.Where(p => IpMath.IsIPv4(p.Ip) && p.Prefix < 22))
            Add(IpMath.NetworkOf(p.Ip, 24), 24, p.Ip, p.Vendor);
        return map.Select(kv =>
        {
            var parts = kv.Key.Split('/'); var net = parts[0]; var prefix = int.Parse(parts[1]);
            var guesses = new List<string> { IpMath.FromUInt(IpMath.ToUInt(net) + 1), IpMath.FromUInt(IpMath.ToUInt(IpMath.BroadcastOf(net, prefix)) - 1) };
            foreach (var g in kv.Value.Gw) if (!guesses.Contains(g)) guesses.Add(g);
            return new RouterCandidate(net, prefix, guesses, kv.Value.Vendors.OrderBy(v => v).ToList());
        }).OrderBy(c => IpMath.ToUInt(c.Network)).ToList();
    }

    /// <summary>The host address to borrow while probing a candidate: top of the subnet, never a gateway guess.</summary>
    public static IpAddr TempAddress(RouterCandidate c) =>
        new(IpMath.HostCandidates(c.Network, c.Prefix, c.GatewayGuesses).First(), c.Prefix);

    public async Task<List<RouterHit>> Find(IReadOnlyList<RouterCandidate> candidates, IReadOnlyList<AdapterInfo> adapters, string adapterName, IProgress<(int Index, int Total, RouterCandidate Candidate, RouterHit? Hit)>? progress = null, CancellationToken ct = default)
    {
        var hits = new List<RouterHit>();
        var own = adapters.FirstOrDefault(a => a.Name == adapterName)?.Addresses ?? [];
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            progress?.Report((i, candidates.Count, c, null));
            // Already on-link? Probe without touching the adapter.
            var existing = own.FirstOrDefault(a => a.Network == c.Network && a.PrefixLength == c.Prefix);
            var temp = existing is null ? TempAddress(c) : null;
            var source = existing?.Address ?? temp!.Address;
            if (temp is not null && !await addTemp(temp, ct)) { progress?.Report((i, candidates.Count, c, null)); continue; }
            try
            {
                var answers = await ArpReachability.Check(arp, c.GatewayGuesses, source, parallel: 8, ct: ct);
                foreach (var a in answers)
                {
                    var hit = new RouterHit(c, a.Ip, a.Mac, a.Ms ?? 0, c.Vendors);
                    hits.Add(hit); progress?.Report((i, candidates.Count, c, hit));
                }
            }
            finally { if (temp is not null) await removeTemp(temp, CancellationToken.None); }
        }
        return hits;
    }
}
