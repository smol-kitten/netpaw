using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Model;

namespace NetPaw.Scan;

/// <summary>How well a candidate worked once applied. Higher is better; ties broken by gateway round-trip.</summary>
public sealed record ScanResult(Profile Candidate, Snapshot Snapshot, int Score, string Verdict)
{
    public bool Working => Snapshot.GatewayOk;
    public bool FullyWorking => Snapshot.GatewayOk && Snapshot.InternetOk && (Snapshot.DnsCheck is null || Snapshot.DnsOk);
    public int GatewayMs => Snapshot.Intranet.FirstOrDefault(p => p.Ok)?.Ms ?? int.MaxValue;
}

public enum ScanMode { FirstWorking, RankAll }

public sealed record ScanProgress(int Index, int Total, Profile Candidate, ScanResult? Result);

/// <summary>
/// Tries profiles on an adapter until one works, or tries all of them and ranks by usability.
/// The apply+measure step is injected, so the ordering, stop rule, scoring and restore logic are
/// unit-tested without touching a network. The caller restores the original configuration when
/// the user does not keep a result.
/// </summary>
public sealed class NetworkScanner(Func<Profile, CancellationToken, Task<Snapshot>> applyAndMeasure)
{
    /// <summary>DHCP first (least intrusive, most often right), then static profiles in list order.</summary>
    public static List<Profile> Candidates(IEnumerable<Profile> profiles, AdapterInfo adapter, bool includeDhcp)
    {
        var list = new List<Profile>();
        if (includeDhcp) list.Add(new Profile { Id = "scan-dhcp", Name = "DHCP", Dhcp = true, Adapter = adapter.Name, Temporary = true });
        list.AddRange(profiles.Where(p => !p.Temporary && !p.Dhcp && (p.Adapter is null || string.Equals(p.Adapter, adapter.Name, StringComparison.OrdinalIgnoreCase))));
        return list;
    }

    public static int Score(Snapshot s)
    {
        if (!s.Link) return 0;
        var score = 0;
        if (s.HasAddress) score += 1;
        if (s.HasGateway) score += 1;
        if (s.GatewayOk) score += 3;
        if (s.InternetOk) score += 3;
        if (s.DnsCheck is { Ok: true }) score += 2;
        return score;
    }

    public static string Verdict(Snapshot s) => !s.Link ? "link down" : !s.HasAddress ? "no address" : !s.GatewayOk ? "gateway silent" : !s.InternetOk ? "intranet only" : s.DnsCheck is { Ok: false } ? "internet, DNS failing" : "online";

    public async Task<List<ScanResult>> Run(IReadOnlyList<Profile> candidates, ScanMode mode, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<ScanResult>();
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            progress?.Report(new ScanProgress(i, candidates.Count, c, null));
            var snap = await applyAndMeasure(c, ct);
            var r = new ScanResult(c, snap, Score(snap), Verdict(snap));
            results.Add(r);
            progress?.Report(new ScanProgress(i, candidates.Count, c, r));
            if (mode == ScanMode.FirstWorking && r.FullyWorking) break;
        }
        return Rank(results);
    }

    public static List<ScanResult> Rank(IEnumerable<ScanResult> results) =>
        results.OrderByDescending(r => r.Score).ThenBy(r => r.GatewayMs).ThenBy(r => r.Candidate.Dhcp ? 0 : 1).ToList();
}
