using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Presets;
using NetPaw.Reach;
using NetPaw.Store;

namespace NetPaw;

/// <summary>Everything the tray and the CLI share: load state, build plans, run them, remember temp addresses.</summary>
public sealed class NetPawService
{
    public JsonStore Store { get; }
    public IAdapterProvider Adapters { get; }
    public IVlanProvider Vlan { get; }
    public IStepRunner Runner { get; }
    public Settings Settings { get; private set; }
    public List<Profile> Profiles { get; private set; }
    public IReadOnlyList<Preset> Presets { get; private set; }

    public NetPawService(JsonStore store, IAdapterProvider adapters, IVlanProvider vlan, IStepRunner runner)
    {
        Store = store; Adapters = adapters; Vlan = vlan; Runner = runner;
        Settings = store.LoadSettings();
        Profiles = store.LoadProfiles();
        Presets = PresetLibrary.Load(store.PresetsFile);
    }

    public static NetPawService CreateDefault(string? dir = null)
    {
        IVlanProvider vlan = OperatingSystem.IsWindows() ? new WmiVlanProvider() : new NullVlanProvider();
        return new NetPawService(new JsonStore(dir ?? JsonStore.DefaultDirectory()), new NetworkInterfaceAdapterProvider(), vlan, new ProcessStepRunner());
    }

    public void Reload()
    {
        Settings = Store.LoadSettings();
        Profiles = Store.LoadProfiles();
        Presets = PresetLibrary.Load(Store.PresetsFile);
    }

    public void SaveProfiles() => Store.SaveProfiles(Profiles);
    public void SaveSettings() => Store.SaveSettings(Settings);

    public IReadOnlyList<AdapterInfo> GetAdapters() => Adapters.GetAdapters();

    public AdapterInfo? WorkAdapter(IReadOnlyList<AdapterInfo>? adapters = null) =>
        AdapterSelector.Pick(adapters ?? GetAdapters(), Settings.WorkAdapter);

