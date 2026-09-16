using NetPaw.Adapters;
using NetPaw.Planning;

namespace NetPaw.Connectivity;

public enum AdvisorySeverity { Info, Warning, Error }

/// <summary>A plain-language finding about the current configuration and whether a DHCP renew could fix it.</summary>
public enum RepairKind { None, Renew, Release, Reset, Prefer, SetMtu, DeleteRoute, Reach }

public sealed record Advisory(AdvisorySeverity Severity, string Title, string Text, bool CanRenew)
{
    /// <summary>The one-click repair, and which adapter it targets. Renewable findings are RepairKind.Renew on the adapter itself.</summary>
    public RepairKind Repair { get; init; } = CanRenew ? RepairKind.Renew : RepairKind.None;
    public string? RepairAdapter { get; init; }
    /// <summary>The adapter the finding is about (set by <see cref="Advisor.Analyze(Snapshot, IReadOnlyList{AdapterInfo}?)"/>).</summary>
    public string? Adapter { get; init; }
    /// <summary>Numeric argument for the repair (the MTU for <see cref="RepairKind.SetMtu"/>).</summary>
    public int? Value { get; init; }
    /// <summary>The route a <see cref="RepairKind.DeleteRoute"/> repair removes.</summary>
    public Planning.RouteEntry? Route { get; init; }
    /// <summary>The address a <see cref="RepairKind.Reach"/> repair reaches (intent watcher).</summary>
    public string? Target { get; init; }
    public override string ToString() => $"{Title}: {Text}";
}

/// <summary>
/// Looks at a snapshot the way an admin would: is the DHCP lease complete (address, gateway, DNS),
/// do the pieces answer, is a static profile missing something obvious. Local analysis only —
/// nothing here touches the network; probes come from the snapshot when checks are enabled.
/// </summary>
public static class Advisor
{
    public static IReadOnlyList<Advisory> Analyze(Snapshot s) => Analyze(s, null);

    /// <param name="others">All adapters on the machine, for cross-adapter findings (held addresses, competing gateways).</param>
    public static IReadOnlyList<Advisory> Analyze(Snapshot s, IReadOnlyList<AdapterInfo>? others) =>
        s.Adapter is null ? [] : AnalyzeCore(s, others).Select(x => x.Adapter is null ? x with { Adapter = s.Adapter.Name } : x).ToList();

