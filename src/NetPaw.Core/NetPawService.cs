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
    public MachineStore Machine { get; }
    public Policy Policy { get; private set; } = Policy.None;
    readonly Func<Policy> _policyReader;
    public IAdapterProvider Adapters { get; }
    public IVlanProvider Vlan { get; }
    public IStepRunner Runner { get; }
    public Settings Settings { get; private set; }
    public List<Profile> Profiles { get; private set; }
    public IReadOnlyList<Preset> Presets { get; private set; }

    public NetPawService(JsonStore store, IAdapterProvider adapters, IVlanProvider vlan, IStepRunner runner, MachineStore? machine = null, Func<Policy>? policy = null)
    {
        Store = store; Adapters = adapters; Vlan = vlan; Runner = runner;
        Machine = machine ?? new MachineStore();
        _policyReader = policy ?? PolicyReader.ReadMachine;
        Settings = new Settings(); Profiles = []; Presets = [];
        Reload();
    }

    public static NetPawService CreateDefault(string? dir = null)
    {
        IVlanProvider vlan = OperatingSystem.IsWindows() ? new WmiVlanProvider() : new NullVlanProvider();
        return new NetPawService(new JsonStore(dir ?? JsonStore.DefaultDirectory()), new NetworkInterfaceAdapterProvider(), vlan, new ProcessStepRunner());
    }

    /// <summary>Policy values win over the user's settings.json; the UI greys those fields.</summary>
    public void Reload()
    {
        Policy = _policyReader();
        Settings = Store.LoadSettings();
        if (Policy.WorkAdapter is not null) Settings.WorkAdapter = Policy.WorkAdapter;
        if (Policy.PanelHotkey is not null) Settings.PanelHotkey = Policy.PanelHotkey;
        if (Policy.ConfirmBeforeApply is { } c) Settings.ConfirmBeforeApply = c;
        var managed = Machine.Load();
        foreach (var e in Machine.Errors) Store.Log("managed profile skipped: " + e);
        Profiles = MachineStore.Merge(managed, Policy.AllowUserProfiles ? Store.LoadProfiles() : []);
        Presets = PresetLibrary.Load(Store.PresetsFile);
    }

    public IEnumerable<Profile> UserProfiles => Profiles.Where(p => !p.Managed);
    public IEnumerable<Profile> ManagedProfiles => Profiles.Where(p => p.Managed);

    /// <summary>Only the user's own profiles are written; managed ones stay in profiles.d.</summary>
    public void SaveProfiles()
    {
        Require(Capability.UserProfiles);
        Store.SaveProfiles(UserProfiles);
    }

    public bool Allowed(Capability c) => c switch
    {
        Capability.UserProfiles => Policy.AllowUserProfiles,
        Capability.UserRepos => Policy.AllowUserRepos,
        Capability.Reach => Policy.AllowReach,
        Capability.TempAddresses => Policy.AllowTempAddresses,
        Capability.Dhcp => Policy.AllowDhcp,
        Capability.UnsignedRepos => Policy.AllowUnsignedRepos,
        _ => true,
    };

    public void Require(Capability c) { if (!Allowed(c)) throw new DeniedByPolicyException(c); }
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

    public ApplyOutcome ApplyDhcp(AdapterInfo? adapter = null)
    {
        Require(Capability.Dhcp);
        return Apply(ApplyPlanner.PlanDhcp(adapter ?? ResolveAdapter(null)));
    }

    public ReachDecision ResolveReach(string target, AdapterInfo? adapter = null)
    {
        Require(Capability.Reach);
        adapter ??= WorkAdapter();
        return ReachResolver.Resolve(target, adapter, Profiles, Presets, Settings.ReachDefaultPrefix);
    }

    /// <summary>Executes a reach decision. Temp addresses go on as secondaries by default; "replace" builds a full temp profile instead.</summary>
    public (ApplyPlan Plan, ApplyOutcome? Outcome) Reach(ReachDecision d, AdapterInfo? adapter = null, bool dryRun = false, bool? replacePrimary = null)
    {
        adapter ??= ResolveAdapter(null);
        Require(Capability.Reach);
        if (d.Kind is ReachKind.TempAddress or ReachKind.UsePreset) Require(Capability.TempAddresses);
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
        Require(Capability.UserProfiles);
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
