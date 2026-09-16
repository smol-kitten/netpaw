using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Presets;
using NetPaw.Reach;
using NetPaw.Repos;
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
    readonly Dictionary<string, (VlanInfo Info, DateTime At)> _vlanCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>WMI takes ~1 s per adapter; the editor asks repeatedly, so answers are kept for 30 s.</summary>
    public VlanInfo QueryVlan(string adapterName)
    {
        if (_vlanCache.TryGetValue(adapterName, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Info;
        var v = Vlan.Query(adapterName);
        _vlanCache[adapterName] = (v, DateTime.UtcNow);
        return v;
    }
    public IStepRunner Runner { get; }
    public Settings Settings { get; private set; }
    public List<Profile> Profiles { get; private set; }
    /// <summary>Bundled + user presets + entries of usable repositories.</summary>
    public IReadOnlyList<Preset> Presets { get; private set; }
    public RepoClient Repos { get; }
    public IReadOnlyList<RepoState> RepoStates { get; private set; } = [];
    /// <summary>Full profiles offered by repositories (kind "profile"); applied like presets, never saved unless copied.</summary>
    public IReadOnlyList<Profile> RepoProfiles { get; private set; } = [];

    public NetPawService(JsonStore store, IAdapterProvider adapters, IVlanProvider vlan, IStepRunner runner, MachineStore? machine = null, Func<Policy>? policy = null, RepoClient? repos = null)
    {
        if (OperatingSystem.IsWindows()) RouteReader = idx => RouteTable.Read(runner, idx);
        Store = store; Adapters = adapters; Vlan = vlan; Runner = runner;
        Machine = machine ?? new MachineStore();
        Repos = repos ?? new RepoClient(Path.Combine(store.Directory, "cache"));
        _policyReader = policy ?? PolicyReader.ReadMachine;
        Settings = new Settings(); Profiles = []; Presets = [];
        Reload();
    }

    public static NetPawService CreateDefault(string? dir = null)
    {
        var store = new JsonStore(dir ?? JsonStore.DefaultDirectory());
        var runner = new ProcessStepRunner { Timeout = cmd => store.Error("runner", $"killed after 30 s: {cmd}") };
        var adapters = new NetworkInterfaceAdapterProvider();
        IVlanProvider vlan = OperatingSystem.IsWindows()
            ? new RegistryVlanProvider(name => adapters.GetAdapters().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Id, new PowerShellVlanProvider(runner))
            : new NullVlanProvider();
        return new NetPawService(store, adapters, vlan, runner);
    }

    /// <summary>Policy values win over the user's settings.json; the UI greys those fields.</summary>
    public void Reload()
    {
        Policy = _policyReader();
        Settings = Store.LoadSettings();
        Store.MinLevel = Settings.LogLevel;
        if (Policy.WorkAdapter is not null) Settings.WorkAdapter = Policy.WorkAdapter;
        if (Policy.PanelHotkey is not null) Settings.PanelHotkey = Policy.PanelHotkey;
        if (Policy.ConfirmBeforeApply is { } c) Settings.ConfirmBeforeApply = c;
        var managed = Machine.Load();
        foreach (var e in Machine.Errors) Store.Log("managed profile skipped: " + e);
        Profiles = MachineStore.Merge(managed, Policy.AllowUserProfiles ? Store.LoadProfiles() : []);
        RebuildRepos(RepoRefs().Select(Repos.LoadCached).ToList());
    }

    /// <summary>Policy repos first (not removable), then the user's, de-duplicated by URL.</summary>
    public List<RepoRef> RepoRefs()
    {
        var list = Policy.RepoUrls.Select(u => RepoRef.Parse(u, fromPolicy: true)).ToList();
        if (Policy.AllowUserRepos)
            foreach (var u in Settings.RepoUrls)
            {
                var r = RepoRef.Parse(u);
                if (!list.Any(x => string.Equals(x.Url, r.Url, StringComparison.OrdinalIgnoreCase))) list.Add(r);
            }
        return list;
    }

    void RebuildRepos(List<RepoState> states)
    {
        RepoStates = states;
        var presets = PresetLibrary.Load(Store.PresetsFile).ToList();
        var profiles = new List<Profile>();
        foreach (var st in states.Where(s => s.Usable(Policy.AllowUnsignedRepos)))
            foreach (var e in st.Pack!.Entries)
            {
                if (e.ToPreset() is { } pr) { pr.Source = st.Name; presets.Add(pr); }
                else if (e.Kind == "profile" && e.Profile is not null)
                {
                    var p = e.Profile.Clone();
                    p.Id = "r-" + st.Name + "-" + e.Id; p.Source = st.Name; p.Hotkey = null;
                    if (string.IsNullOrWhiteSpace(p.Name)) p.Name = e.Title;
                    p.Note ??= e.Note;
                    if (p.Validate().Count == 0) profiles.Add(p);
                }
            }
        Presets = presets;
        RepoProfiles = profiles;
    }

    /// <summary>Fetches every configured repository (the only place NetPaw touches the network).</summary>
    public async Task<IReadOnlyList<RepoState>> SyncRepos(CancellationToken ct = default)
    {
        var states = new List<RepoState>();
        foreach (var r in RepoRefs()) states.Add(await Repos.Sync(r, ct));
        RebuildRepos(states);
        foreach (var s in states) Store.Log($"repo {s.Repo.Url}: {(s.Pack is null ? "unusable" : s.Count + " entries")} trust={s.Trust}{(s.Error is null ? "" : " — " + s.Error)}");
        return states;
    }

    public void AddRepo(string spec)
    {
        Require(Capability.UserRepos);
        var r = RepoRef.Parse(spec);
        if (!r.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !r.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("repository URLs must start with https://");
        if (RepoRefs().Any(x => string.Equals(x.Url, r.Url, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("that repository is already configured");
        Settings.RepoUrls.Add(r.ToSpec()); SaveSettings();
        RebuildRepos(RepoRefs().Select(Repos.LoadCached).ToList());
    }

    public void RemoveRepo(string url)
    {
        Require(Capability.UserRepos);
        var n = Settings.RepoUrls.RemoveAll(u => string.Equals(RepoRef.Parse(u).Url, url, StringComparison.OrdinalIgnoreCase));
        if (n == 0) throw new InvalidOperationException(RepoRefs().Any(x => x.FromPolicy && string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)) ? "that repository is set by policy" : "no such repository");
        SaveSettings(); Repos.Forget(url);
        RebuildRepos(RepoRefs().Select(Repos.LoadCached).ToList());
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
        Capability.Telemetry => Policy.AllowTelemetry,
        Capability.AutoSwitch => Policy.AllowAutoSwitch,
        Capability.Scan => Policy.AllowScan,
        Capability.Capture => Policy.AllowCapture,
        Capability.UpdateCheck => Policy.AllowUpdateCheck,
        Capability.IntentWatch => Policy.AllowIntentWatch,
        _ => true,
    };

    public void Require(Capability c) { if (!Allowed(c)) throw new DeniedByPolicyException(c); }
    public void SaveSettings() => Store.SaveSettings(Settings);

    /// <summary>Auto-switch confirmation lives in settings.json (per machine), so it also works for managed profiles, which are never written back.</summary>
    public bool AutoSwitchConfirmed(Profile p) => p.AutoSwitchConfirmed || Settings.AutoSwitchConfirmedIds.Contains(p.Id);
    public void ConfirmAutoSwitch(Profile p)
    {
        if (!Settings.AutoSwitchConfirmedIds.Contains(p.Id)) { Settings.AutoSwitchConfirmedIds.Add(p.Id); SaveSettings(); }
        if (!p.Managed && Allowed(Capability.UserProfiles)) { p.AutoSwitchConfirmed = true; SaveProfiles(); }
    }
    public void DeclineAutoSwitch(Profile p)
    {
        Settings.AutoSwitchDeclinedIds.Add(p.Id); SaveSettings();
        if (!p.Managed && Allowed(Capability.UserProfiles)) { p.AutoSwitch = false; SaveProfiles(); }
    }

    /// <summary>Stable random install id (created once, stored in settings.json).</summary>
    public string InstallId()
    {
        if (string.IsNullOrEmpty(Settings.InstallId)) { Settings.InstallId = Guid.NewGuid().ToString("N")[..16]; SaveSettings(); }
        return Settings.InstallId;
    }

    public IReadOnlyList<AdapterInfo> GetAdapters() => AdapterSelector.TagVSwitchUplinks(Adapters.GetAdapters());

    public AdapterInfo? WorkAdapter(IReadOnlyList<AdapterInfo>? adapters = null) =>
        AdapterSelector.Pick(adapters ?? GetAdapters(), Settings.WorkAdapter);

    /// <summary>Profile → adapter: the remembered MAC wins (survives renames and dock port changes), then the name, then the work adapter.</summary>
    public AdapterInfo AdapterFor(Profile p, IReadOnlyList<AdapterInfo>? adapters = null)
    {
        adapters ??= GetAdapters();
        // A Hyper-V host vEthernet shares the physical NIC's MAC: never resolve onto the address-less uplink.
        if (p.AdapterMac is { Length: > 0 } mac && adapters.Where(a => !a.VSwitchUplink).OrderBy(a => a.HyperVVirtual ? 0 : 1)
                .FirstOrDefault(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase)) is { } byMac) return byMac;
        if (p.Adapter is not null && adapters.Any(a => string.Equals(a.Name, p.Adapter, StringComparison.OrdinalIgnoreCase))) return ResolveAdapter(p.Adapter, adapters);
        if (p.Adapter is not null || p.AdapterMac is { Length: > 0 })
            throw new InvalidOperationException($"Adapter '{p.Adapter ?? p.AdapterMac}' for profile '{p.Name}' is not present (unplugged dock or USB NIC?). Pick another adapter in the editor or apply with -a.");
        return ResolveAdapter(null, adapters);
    }

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
        adapter ??= AdapterFor(p);
        var vlan = p.VlanId is null ? null : QueryVlan(adapter.Name);
        var plan = ApplyPlanner.Plan(p, adapter, vlan);
        plan.Profile = p;
        return plan;
    }

    public ApplyOutcome Apply(ApplyPlan plan)
    {
        // Any plan that touches the resolver set gets one flush at the end — renew, reset, borrow-return included.
        if (Settings.FlushDns && plan.ChangesDns && !plan.Steps.Any(s => s.Arguments == "/flushdns"))
            plan.Steps.Add(new Step("Flush DNS cache", "ipconfig", "/flushdns", Critical: false));
        var outcome = PlanExecutor.Execute(plan, Runner, Store.Log);
        // A full "set address" wipes every address on the adapter, so forget temp addresses we tracked there.
        if (plan.Steps.Any(s => s.Arguments.Contains(" set address ")))
            Store.SaveTemp(Store.LoadTemp().Where(t => !string.Equals(t.Adapter, plan.Adapter, StringComparison.OrdinalIgnoreCase)));
        return outcome;
    }

    public ApplyOutcome ApplyProfile(Profile p, AdapterInfo? adapter = null) => Apply(PlanProfile(p, adapter));

    /// <summary>Apply, wait for Windows to settle, re-read the adapter and report what it did not take. One code path for tray and CLI.</summary>
    /// <summary>Reads live routes + interface metric for verification. Windows: netsh through the runner; elsewhere: nothing (route checks skipped). Tests override.</summary>
    public Func<int, (IReadOnlyList<RouteEntry>? Routes, int? Metric)> RouteReader { get; set; } =
        _ => (null, null);

    public ApplyOutcome ApplyAndVerify(ApplyPlan plan, int settleMs = 2500)
    {
        var outcome = Apply(plan);
        if (!outcome.Success || plan.Profile is null) return outcome;
        Thread.Sleep(settleMs);
        var diffs = VerifyNow(plan);
        if (diffs.Count > 0)
        {
            // Windows drops the old lease's default route a few seconds after a static apply: confirm before alarming.
            Thread.Sleep(4000);
            diffs = VerifyNow(plan);
        }
        outcome.VerifyDiffs = diffs;
        if (outcome.VerifyDiffs.Count > 0) Store.Log($"verify '{plan.Title}' on {plan.Adapter}: " + string.Join("; ", outcome.VerifyDiffs));
        return outcome;
    }

    List<string> VerifyNow(ApplyPlan plan)
    {
        var live = GetAdapters().FirstOrDefault(a => a.Name == plan.Adapter);
        if (live is null) return ["adapter disappeared"];
        var p = plan.Profile!;
        var (routes, metric) = !p.Dhcp && (p.Routes.Count > 0 || p.InterfaceMetric is not null) ? RouteReader(live.Index) : (null, null);
        return ApplyVerifier.Compare(p, live, strict: true, routes, metric);
    }

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

    /// <summary>
    /// Borrow an address for a probe (find-routers, auto-switch): a secondary on a static adapter, or a
    /// temporary static replacement on a DHCP adapter (netsh cannot add a secondary next to a lease).
    /// The returned action puts things back (delete the secondary / return to DHCP).
    /// </summary>
    readonly Dictionary<string, Action> _borrowed = new(StringComparer.OrdinalIgnoreCase);

    public bool Borrow(string adapterName, IpAddr addr, bool allowLeaseDrop = false) => Borrow(GetAdapters().FirstOrDefault(a => a.Name == adapterName), addr, allowLeaseDrop);

    /// <summary>
    /// Same, with an adapter snapshot the caller already holds. On a DHCP adapter that HAS a working lease
    /// a borrow means replacing the lease (netsh cannot add a static secondary next to it) — refused unless
    /// the caller explicitly accepted that disruption; a lease-less DHCP adapter (169.254) is fair game.
    /// </summary>
    public bool Borrow(AdapterInfo? live, IpAddr addr, bool allowLeaseDrop = false)
    {
        if (live is null) return false;
        if (live.Dhcp && live.HasRealAddress && !allowLeaseDrop) return false;
        var key = live.Name + "|" + addr;
        lock (_borrowed) { if (_borrowed.ContainsKey(key)) return true; }
        Action restore;
        bool ok;
        if (live.Dhcp)
        {
            var tmp = new Profile { Name = "borrow " + addr, Adapter = live.Name, Addresses = [addr], Temporary = true };
            ok = Apply(ApplyPlanner.Plan(tmp, live)).Success;
            restore = () => Apply(ApplyPlanner.PlanDhcp(live));
        }
        else
        {
            var plan = ApplyPlanner.PlanAddSecondary(live, addr, "borrow");
            if (plan.IsEmpty) return false;
            ok = Apply(plan).Success;
            restore = () => Apply(ApplyPlanner.PlanRemoveAddresses(live.Name, [addr], "return"));
        }
        if (ok) lock (_borrowed) _borrowed[key] = restore;
        return ok;
    }

    /// <summary>Puts a borrowed address back (delete the secondary / return to DHCP). Idempotent.</summary>
    public void Return(string adapterName, IpAddr addr)
    {
        Action? restore;
        lock (_borrowed) _borrowed.Remove(adapterName + "|" + addr, out restore);
        restore?.Invoke();
    }

    /// <summary>The (addTemp, removeTemp) pair RouterFinder and AutoSwitcher take, backed by the ledger; work runs off the caller's thread.</summary>
    public (Func<IpAddr, CancellationToken, Task<bool>> Add, Func<IpAddr, CancellationToken, Task> Remove) Borrower(AdapterInfo live, bool allowLeaseDrop = false, int settleMs = 300) =>
        (async (addr, ct) => { var ok = await Task.Run(() => Borrow(live, addr, allowLeaseDrop), ct); if (ok) await Task.Delay(settleMs, ct); return ok; },
         (addr, _) => Task.Run(() => Return(live.Name, addr)));

    /// <summary>True when a borrow on this adapter would have to replace a working DHCP lease (ask the user first).</summary>
    public static bool BorrowDropsLease(AdapterInfo live) => live.Dhcp && live.HasRealAddress;

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
        var p = new Profile { Name = name, Adapter = adapter.Name, AdapterMac = adapter.Mac, Dhcp = adapter.Dhcp };
        if (!adapter.Dhcp)
        {
            p.Addresses = [.. adapter.Addresses];
            p.Gateway = adapter.Gateways.FirstOrDefault();
            p.Dns = [.. adapter.Dns];
        }
        var v = QueryVlan(adapter.Name);
        if (v.Capable && v.VlanId is > 0) p.VlanId = v.VlanId;
        return p;
    }
}
