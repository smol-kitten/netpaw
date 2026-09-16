using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Presets;

namespace NetPaw.Reach;

public enum ReachKind
{
    /// <summary>The adapter already has an address (or a route) covering the target — nothing to do.</summary>
    AlreadyReachable,
    /// <summary>A saved profile covers the target; apply it.</summary>
    UseProfile,
    /// <summary>A preset's device address equals the target; use its subnet knowledge.</summary>
    UsePreset,
    /// <summary>No knowledge: add a temporary address in an assumed subnet around the target.</summary>
    TempAddress,
    /// <summary>"host:port" — a TCP connect check, no address change.</summary>
    PortCheck,
    Invalid,
}

public sealed record ReachDecision(ReachKind Kind, string Target, Profile? Profile, Preset? Preset, IpAddr? Address, string Explanation);

/// <summary>
/// "I want to talk to 192.168.88.1 — make it happen." Prefers knowledge that already exists
/// (current addresses, saved profiles, presets) over guessing, and when it has to guess it
/// picks a host address from the top of the assumed subnet so it will not collide with the
/// device's own default.
/// </summary>
public static class ReachResolver
{
    public static ReachDecision Resolve(string target, AdapterInfo? adapter, IEnumerable<Profile> profiles, IEnumerable<Preset> presets, int defaultPrefix = 24)
    {
        var t = target.Trim();
        if (Connectivity.PortCheck.TryParse(t, out var pcHost, out var pcPort))
            return new ReachDecision(ReachKind.PortCheck, $"{pcHost}:{pcPort}", null, null, null, $"Check whether TCP port {pcPort} on {pcHost} answers from this adapter.");
        if (!IpMath.TryParseCidr(t, out var targetAddr, defaultPrefix))
            return new ReachDecision(ReachKind.Invalid, t, null, null, null, $"'{t}' is not an IPv4 address.");
        var ip = targetAddr.Address;
        var explicitPrefix = t.Contains('/') || t.Contains(' ');

        if (adapter is not null && adapter.Covers(ip))
            return new ReachDecision(ReachKind.AlreadyReachable, ip, null, null, adapter.Addresses.First(a => a.Contains(ip)),
                $"{adapter.Name} already has {adapter.Addresses.First(a => a.Contains(ip))} which covers {ip}.");

        var byAddress = profiles.Where(p => !p.Dhcp && !p.Temporary && p.Addresses.Any(a => a.Contains(ip))).ToList();
        var byRoute = profiles.Where(p => !p.Dhcp && !p.Temporary && p.Routes.Any(r => r.Contains(ip))).ToList();
        // A profile whose directly connected subnet contains the target beats one that merely routes to it;
        // among ties prefer the most specific subnet.
        var best = byAddress.OrderByDescending(p => p.Addresses.Where(a => a.Contains(ip)).Max(a => a.PrefixLength)).FirstOrDefault()
                   ?? byRoute.OrderByDescending(p => p.Routes.Where(r => r.Contains(ip)).Max(r => r.PrefixLength)).FirstOrDefault();
        if (best is not null && !explicitPrefix)
            return new ReachDecision(ReachKind.UseProfile, ip, best, null, null, $"Profile '{best.Name}' covers {ip} ({best.Summary()}).");

        var preset = presets.FirstOrDefault(p => p.Ip == ip);
        var prefix = explicitPrefix ? targetAddr.PrefixLength : preset?.Prefix ?? defaultPrefix;
        var avoid = (adapter?.Addresses.Select(a => a.Address) ?? []).Concat(profiles.SelectMany(p => p.Addresses).Select(a => a.Address));
        var host = IpMath.HostCandidates(ip, prefix, avoid).FirstOrDefault();
        if (host is null)
            return new ReachDecision(ReachKind.Invalid, ip, null, null, null, $"No free host address in {IpMath.NetworkOf(ip, prefix)}/{prefix}.");
        var addr = new IpAddr(host, prefix);

        if (preset is not null && !explicitPrefix)
            return new ReachDecision(ReachKind.UsePreset, ip, null, preset, addr, $"{ip} is the default of {preset.Vendor} {preset.Model}; using {addr}.");

        var note = IpMath.IsPrivate(ip) ? "" : " (public address — are you sure it is on-link?)";
        return new ReachDecision(ReachKind.TempAddress, ip, null, null, addr, $"No profile covers {ip}; adding temporary {addr}{note}.");
    }
}
