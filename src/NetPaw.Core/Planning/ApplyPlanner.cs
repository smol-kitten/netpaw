using NetPaw.Adapters;
using NetPaw.Model;

namespace NetPaw.Planning;

/// <summary>
/// Turns "current adapter state + desired profile" into an ordered, explicit list of netsh/PowerShell
/// steps. Pure: no I/O, fully unit-tested. netsh was chosen over WMI because the plan is
/// human-readable, copy-pasteable, and behaves identically on every supported Windows build.
/// </summary>
public static class ApplyPlanner
{
    static string Q(string adapter) => "\"" + adapter + "\"";

    public static ApplyPlan Plan(Profile p, AdapterInfo adapter, VlanInfo? vlan = null)
    {
        var plan = new ApplyPlan { Title = p.Name, Adapter = adapter.Name };
        var n = Q(adapter.Name);
        if (!adapter.Up) plan.Warnings.Add($"Adapter '{adapter.Name}' is down; settings are stored but will only take effect once it comes up.");

        if (p.Dhcp)
        {
            // Always issued, even if the snapshot says "already DHCP": snapshots go stale (a scan may have
            // applied a static profile in between) and the command is idempotent.
            plan.Steps.Add(Step.Netsh("Switch to DHCP", $"interface ipv4 set address name={n} source=dhcp"));
            plan.Steps.Add(Step.Netsh("DNS from DHCP", $"interface ipv4 set dnsservers name={n} source=dhcp"));
        }
        else
        {
            var primary = p.Primary ?? throw new InvalidOperationException("Static profile without addresses.");
            var gw = p.Gateway is null ? "gateway=none" : $"gateway={p.Gateway} gwmetric={p.GatewayMetric ?? 1}";
            // "set address source=static" replaces every unicast address on the interface, so secondaries and
            // leftover temp addresses are wiped in this single step and re-added below in profile order.
            plan.Steps.Add(Step.Netsh($"Primary {primary}", $"interface ipv4 set address name={n} source=static address={primary.Address} mask={primary.Mask} {gw}"));
            foreach (var s in p.Secondaries)
                plan.Steps.Add(Step.Netsh($"Secondary {s}", $"interface ipv4 add address name={n} address={s.Address} mask={s.Mask}", critical: false));

            if (p.Dns.Count == 0)
                plan.Steps.Add(Step.Netsh("Clear DNS", $"interface ipv4 set dnsservers name={n} source=static address=none validate=no", critical: false));
            else
            {
                plan.Steps.Add(Step.Netsh($"DNS {p.Dns[0]}", $"interface ipv4 set dnsservers name={n} source=static address={p.Dns[0]} register=primary validate=no", critical: false));
                for (var i = 1; i < p.Dns.Count; i++)
                    plan.Steps.Add(Step.Netsh($"DNS {p.Dns[i]}", $"interface ipv4 add dnsservers name={n} address={p.Dns[i]} index={i + 1} validate=no", critical: false));
            }
        }

        if (p.InterfaceMetric is { } im)
            plan.Steps.Add(Step.Netsh($"Interface metric {im}", $"interface ipv4 set interface {n} metric={im}", critical: false));

        if (p.DnsSuffix is not null)
            plan.Steps.Add(Step.PowerShell($"DNS suffix '{p.DnsSuffix}'", $"Set-DnsClient -InterfaceAlias '{adapter.Name.Replace("'", "''")}' -ConnectionSpecificSuffix '{p.DnsSuffix.Replace("'", "''")}'", critical: false));

        foreach (var r in p.Routes)
        {
            var hop = r.Gateway is null ? "" : $" nexthop={r.Gateway}";
            var metric = r.Metric is null ? "" : $" metric={r.Metric}";
            // netsh refuses to add an existing route ("object already exists"), so re-applying a profile
            // would report a failed step. Delete first (best effort), then add.
            plan.Steps.Add(Step.Netsh($"Route {r} (clear old)", $"interface ipv4 delete route prefix={r.Prefix} interface={n}{hop}", critical: false, expectFailure: true));
            plan.Steps.Add(Step.Netsh($"Route {r}", $"interface ipv4 add route prefix={r.Prefix} interface={n}{hop}{metric} store=persistent", critical: false));
        }

        if (p.VlanId is { } vid)
        {
            if (vlan is { Capable: true })
            {
                if (vlan.VlanId == vid) plan.Warnings.Add($"VLAN {vid} is already set on the driver.");
                else plan.Steps.Add(VlanStep(adapter.Name, vid));
            }
            else plan.Warnings.Add($"Profile wants VLAN {vid} but the driver of '{adapter.Name}' exposes no VlanID property — skipped. Use a driver/vNIC that supports 802.1Q tagging.");
        }
        return plan;
    }

