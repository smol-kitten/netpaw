using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;

namespace NetPaw.Connectivity;

/// <summary>One probe sent from a chosen source address (ICMP echo with an optional TTL, or one TCP connect).</summary>
/// <param name="Kind">reply, ttl-expired, timeout, unreachable, open, refused, error.</param>
/// <param name="From">The address that answered (the target, or the hop that reported TTL expired / unreachable).</param>
public sealed record SourceProbe(string Kind, string? From, int? Ms)
{
    public bool Reply => Kind is "reply" or "open";
}

/// <summary>Probes that bind to a given source address. Windows' ICMP API supports a source address without elevation
/// (IcmpSendEcho2Ex); a TCP connect binds its local endpoint. The source decides which address the router and the
/// target see, which is the whole point of comparing two addresses on one cable.</summary>
public interface ISourceProber
{
    Task<SourceProbe> Ping(string target, string source, int ttl, int timeoutMs, CancellationToken ct);
    Task<SourceProbe> Connect(string target, int port, string source, int timeoutMs, CancellationToken ct);
    /// <summary>ARP for ip from source; the MAC or null.</summary>
    Task<string?> Arp(string ip, string source, CancellationToken ct);
}

public enum DiagVerdict
{
    /// <summary>Every check answered.</summary>
    Reachable,
    /// <summary>The target answers TCP but not ICMP: a firewall drops echo, the path is fine.</summary>
    IcmpFiltered,
    /// <summary>This PC has no usable route for this source: no route, or the route's gateway is not on the source's subnet.</summary>
    PcRouting,
    /// <summary>The gateway does not answer ARP: cable, VLAN, or the gateway address is wrong for this subnet.</summary>
    LocalLink,
    /// <summary>The gateway is on the cable (ARP) but does not answer this source: router interface / ACL for this subnet.</summary>
    RouterInterface,
    /// <summary>The router answers but does not forward this source (hop 1 silent or the far side silent): router policy, NAT or routing.</summary>
    RouterPolicy,
    /// <summary>The router forwards (far side answers) but the target does not answer this source: the target lacks a route back or its host firewall drops the subnet.</summary>
    TargetReturnPath,
    /// <summary>Target silent and no far-side address given to split router policy from target return path.</summary>
    BeyondRouter,
}

public sealed record DiagStep(string Source, string Check, bool? Ok, string Detail);

public sealed record SourceReport(IpAddr Source, string? Gateway, DiagVerdict Verdict, IReadOnlyList<DiagStep> Steps, string Explanation);

public sealed record DiagReport(string Target, int? Port, string? FarSide, IReadOnlyList<SourceReport> Sources, IReadOnlyList<string> Conclusions)
{
    public bool AnyReachable => Sources.Any(s => s.Verdict is DiagVerdict.Reachable or DiagVerdict.IcmpFiltered);
}

/// <summary>
/// "Why can 192.168.1.20 reach the server but 10.17.0.5 on the same cable cannot?" — runs the same ladder from every
/// address on the adapter (route → gateway ARP → gateway ping → hop 1 → far side → target → TCP) and names the layer
/// where each source stops. The far-side address (the router's address in the target's network) splits "the router does
/// not forward me" from "the target does not answer me". Pure logic lives in <see cref="Judge"/> and
/// <see cref="MatchRoute"/>; the probes are injected.
/// </summary>
public static class PathDiagnosis
{
    /// <summary>Longest-prefix, then lowest-metric route for target. Null when the table has no matching route.</summary>
    public static RouteEntry? MatchRoute(IEnumerable<RouteEntry> routes, string target)
    {
        RouteEntry? best = null; var bestLen = -1;
        foreach (var r in routes)
        {
            if (!IpMath.TryParseCidr(r.Prefix, out var p, 0)) continue;
            if (!IpMath.Contains(p.Address, p.PrefixLength, target)) continue;
            if (p.PrefixLength > bestLen || (p.PrefixLength == bestLen && best is not null && r.Metric < best.Metric)) { best = r; bestLen = p.PrefixLength; }
        }
        return best;
    }