    static List<Advisory> AnalyzeCore(Snapshot s, IReadOnlyList<AdapterInfo>? others)
    {
        var list = new List<Advisory>();
        var a = s.Adapter;
        if (a is null || !s.Link) return list;
        if (others is not null) list.AddRange(CrossAdapter(a, s, others));
        // Per-server DNS verdicts (checks on): name the dead one when another answers; say so when none does.
        if (s.ChecksEnabled && s.DnsServers.Count > 0)
        {
            var dead = s.DnsServers.Where(d => !d.Ok).Select(d => d.Target).ToList();
            var alive = s.DnsServers.Where(d => d.Ok).Select(d => d.Target).ToList();
            if (dead.Count > 0 && alive.Count > 0)
                list.Add(new(AdvisorySeverity.Warning, $"DNS server {string.Join(", ", dead)} does not answer",
                    $"{string.Join(", ", alive)} answers. Every lookup that hits {dead[0]} first waits for its timeout before falling back — remove or replace it{(a.Dhcp ? " on the DHCP server" : " in the profile")}.", CanRenew: false));
            else if (alive.Count == 0 && s.DnsCheck is null)
                list.Add(new(AdvisorySeverity.Warning, "No DNS server answers", $"{string.Join(", ", dead)} gave no reply to a direct query. Names will not resolve{(s.GatewayOk ? " although the gateway answers" : "")}.", CanRenew: a.Dhcp));
        }
        if (s.Wlan is { Weak: true } w)
            list.Add(new(AdvisorySeverity.Warning, $"Weak Wi-Fi signal ({w.Signal} %)", $"'{w.Ssid}' on channel {w.Channel?.ToString() ?? "?"} at {w.Signal} % — drops, retries and slow lookups are expected. Move closer, switch band, or use the cable.", CanRenew: false));
        if (s.PathMtu is { Reduced: true } m && !a.Wireless && !(others is not null && VpnDetector.Detect(others).Any(v => v.Up)))
            list.Add(new(AdvisorySeverity.Warning, $"Path MTU {m.Mtu} on {a.Name}", $"Packets larger than {m.Mtu} bytes are dropped on the way to {m.Host} (a tunnel or PPPoE upstream). Pages hang after the handshake; set the adapter MTU to {m.Mtu} so Windows fragments before sending.", CanRenew: false) { Repair = RepairKind.SetMtu, RepairAdapter = a.Name, Value = m.Mtu });
        if (s.Dot1x is { Failed: true } d && !s.HasAddress)
        {
            list.Add(new(AdvisorySeverity.Error, $"802.1X authentication failed on {a.Name}", $"The switch rejected this port's credentials or certificate ({d.State}). DHCP cannot answer on a closed port — this is not a lease problem. Check the machine/user certificate or the Wired AutoConfig profile.", CanRenew: false));
            return list;
        }
        if (s.Dot1x is { InProgress: true } && !s.HasAddress)
            list.Add(new(AdvisorySeverity.Info, "802.1X authentication in progress", $"{a.Name} is still authenticating with the switch; DHCP follows once the port opens.", CanRenew: false));
        // Two manual default routes on this interface: the older one (a VPN or a lab session) is stale and steals traffic by metric.
        if (s.Routes is { Count: > 0 })
        {
            var defaults = s.Routes.Where(r => r.Default && r.Manual && r.InterfaceIndex == a.Index).OrderBy(r => r.Metric).ToList();
            if (defaults.Count > 1)
            {
                var stale = defaults.Skip(1).First(); var kept = defaults[0];
                list.Add(new(AdvisorySeverity.Warning, $"Two default routes on {a.Name}", $"0.0.0.0/0 via {kept.Gateway} (metric {kept.Metric}) and via {stale.Gateway} (metric {stale.Metric}) are both manual. The second one is a leftover; delete it.", CanRenew: false) { Repair = RepairKind.DeleteRoute, RepairAdapter = a.Name, Route = stale });
            }
        }
        if (a.VSwitchUplink)
        {
            list.Add(new(AdvisorySeverity.Info, "Bound to a Hyper-V virtual switch", $"{a.Name} carries the external switch and has no host address by design. Configure the host on its vEthernet adapter instead — pick it as the work adapter (tray menu → Work adapter).", CanRenew: false));
            return list;
        }
        if (a.Dhcp)
        {
            if (!s.HasAddress)
                list.Add(new(AdvisorySeverity.Error, "No DHCP lease", "The adapter is set to DHCP but got no address (self-assigned 169.254.x.x). The DHCP server is not answering on this link.", CanRenew: true));
            else
            {
                if (!s.HasGateway) list.Add(new(AdvisorySeverity.Warning, "Lease without gateway", "DHCP handed out an address but no default gateway (option 3). Off-link traffic will fail.", CanRenew: true));
                if (a.Dns.Count == 0) list.Add(new(AdvisorySeverity.Warning, "Lease without DNS", "DHCP handed out no DNS servers (option 6). Names will not resolve.", CanRenew: true));
                if (s.ChecksEnabled && s.HasGateway && s.Intranet.Count > 0 && !s.GatewayOk)
                    list.Add(new(AdvisorySeverity.Error, "Gateway from lease does not answer", $"{a.Gateways[0]} did not reply. The lease may be stale (moved to another switch/VLAN) or the router is down.", CanRenew: true));
                if (s.ChecksEnabled && s.InternetOk && s.DnsCheck is { Ok: false })
                    list.Add(new(AdvisorySeverity.Warning, "DNS servers not answering", $"Internet is reachable but {string.Join(", ", a.Dns)} did not resolve {s.DnsCheck.Target}. A renew may hand out working servers.", CanRenew: true));
            }
        }
        else
        {
            if (s.HasAddress && !s.HasGateway && !a.Addresses.All(x => x.Address.StartsWith("169.254.")))
                list.Add(new(AdvisorySeverity.Info, "Static profile without gateway", "Fine for a setup subnet; off-link traffic and internet will not work until a profile with a gateway is applied.", CanRenew: false));
            if (s.ChecksEnabled && s.HasGateway && s.Intranet.Count > 0 && !s.GatewayOk)
                list.Add(new(AdvisorySeverity.Error, "Gateway does not answer", $"{a.Gateways[0]} did not reply. Wrong subnet or VLAN for this port, or the router is down.", CanRenew: false));
            if (a.Dns.Count == 0 && s.HasGateway)
                list.Add(new(AdvisorySeverity.Info, "No DNS servers", "This profile sets no DNS; names will not resolve.", CanRenew: false));
            if (s.ChecksEnabled && s.InternetOk && s.DnsCheck is { Ok: false })
                list.Add(new(AdvisorySeverity.Warning, "DNS servers not answering", $"{string.Join(", ", a.Dns)} did not resolve {s.DnsCheck.Target}.", CanRenew: false));
        }
        return list;
    }

