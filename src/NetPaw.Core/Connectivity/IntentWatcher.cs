using NetPaw.Adapters;
using NetPaw.Arp;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Presets;
using NetPaw.Reach;

namespace NetPaw.Connectivity;

public enum StuckKind
{
    /// <summary>Target is in a directly connected subnet and nobody answered ARP: host down, or it lives on another VLAN.</summary>
    OnLinkSilent,
    /// <summary>Target is off-link; the gateway forwarded (or dropped) and nothing came back.</summary>
    OffLinkNoReply,
}

public sealed record StuckTarget(string Remote, int Port, int Pid, string Process, StuckKind Kind, ReachDecision Decision, DateTimeOffset Since)
{
    public int SecondsWaiting(DateTimeOffset now) => (int)(now - Since).TotalSeconds;
}

/// <summary>
/// "Edge is trying 192.168.88.1 and nobody answers." Samples the SYN_SENT rows every couple of seconds;
/// a row seen twice is stuck (a real handshake spends milliseconds there). The reason comes from the
/// ARP cache, the recommendation from the reach resolver. Pure: every rule has a test with a fake table.
/// </summary>
public sealed class IntentWatcher(int ownPid, Func<int, string?> processName)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    public const int MaxReported = 3;

    readonly Dictionary<string, (TcpEndpoint Endpoint, DateTimeOffset First)> _pending = [];
    readonly Dictionary<string, DateTimeOffset> _reported = [];

    /// <summary>Feeds one sample; returns the endpoints that were already pending on the previous sample and are not in cooldown.</summary>
    public List<(TcpEndpoint Endpoint, DateTimeOffset Since)> Sample(IReadOnlyList<TcpEndpoint> now, DateTimeOffset at)
    {
        var stuck = new List<(TcpEndpoint, DateTimeOffset)>();
        var seen = new HashSet<string>();
        foreach (var e in now.Where(Keep))
        {
            seen.Add(e.Key);
            if (_pending.TryGetValue(e.Key, out var p))
            {
                if (!_reported.TryGetValue(e.Remote, out var last) || at - last >= Cooldown)
                {
                    _reported[e.Remote] = at;
                    stuck.Add((e, p.First));
                }
            }
            else _pending[e.Key] = (e, at);
        }
        foreach (var k in _pending.Keys.Where(k => !seen.Contains(k)).ToList()) _pending.Remove(k);
        foreach (var k in _reported.Where(kv => at - kv.Value >= Cooldown).Select(kv => kv.Key).ToList()) _reported.Remove(k);
        return stuck.Take(MaxReported).ToList();
    }

    bool Keep(TcpEndpoint e)
    {
        if (e.Pid == ownPid) return false;
        var r = e.Remote;
        if (r.StartsWith("127.") || r.StartsWith("169.254.") || r.StartsWith("0.")) return false;
        if (!int.TryParse(r.Split('.')[0], out var first) || first >= 224) return false;      // multicast, broadcast, reserved
        return true;
    }

    public static StuckKind Classify(string remote, AdapterInfo? adapter, IReadOnlyList<ArpEntry> arp)
    {
        if (adapter is not null && adapter.Covers(remote))
        {
            var entry = arp.FirstOrDefault(a => a.Ip == remote);
            var answered = entry is not null && !entry.Mac.StartsWith("00-00-00") && !entry.Mac.StartsWith("00:00:00") && !entry.Kind.Contains("invalid", StringComparison.OrdinalIgnoreCase);
            if (!answered) return StuckKind.OnLinkSilent;
        }
        return StuckKind.OffLinkNoReply;
    }

    public StuckTarget Describe(TcpEndpoint e, DateTimeOffset since, AdapterInfo? adapter, IReadOnlyList<ArpEntry> arp, IEnumerable<Profile> profiles, IEnumerable<Preset> presets, int defaultPrefix = 24)
    {
        string name;
        try { name = processName(e.Pid) ?? $"pid {e.Pid}"; } catch (Exception) { name = $"pid {e.Pid}"; }
        return new StuckTarget(e.Remote, e.Port, e.Pid, name, Classify(e.Remote, adapter, arp), ReachResolver.Resolve(e.Remote, adapter, profiles, presets, defaultPrefix), since);
    }
}
