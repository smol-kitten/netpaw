namespace NetPaw.Connectivity;

public sealed record Hop(int Ttl, string? Address, IReadOnlyList<int?> Ms)
{
    public bool Silent => Address is null;
    public string MsText => string.Join("  ", Ms.Select(m => m is null ? "*" : m + " ms"));
}

public sealed record TraceResult(string Host, IReadOnlyList<Hop> Hops, bool Reached)
{
    /// <summary>The last hop that answered before the silence — where the path stops.</summary>
    public Hop? LastAnswering => Hops.LastOrDefault(h => !h.Silent);
    public string Summary => Reached ? $"{Host} reached in {Hops.Count} hops" : LastAnswering is { } h ? $"path stops after hop {h.Ttl} ({h.Address}); {Hops.Count - h.Ttl} silent hops" : $"no hop answered — ICMP blocked on the first hop, or no route";
}

/// <summary>
/// Traceroute over ICMP TTL (no raw sockets, no elevation). Each hop gets <c>probes</c> pings in parallel;
/// the walk stops at the target or after three silent hops past the last answer (a firewall that drops
/// ICMP looks the same as the end of the world, so do not wait 30 hops for it).
/// </summary>
public static class Trace
{
    public static async Task<TraceResult> Run(IProbe probe, string host, int maxHops = 30, int probes = 3, int timeoutMs = 1000, IProgress<Hop>? progress = null, CancellationToken ct = default)
    {
        var hops = new List<Hop>(); var silentRun = 0;
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(Enumerable.Range(0, probes).Select(_ => probe.PingTtl(host, ttl, timeoutMs, ct)));
            var answered = results.FirstOrDefault(r => r.Detail is not null && r.Detail != "*");
            var hop = new Hop(ttl, answered?.Detail, results.Select(r => r.Detail is null || r.Detail == "*" ? (int?)null : r.Ms).ToList());
            hops.Add(hop); progress?.Report(hop);
            if (results.Any(r => r.Ok)) return new TraceResult(host, hops, true);
            silentRun = hop.Silent ? silentRun + 1 : 0;
            if (silentRun >= 3 && hops.Any(h => !h.Silent)) break;
            if (silentRun >= 5) break;
        }
        return new TraceResult(host, hops, false);
    }
}