    /// <summary>
    /// The dock problem: the old NIC (now unplugged) still holds the address the DHCP server wants to
    /// offer the new NIC, so Windows refuses the lease and the new NIC ends up on 169.254. And the
    /// "both up" problem: two adapters with default gateways — Windows picks by metric, often the wrong one.
    /// </summary>
    static IEnumerable<Advisory> CrossAdapter(AdapterInfo a, Snapshot s, IReadOnlyList<AdapterInfo> all)
    {
        var others = all.Where(o => o.Name != a.Name && o.HostFacing).ToList();
        if (a.Dhcp && !s.HasAddress)
        {
            // The dock case: the unplugged NIC still owns a lease; when this adapter already knows its DHCP server,
            // only a holder in that server's subnet can be the reason for the refused offer.
            var server = a.DhcpServers.FirstOrDefault();
            var holder = others.FirstOrDefault(o => !o.Up && o.HasRealAddress && (server is null || o.Addresses.Any(x => x.Contains(server))));
            if (holder is not null)
                yield return new Advisory(AdvisorySeverity.Warning, "Address held by another adapter",
                    $"{holder.Name} (disconnected) still holds {string.Join(", ", holder.Addresses.Select(x => x.ToString()))}. Windows refuses a DHCP offer for an address another adapter still owns — reset that adapter (disable → enable) to let it go.", CanRenew: false)
                    { RepairAdapter = holder.Name, Repair = RepairKind.Reset }; // ipconfig /release refuses a media-disconnected NIC; disable→enable clears it
        }
        if (a.HasGateway)
        {
            var rivals = others.Where(o => o.Up && o.HasGateway).ToList();
            if (rivals.Count > 0)
            {
                var names = string.Join(", ", rivals.Select(r => $"{r.Name} ({r.Gateways[0]})"));
                yield return new Advisory(AdvisorySeverity.Info, "Two default gateways",
                    $"{a.Name} ({a.Gateways[0]}) and {names} both offer a default route. Windows sends off-link traffic to the lowest interface metric — usually the faster link, not necessarily the one you want. 'Prefer' pins the metrics.", CanRenew: false)
                    { RepairAdapter = a.Name, Repair = RepairKind.Prefer };
            }
        }
    }

    /// <summary>`ipconfig /release "<adapter>"` on the adapter that still owns the address.</summary>
    /// <summary>One stuck connection as advice: who, what, why, and the cheapest way to make it reachable.</summary>
    public static Advisory FromStuck(StuckTarget t, DateTimeOffset now)
    {
        var why = t.Kind == StuckKind.OnLinkSilent ? "the host is on this subnet but does not answer: down, or on another VLAN" : "no reply from beyond the gateway";
        var d = t.Decision;
        var (sev, fix) = d.Kind switch
        {
            Reach.ReachKind.UseProfile => (AdvisorySeverity.Warning, $"Profile '{d.Profile!.Name}' covers it."),
            Reach.ReachKind.UsePreset => (AdvisorySeverity.Warning, $"Preset {d.Preset!.Vendor} {d.Preset.Model} covers it (its subnet, temporary address {d.Address})."),
            Reach.ReachKind.TempAddress => (AdvisorySeverity.Info, $"Nothing saved covers it; 'reach {t.Remote}' adds a temporary address {d.Address}."),
            Reach.ReachKind.AlreadyReachable => (AdvisorySeverity.Info, "This adapter already sits in its subnet; the host itself is silent."),
            _ => (AdvisorySeverity.Info, d.Explanation),
        };
        return new Advisory(sev, $"{t.Process} cannot reach {t.Remote}:{t.Port}", $"No reply for {t.SecondsWaiting(now)} s ({why}). {fix}", CanRenew: false)
        { Repair = d.Kind is Reach.ReachKind.UseProfile or Reach.ReachKind.UsePreset or Reach.ReachKind.TempAddress ? RepairKind.Reach : RepairKind.None, Target = t.Remote };
    }

    /// <summary>netsh set subinterface: the one knob that makes Windows fragment before a too-small link swallows the packet.</summary>
    public static ApplyPlan PlanSetMtu(AdapterInfo adapter, int mtu)
    {
        var plan = new ApplyPlan { Title = $"MTU {mtu} on {adapter.Name}", Adapter = adapter.Name };
        plan.Steps.Add(Step.Netsh($"Interface MTU {mtu}", $"interface ipv4 set subinterface \"{adapter.Name}\" mtu={mtu} store=persistent"));
        return plan;
    }

