using NetPaw.Adapters;
using NetPaw.Planning;

namespace NetPaw.Connectivity;

public enum AdvisorySeverity { Info, Warning, Error }

/// <summary>A plain-language finding about the current configuration and whether a DHCP renew could fix it.</summary>
public enum RepairKind { None, Renew, Release, Reset, Prefer }

public sealed record Advisory(AdvisorySeverity Severity, string Title, string Text, bool CanRenew)
{
    /// <summary>A one-click repair other than renew, and which adapter it targets.</summary>
    public RepairKind Repair { get; init; } = RepairKind.None;
    public string? RepairAdapter { get; init; }
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
    public static IReadOnlyList<Advisory> Analyze(Snapshot s, IReadOnlyList<AdapterInfo>? others)
    {
        var list = new List<Advisory>();
        var a = s.Adapter;
        if (a is null || !s.Link) return list;
        if (others is not null) list.AddRange(CrossAdapter(a, s, others));
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
            var holder = others.FirstOrDefault(o => !o.Up && o.Addresses.Any(x => !x.Address.StartsWith("169.254.")));
            if (holder is not null)
                yield return new Advisory(AdvisorySeverity.Warning, "Address held by another adapter",
                    $"{holder.Name}{(holder.Up ? "" : " (disconnected)")} still holds {string.Join(", ", holder.Addresses.Select(x => x.ToString()))}. Windows refuses a DHCP offer for an address another adapter still owns — release it there or reset that adapter.", CanRenew: false)
                    { RepairAdapter = holder.Name, Repair = holder.Dhcp ? RepairKind.Release : RepairKind.Reset };
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
