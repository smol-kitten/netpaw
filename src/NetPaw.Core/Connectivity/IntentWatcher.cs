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
    /// <summary>How long this connection attempt has been stuck, in whole seconds.</summary>
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

    readonly Dictionary<string, (TcpEndpoint Endpoint, DateTimeOffset First, int Seen)> _pending = [];
    readonly Dictionary<string, DateTimeOffset> _reported = [];

    /// <summary>How many consecutive samples (one per tick) a socket must sit in SYN_SENT before it is reported. 2 = the
    /// original behaviour (seen twice). A flaky updater that retries for a few seconds never reaches 3–4.</summary>
    public int MinSamples { get; set; } = 3;
    /// <summary>Remote addresses or networks ("10.0.0.5", "10.0.0.0/24") that never produce a report.</summary>
    public IReadOnlyList<string> IgnoreTargets { get; set; } = [];
    /// <summary>Process names (without .exe, case-insensitive) that never produce a report.</summary>
    public IReadOnlyList<string> IgnoreProcesses { get; set; } = [];

    /// <summary>Feeds one sample; returns the endpoints that were pending for <see cref="MinSamples"/> consecutive samples and are not in cooldown.</summary>
    public List<(TcpEndpoint Endpoint, DateTimeOffset Since)> Sample(IReadOnlyList<TcpEndpoint> now, DateTimeOffset at)
    {
        var stuck = new List<(TcpEndpoint, DateTimeOffset)>();
        var seen = new HashSet<string>();
        var min = Math.Max(2, MinSamples);
        foreach (var e in now.Where(Keep))
        {
            seen.Add(e.Key);
            if (_pending.TryGetValue(e.Key, out var p))
            {
                _pending[e.Key] = p = (p.Endpoint, p.First, p.Seen + 1);
                if (p.Seen >= min && (!_reported.TryGetValue(e.Remote, out var last) || at - last >= Cooldown))
                {
                    _reported[e.Remote] = at;
                    stuck.Add((e, p.First));
                }
            }
            else _pending[e.Key] = (e, at, 1);
        }
        foreach (var k in _pending.Keys.Where(k => !seen.Contains(k)).ToList()) _pending.Remove(k);
        foreach (var k in _reported.Where(kv => at - kv.Value >= Cooldown).Select(kv => kv.Key).ToList()) _reported.Remove(k);
        return stuck.Take(MaxReported).ToList();
    }

    /// <summary>Filters the TCP table: not our own process, not loopback, only SYN_SENT-like states.</summary>
    bool Keep(TcpEndpoint e)
    {
        if (e.Pid == ownPid) return false;
        var r = e.Remote;
        if (r.StartsWith("127.") || r.StartsWith("169.254.") || r.StartsWith("0.")) return false;
        if (!int.TryParse(r.Split('.')[0], out var first) || first >= 224) return false;      // multicast, broadcast, reserved
        if (IgnoreTargets.Any(t => Net.IpMath.TryParseCidr(t, out var c, 32) && Net.IpMath.Contains(c.Address, c.PrefixLength, r))) return false;
        if (IgnoreProcesses.Count > 0 && processName(e.Pid) is { } name && IgnoreProcesses.Select(Normalise).Any(p => p.Length > 0 && p.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    static string Normalise(string process) { var p = process.Trim(); return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p; }

    /// <summary>Why the target is unreachable: on-link but silent (ARP), off-subnet without a gateway, or unknown.</summary>
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

    /// <summary>Names the stuck target from profiles, presets and the ARP cache, and attaches the reach suggestion.</summary>
    public StuckTarget Describe(TcpEndpoint e, DateTimeOffset since, AdapterInfo? adapter, IReadOnlyList<ArpEntry> arp, IEnumerable<Profile> profiles, IEnumerable<Preset> presets, int defaultPrefix = 24)
    {
        string name;
        try { name = processName(e.Pid) ?? $"pid {e.Pid}"; } catch (Exception) { name = $"pid {e.Pid}"; }
        return new StuckTarget(e.Remote, e.Port, e.Pid, name, Classify(e.Remote, adapter, arp), ReachResolver.Resolve(e.Remote, adapter, profiles, presets, defaultPrefix), since);
    }
}