    public static ApplyPlan PlanRelease(AdapterInfo adapter)
    {
        var plan = new ApplyPlan { Title = "release DHCP lease", Adapter = adapter.Name };
        plan.Steps.Add(new Step("Release DHCP lease", "ipconfig", $"/release \"{adapter.Name}\""));
        return plan;
    }

    /// <summary>Interface metric 10 on the preferred adapter, 50 on every other adapter that has a gateway — explicit instead of "automatic metric".</summary>
    public static ApplyPlan PlanPrefer(AdapterInfo preferred, IEnumerable<AdapterInfo> all)
    {
        var plan = new ApplyPlan { Title = $"prefer {preferred.Name}", Adapter = preferred.Name };
        plan.Steps.Add(Step.Netsh($"Metric 10 on {preferred.Name}", $"interface ipv4 set interface \"{preferred.Name}\" metric=10"));
        foreach (var o in all.Where(o => o.Name != preferred.Name && o.HasGateway))
            plan.Steps.Add(Step.Netsh($"Metric 50 on {o.Name}", $"interface ipv4 set interface \"{o.Name}\" metric=50", critical: false));
        return plan;
    }

    /// <summary>`ipconfig /renew "<adapter>"` — release+renew is deliberately avoided: it drops the address even when the server is gone.</summary>
    public static ApplyPlan PlanRenew(AdapterInfo adapter)
    {
        var plan = new ApplyPlan { Title = "renew DHCP lease", Adapter = adapter.Name };
        if (!adapter.Dhcp) { plan.Warnings.Add($"{adapter.Name} is static; nothing to renew."); return plan; }
        plan.Steps.Add(new Step("Renew DHCP lease", "ipconfig", $"/renew \"{adapter.Name}\""));
        return plan;
    }
}

public sealed class RepairSettings
{
    /// <summary>Show advisories (local analysis, no probes). On by default — it is just reading the config.</summary>
    public bool DhcpAdvisory { get; set; } = true;
    /// <summary>Run ipconfig /renew when an advisory says a renew could help. Off by default: it changes the adapter.</summary>
    public bool AutoRenew { get; set; }
    public int AutoRenewIntervalSeconds { get; set; } = 60;
    /// <summary>Attempts per incident; resets once the state is healthy again.</summary>
    public int AutoRenewMaxAttempts { get; set; } = 3;
    /// <summary>Write state changes and configuration findings with timestamps to incidents.jsonl. Off by default.</summary>
    public bool IncidentLog { get; set; }
    /// <summary>Scan: include a DHCP candidate before the static profiles.</summary>
    public bool ScanIncludeDhcp { get; set; } = true;
    /// <summary>Notify when a VPN comes up/down or switches between full and split tunnel.</summary>
    public bool VpnNotifications { get; set; } = true;
    /// <summary>Listen for LLDP/CDP (pktmon, passive) after every link-up so the card can say which switch port you are on. Off by default: it is a ~35 s capture.</summary>
    public bool DiscoverSwitchOnLinkUp { get; set; }
    /// <summary>Watch the TCP table for connections nobody answers and recommend the profile that covers the target. Local only; nothing is sent.</summary>
    public bool IntentWatch { get; set; } = true;
    public int DiscoverSeconds { get; set; } = 35;
}

/// <summary>Decides when an automatic renew is due: bounded per incident, spaced by the interval, never while healthy.</summary>
public sealed class RenewScheduler
{
    public int Attempts { get; private set; }
    public DateTimeOffset? LastAttempt { get; private set; }
    DateTimeOffset? _since;
    /// <summary>A renewable finding must persist this long before the first automatic attempt — DHCP usually finishes on its own within it.</summary>
    public static readonly TimeSpan MinPersist = TimeSpan.FromSeconds(10);

    public bool ShouldRenew(RepairSettings r, IReadOnlyList<Advisory> advisories, DateTimeOffset now)
    {
        if (!r.AutoRenew) return false;
        if (!advisories.Any(a => a.CanRenew)) { Reset(); return false; }
        _since ??= now;
        if (now - _since < MinPersist) return false;
        if (Attempts >= Math.Max(1, r.AutoRenewMaxAttempts)) return false;
        if (LastAttempt is { } last && now - last < TimeSpan.FromSeconds(Math.Max(15, r.AutoRenewIntervalSeconds))) return false;
        return true;
    }

    public void Mark(DateTimeOffset now) { Attempts++; LastAttempt = now; }
    public void Reset() { Attempts = 0; LastAttempt = null; _since = null; }
}
