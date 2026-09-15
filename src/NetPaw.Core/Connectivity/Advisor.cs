using NetPaw.Adapters;
using NetPaw.Planning;

namespace NetPaw.Connectivity;

public enum AdvisorySeverity { Info, Warning, Error }

/// <summary>A plain-language finding about the current configuration and whether a DHCP renew could fix it.</summary>
public sealed record Advisory(AdvisorySeverity Severity, string Title, string Text, bool CanRenew)
{
    public override string ToString() => $"{Title}: {Text}";
}

/// <summary>
/// Looks at a snapshot the way an admin would: is the DHCP lease complete (address, gateway, DNS),
/// do the pieces answer, is a static profile missing something obvious. Local analysis only —
/// nothing here touches the network; probes come from the snapshot when checks are enabled.
/// </summary>
public static class Advisor
{
    public static IReadOnlyList<Advisory> Analyze(Snapshot s)
    {
        var list = new List<Advisory>();
        var a = s.Adapter;
        if (a is null || !s.Link) return list;
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