    /// <summary>The verdict for one source from its step results. Steps missing (null Ok) are skipped, not counted.</summary>
    public static (DiagVerdict Verdict, string Explanation) Judge(bool hasRoute, bool gatewayOnSubnet, bool onLink, bool? arp, bool? gwPing, bool? hop1IsGateway, bool? farSide, bool? target, bool? tcp)
    {
        if (!hasRoute) return (DiagVerdict.PcRouting, "this PC has no route to the target for this source");
        var offSubnet = !onLink && !gatewayOnSubnet;
        if (target == true || tcp == true)
            return target == true ? (DiagVerdict.Reachable, "the target answers this source" + (offSubnet ? " (even though the route's gateway is on another subnet: the router accepts it)" : ""))
                                  : (DiagVerdict.IcmpFiltered, "TCP connects but ICMP echo is dropped: a firewall filters ping, the path works");
        if (onLink) return (DiagVerdict.TargetReturnPath, "same subnet, no gateway involved: the target does not answer this source (host firewall, or the target is down)");
        if (arp == false) return (DiagVerdict.LocalLink, "the gateway does not answer ARP: wrong VLAN, cable, or the gateway address is wrong for this subnet");
        // Measured first, then the most likely explanation: a gateway on another subnet is the usual reason a second address on one cable goes nowhere.
        if (offSubnet) return (DiagVerdict.PcRouting, "the route's gateway is not on this source's subnet: Windows hands this source's packets to another subnet's router, which drops or misroutes them (add a route for the target via this subnet's own gateway, or make that gateway the preferred default)");
        if (gwPing == false) return (DiagVerdict.RouterInterface, "the gateway is on the cable (ARP) but does not answer this source: router interface ACL / firewall for this subnet, or ICMP disabled on it");
        if (hop1IsGateway == false) return (DiagVerdict.RouterPolicy, "the router answers ping but does not forward this source's packets (hop 1 silent): router policy, NAT or a missing route on the router");
        if (farSide == false) return (DiagVerdict.RouterPolicy, "the router forwards toward its far side but the far-side address does not answer this source: policy / NAT / routing on the router for this subnet");
        if (farSide == true) return (DiagVerdict.TargetReturnPath, "the router forwards (far side answers this source) but the target does not: the target has no route back to this subnet or its host firewall drops it");
        return (DiagVerdict.BeyondRouter, "the router forwards (hop 1 answered) and the target is silent; give the router's far-side address (--via) to tell router policy from the target's return path");
    }