    public AdapterInfo ResolveAdapter(string? name, IReadOnlyList<AdapterInfo>? adapters = null)
    {
        adapters ??= GetAdapters();
        var picked = AdapterSelector.Pick(adapters, name ?? Settings.WorkAdapter);
        if (picked is null) throw new InvalidOperationException("No usable network adapter found.");
        if (name is not null && !string.Equals(picked.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Adapter '{name}' not found. Available: {string.Join(", ", adapters.Select(a => a.Name))}");
        return picked;
    }

    public Profile? FindProfile(string nameOrId) =>
        Profiles.FirstOrDefault(p => p.Id == nameOrId)
        ?? Profiles.FirstOrDefault(p => string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault(p => p.Name.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase));

    public ApplyPlan PlanProfile(Profile p, AdapterInfo? adapter = null)
    {
        adapter ??= ResolveAdapter(p.Adapter);
        var vlan = p.VlanId is null ? null : Vlan.Query(adapter.Name);
        return ApplyPlanner.Plan(p, adapter, vlan);
    }

    public ApplyOutcome Apply(ApplyPlan plan)
    {
        var outcome = PlanExecutor.Execute(plan, Runner, Store.Log);
        // A full "set address" wipes every address on the adapter, so forget temp addresses we tracked there.
        if (plan.Steps.Any(s => s.Arguments.Contains(" set address ")))
            Store.SaveTemp(Store.LoadTemp().Where(t => !string.Equals(t.Adapter, plan.Adapter, StringComparison.OrdinalIgnoreCase)));
        return outcome;
    }

    public ApplyOutcome ApplyProfile(Profile p, AdapterInfo? adapter = null) => Apply(PlanProfile(p, adapter));

    public ApplyOutcome ApplyDhcp(AdapterInfo? adapter = null) => Apply(ApplyPlanner.PlanDhcp(adapter ?? ResolveAdapter(null)));

    public ReachDecision ResolveReach(string target, AdapterInfo? adapter = null)
    {
        adapter ??= WorkAdapter();
        return ReachResolver.Resolve(target, adapter, Profiles, Presets, Settings.ReachDefaultPrefix);
    }

    /// <summary>Executes a reach decision. Temp addresses go on as secondaries by default; "replace" builds a full temp profile instead.</summary>
    public (ApplyPlan Plan, ApplyOutcome? Outcome) Reach(ReachDecision d, AdapterInfo? adapter = null, bool dryRun = false, bool? replacePrimary = null)
    {
        adapter ??= ResolveAdapter(null);
        var replace = replacePrimary ?? !Settings.ReachAsSecondary;
        ApplyPlan plan;
        switch (d.Kind)
        {
            case ReachKind.UseProfile:
                plan = PlanProfile(d.Profile!, adapter);
                break;
            case ReachKind.UsePreset:
            case ReachKind.TempAddress:
                if (replace || adapter.Dhcp)
                {
                    var tmp = d.Preset?.ToProfile(adapter.Name, adapter.Addresses.Select(a => a.Address), d.Address!.Address)
                              ?? new Profile { Name = $"reach {d.Target}", Adapter = adapter.Name, Addresses = [d.Address!], Temporary = true };
                    plan = PlanProfile(tmp, adapter);
                    if (adapter.Dhcp && !replace) plan.Warnings.Add("Adapter was on DHCP, so the temporary address replaced the lease. Pick a profile or 'DHCP' to go back.");
                }
                else plan = ApplyPlanner.PlanAddSecondary(adapter, d.Address!, $"reach {d.Target}");
                break;
            case ReachKind.AlreadyReachable:
                return (new ApplyPlan { Title = "reach", Adapter = adapter.Name }, null);
            default:
                throw new InvalidOperationException(d.Explanation);
        }
        if (dryRun) return (plan, null);
        var outcome = Apply(plan);
        // Only a secondary is "temporary" in the removable sense; a full replace became the adapter's primary.
        var addedSecondary = plan.Steps.Any(s => s.Arguments.Contains(" add address "));
        if (outcome.Success && addedSecondary && (d.Kind is ReachKind.TempAddress or ReachKind.UsePreset))
        {
            var temp = Store.LoadTemp();
            temp.Add(new TempAddress(adapter.Name, d.Address!, d.Kind == ReachKind.UsePreset ? d.Preset!.Key : "reach " + d.Target, DateTimeOffset.Now));
            Store.SaveTemp(temp);
        }
        return (plan, outcome);
    }

    /// <summary>An explicitly chosen preset always wins over profile/route knowledge — the user said which device they want.</summary>
    public ReachDecision ResolvePreset(Preset preset, AdapterInfo adapter)
    {
        if (adapter.Covers(preset.Ip))
            return new ReachDecision(ReachKind.AlreadyReachable, preset.Ip, null, preset, adapter.Addresses.First(a => a.Contains(preset.Ip)), $"{adapter.Name} already covers {preset.Ip}.");
        var host = preset.HostIp ?? Net.IpMath.HostCandidates(preset.Ip, preset.Prefix, adapter.Addresses.Select(a => a.Address)).First();
        var addr = new IpAddr(host, preset.Prefix);
        return new ReachDecision(ReachKind.UsePreset, preset.Ip, null, preset, addr, $"{preset.Vendor} {preset.Model} at {preset.Ip}; using {addr}.");
    }

    public (ApplyPlan Plan, ApplyOutcome? Outcome) ApplyPreset(Preset preset, AdapterInfo? adapter = null, bool dryRun = false, bool? replacePrimary = null)
    {
        adapter ??= ResolveAdapter(null);
        var d = ResolvePreset(preset, adapter);
        if (d.Kind == ReachKind.AlreadyReachable) return (new ApplyPlan { Title = preset.ToString(), Adapter = adapter.Name }, null);
        return Reach(d, adapter, dryRun, replacePrimary);
    }

    public IReadOnlyList<TempAddress> TempAddresses => Store.LoadTemp();

    public ApplyOutcome? ClearTemp()
    {
        var temp = Store.LoadTemp();
        if (temp.Count == 0) return null;
        ApplyOutcome? last = null;
        foreach (var g in temp.GroupBy(t => t.Adapter))
            last = PlanExecutor.Execute(ApplyPlanner.PlanRemoveAddresses(g.Key, g.Select(t => t.Address), "clear temporary addresses"), Runner, Store.Log);
        Store.SaveTemp([]);
        return last;
    }

    /// <summary>Snapshot the adapter's live configuration into a profile.</summary>
    public Profile Capture(string name, AdapterInfo adapter)
    {
        var p = new Profile { Name = name, Adapter = adapter.Name, Dhcp = adapter.Dhcp };
        if (!adapter.Dhcp)
        {
            p.Addresses = [.. adapter.Addresses];
            p.Gateway = adapter.Gateways.FirstOrDefault();
            p.Dns = [.. adapter.Dns];
        }
        var v = Vlan.Query(adapter.Name);
        if (v.Capable && v.VlanId is > 0) p.VlanId = v.VlanId;
        return p;
    }
}