    /// <summary>Disable → enable: the documented cure for the Windows "static applied but ipconfig still shows DHCP / two gateways" state. Never automatic.</summary>
    public static ApplyPlan PlanResetAdapter(AdapterInfo adapter)
    {
        var plan = new ApplyPlan { Title = "reset adapter", Adapter = adapter.Name };
        if (adapter.HasGateway) plan.Warnings.Add($"{adapter.Name} carries the default route: the link drops for a few seconds and remote sessions over it will stall.");
        plan.Steps.Add(Step.Netsh("Disable adapter", $"interface set interface {Q(adapter.Name)} admin=disable"));
        plan.Steps.Add(Step.Netsh("Enable adapter", $"interface set interface {Q(adapter.Name)} admin=enable"));
        return plan;
    }

    /// <summary>Setting the driver keyword resets the adapter (link drops for a few seconds) — that is inherent to the driver, not to NetPaw.</summary>
    public static Step VlanStep(string adapterName, int vlanId) =>
        Step.PowerShell($"VLAN {vlanId} (adapter will reset)", $"Set-NetAdapterAdvancedProperty -Name '{adapterName.Replace("'", "''")}' -RegistryKeyword 'VlanID' -RegistryValue {vlanId}", critical: false);

    /// <summary>Adds a secondary address without touching the primary/gateway/DNS.</summary>
    public static ApplyPlan PlanAddSecondary(AdapterInfo adapter, IpAddr addr, string title)
    {
        var plan = new ApplyPlan { Title = title, Adapter = adapter.Name };
        if (adapter.Addresses.Any(a => a.Address == addr.Address)) { plan.Warnings.Add($"{addr.Address} is already on {adapter.Name}."); return plan; }
        if (adapter.Dhcp) plan.Warnings.Add("Adapter is on DHCP: netsh cannot add a static secondary next to a DHCP lease. Switch to a static profile first (or use 'replace' mode).");
        else plan.Steps.Add(Step.Netsh($"Add {addr}", $"interface ipv4 add address name={Q(adapter.Name)} address={addr.Address} mask={addr.Mask}"));
        return plan;
    }

    public static ApplyPlan PlanRemoveAddresses(string adapterName, IEnumerable<IpAddr> addrs, string title)
    {
        var plan = new ApplyPlan { Title = title, Adapter = adapterName };
        foreach (var a in addrs)
            plan.Steps.Add(Step.Netsh($"Remove {a}", $"interface ipv4 delete address name={Q(adapterName)} address={a.Address}", critical: false));
        return plan;
    }

    /// <summary>Quick "DHCP now" without a profile.</summary>
    public static ApplyPlan PlanDhcp(AdapterInfo adapter)
    {
        var p = new Profile { Name = "DHCP", Dhcp = true };
        var plan = Plan(p, adapter); plan.Profile = p; return plan;
    }

    /// <summary>"Is this profile what the adapter currently runs" for the ✓ in menus: primary must match, extra addresses and DNS are tolerated. Strict comparison lives in <see cref="ApplyVerifier"/>.</summary>
    public static bool Matches(Profile p, AdapterInfo adapter) => ApplyVerifier.Compare(p, adapter, strict: false).Count == 0;
}