    public static async Task<DiagReport> Run(string target, int? port, AdapterInfo adapter, IEnumerable<IpAddr> sources, IReadOnlyList<RouteEntry> routes, string? farSide,
        ISourceProber prober, int timeoutMs = 1500, IProgress<DiagStep>? progress = null, CancellationToken ct = default)
    {
        var reports = new List<SourceReport>();
        foreach (var src in sources)
        {
            var steps = new List<DiagStep>();
            void Add(string check, bool? ok, string detail) { var s = new DiagStep(src.Address, check, ok, detail); steps.Add(s); progress?.Report(s); }
            var onLink = src.Contains(target);
            var route = MatchRoute(routes, target);
            var gw = onLink ? null : route?.Gateway;
            var hasRoute = onLink || route is not null;
            var gwOnSubnet = onLink || (gw is not null && src.Contains(gw)) || (route is { Gateway: null } && route.InterfaceIndex == adapter.Index);
            Add("route", hasRoute ? (gwOnSubnet ? true : null) : false, onLink ? $"on-link ({src})" : route is null ? "no route" : $"{route.Prefix} via {gw ?? "on-link"} on {(route.InterfaceIndex == adapter.Index ? adapter.Name : route.InterfaceName)} metric {route.Metric}{(gwOnSubnet ? "" : " — NOT on this source's subnet")}");
            bool? arp = null, gwPing = null, hop1 = null, far = null, tgt = null, tcp = null;
            if (hasRoute)
            {
                if (gw is not null)
                {
                    var mac = await prober.Arp(gw, src.Address, ct);
                    arp = mac is not null; Add("gateway arp", arp, mac ?? "no answer");
                    if (arp == true)
                    {
                        var p = await prober.Ping(gw, src.Address, 64, timeoutMs, ct); gwPing = p.Reply; Add("gateway ping", gwPing, p.Reply ? $"{p.Ms} ms" : p.Kind);
                        if (gwPing == true)
                        {
                            var h = await prober.Ping(target, src.Address, 1, timeoutMs, ct);
                            hop1 = h.Kind == "ttl-expired" && h.From == gw || h.Reply; Add("hop 1", hop1, h.Kind == "ttl-expired" ? $"{h.From} reported TTL expired{(h.From == gw ? "" : " (not the gateway!)")}" : h.Kind);
                            if (hop1 == true && farSide is not null && farSide != target)
                            {
                                var f = await prober.Ping(farSide, src.Address, 64, timeoutMs, ct); far = f.Reply; Add("far side", far, f.Reply ? $"{farSide} answers, {f.Ms} ms" : $"{farSide}: {f.Kind}");
                            }
                        }
                    }
                }
                if (arp != false && gwPing != false)
                {
                    var t = await prober.Ping(target, src.Address, 64, timeoutMs, ct); tgt = t.Reply; Add("target ping", tgt, t.Reply ? $"{t.Ms} ms" : t.Kind + (t.From is not null && t.From != target ? $" from {t.From}" : ""));
                    if (port is { } prt) { var c = await prober.Connect(target, prt, src.Address, timeoutMs, ct); tcp = c.Kind == "open"; Add($"tcp {prt}", tcp, c.Kind); }
                }
            }
            var (verdict, why) = Judge(hasRoute, gwOnSubnet, onLink, arp, gwPing, hop1, far, tgt, tcp);
            reports.Add(new SourceReport(src, gw, verdict, steps, why));
        }
        return new DiagReport(target, port, farSide, reports, Conclude(reports, target));
    }

    /// <summary>Cross-source conclusions: the same gateway box treating two sources differently is the strongest signal.</summary>
    public static IReadOnlyList<string> Conclude(IReadOnlyList<SourceReport> reports, string target)
    {
        var lines = new List<string>();
        var ok = reports.Where(r => r.Verdict is DiagVerdict.Reachable or DiagVerdict.IcmpFiltered).ToList();
        var bad = reports.Where(r => !(r.Verdict is DiagVerdict.Reachable or DiagVerdict.IcmpFiltered)).ToList();
        if (reports.Count == 0) { lines.Add("no IPv4 source address on the adapter — nothing to test"); return lines; }
        if (bad.Count == 0) { lines.Add($"{target} is reachable from every address on the adapter"); return lines; }
        if (ok.Count == 0) lines.Add($"{target} is not reachable from any address on the adapter: " + string.Join("; ", bad.Select(b => $"{b.Source.Address}: {b.Verdict} ({b.Explanation})")));
        foreach (var b in bad)
        {
            lines.Add($"{b.Source}: {b.Verdict} — {b.Explanation}");
            var twin = ok.FirstOrDefault(o => o.Gateway is not null && b.Gateway is not null);
            if (twin is not null && b.Verdict is DiagVerdict.RouterPolicy or DiagVerdict.TargetReturnPath or DiagVerdict.BeyondRouter or DiagVerdict.RouterInterface)
                lines.Add($"  {twin.Source.Address} (via {twin.Gateway}) reaches it on the same cable, so this PC, the cable and the VLAN are fine for {b.Source.Address}: the difference is how the router or the target treats {b.Source.Network}/{b.Source.PrefixLength}.");
            lines.Add(b.Verdict switch
            {
                DiagVerdict.PcRouting => "  next: `netpaw-cli routes`; add a route for the target via this subnet's gateway, or make this gateway the preferred default (prefer / metrics).",
                DiagVerdict.LocalLink => "  next: check the VLAN on this port and the gateway address of this subnet (`netpaw-cli arp`, `netpaw-cli switch`).",
                DiagVerdict.RouterInterface => "  next: on the router, the interface for this subnet: is it up, is ICMP allowed, is there an input ACL?",
                DiagVerdict.RouterPolicy => "  next: on the router, the forwarding rules for this subnet toward the target's network (firewall zone/policy, NAT, a route for the target's network).",
                DiagVerdict.TargetReturnPath => $"  next: on the target, `route print` / `ip route`: does it have a route back to {b.Source.Network}/{b.Source.PrefixLength} (its default gateway must know that subnet)? Then its host firewall scope.",
                DiagVerdict.BeyondRouter => "  next: rerun with --via <the router's address in the target's network> to split router policy from the target's return path.",
                _ => "",
            });
        }
        return lines.Where(l => l.Length > 0).ToList();
    }
}
