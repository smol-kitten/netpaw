using System.Diagnostics;
using System.Net.NetworkInformation;
using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Hotkeys;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Reach;

namespace NetPaw.Tray;

/// <summary>Owns the tray icon, menu, hotkeys, the quick panel and the "apply" pipeline with notifications.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NetPawService _svc;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu;
    readonly HotkeyManager _hotkeys = new();
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 15_000 };
    QuickPanel? _panel;
    ProfileEditorForm? _editor;
    InfoToast? _toast;
    HelpForm? _help;
    readonly ConnectivityChecker _checker = new(new NetworkProbe());
    readonly System.Windows.Forms.Timer _checkTimer = new();
    Snapshot? _snapshot, _previous, _announced;
    readonly RenewScheduler _renew = new();
    IReadOnlyList<Advisory> _advisories = [];
    IReadOnlyList<string> _loggedAdvisories = [];
    DateTime? _advisoriesSince;
    IncidentLog? _incidents;
    NetState? _pendingState; DateTime _pendingSince;
    readonly System.Windows.Forms.Timer _confirm = new() { Interval = 3000 };
    bool _checking;
    bool _busy;
    IReadOnlyList<AdapterInfo> _adapters = [];

    public NetPawService Service => _svc;
    public Snapshot? LastSnapshot => _snapshot;
    public IReadOnlyList<Advisory> Advisories => _advisories;
    public string RenewInfo => _svc.Settings.Repair.AutoRenew
        ? $"Renew lease now  (auto-renew {_renew.Attempts}/{_svc.Settings.Repair.AutoRenewMaxAttempts} this incident)"
        : "Renew lease now  (ipconfig /renew)";
    /// <summary>(text, kind) kind: "busy" | "ok" | "warn" | "error". The quick panel shows it inline instead of a balloon.</summary>
    public event Action<string, string>? Status;
    bool PanelShowing => _panel is { Visible: true };
    public AdapterInfo? Work => _svc.WorkAdapter(_adapters);

    public TrayApp(NetPawService svc, bool showPanelAtStart)
    {
        _svc = svc;
        _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.Base, ShowImageMargin = true, ShowCheckMargin = false };
        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { Icon = Icons.Paw(Theme.Down), Text = "NetPaw", Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        _tray.BalloonTipClicked += (_, _) => OpenLog();
        _refresh.Tick += (_, _) => RefreshState();
        _refresh.Start();
        _hotkeys.ShowRequested += () => ShowPanel();
        RegisterHotkeys();
        RefreshState();
        _checkTimer.Tick += (_, _) => _ = RunCheck();
        _confirm.Tick += (_, _) => { _confirm.Stop(); _ = RunCheck(); };
        ConfigureChecks();
        // Instant reaction to cable pulls / address changes, on top of the periodic probe.
        NetworkChange.NetworkAvailabilityChanged += (_, _) => OnNetworkEvent();
        NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkEvent();
        _ = RunCheck();
        if (TelemetryHost.IsTelemetryBuild && !_svc.Settings.TelemetryNoticeShown)
        {
            _svc.Settings.TelemetryNoticeShown = true; _svc.SaveSettings();
            Notify("Telemetry build", "This build sends crash reports and anonymous usage counts to telemetry.catboy.systems (no addresses, no names). Settings → Telemetry turns it off.", ToolTipIcon.Info, force: true);
        }
        if (showPanelAtStart) { var once = new System.Windows.Forms.Timer { Interval = 200 }; once.Tick += (_, _) => { once.Dispose(); ShowPanel(); }; once.Start(); }
    }

    // ---- connectivity ----------------------------------------------------------------------

    public void ConfigureChecks()
    {
        var c = _svc.Settings.Checks;
        _checkTimer.Stop();
        if (c.Enabled) { _checkTimer.Interval = Math.Clamp(c.IntervalSeconds, 5, 3600) * 1000; _checkTimer.Start(); }
    }

    /// <summary>NetworkChange fires on a worker thread, in bursts; hop to the UI thread and let RunCheck coalesce.</summary>
    void OnNetworkEvent()
    {
        if (!_hotkeys.IsHandleCreated) return;
        try { _hotkeys.BeginInvoke(() => _ = RunCheck()); } catch (InvalidOperationException) { }
    }

    /// <summary>One probe round on a worker thread; updates icon and toast, and (monitor mode) notifies on transitions.</summary>
    public async Task RunCheck()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            try { _adapters = _svc.GetAdapters(); } catch (Exception ex) { _svc.Store.Log("adapters: " + ex.Message); }
            var work = Work;
            var snap = await Task.Run(() => _checker.Check(work, _svc.Settings.Checks));
            _previous = _snapshot; _snapshot = snap;
            _advisories = _svc.Settings.Repair.DhcpAdvisory ? Advisor.Analyze(snap) : [];
            RefreshState();
            if (_svc.Settings.Repair.IncidentLog)
            {
                // Findings are logged once they held for 10 s (DHCP negotiation is not an "invalid config"); clearing is logged at once.
                var titles = _advisories.Select(a => a.Title).ToList();
                if (titles.SequenceEqual(_loggedAdvisories)) _advisoriesSince = null;
                else
                {
                    _advisoriesSince ??= DateTime.UtcNow;
                    if (titles.Count == 0 || DateTime.UtcNow - _advisoriesSince >= TimeSpan.FromSeconds(10))
                    {
                        _incidents ??= new IncidentLog(_svc.Store.IncidentsFile);
                        if (IncidentLog.FromChange(snap, null, _advisories, _loggedAdvisories) is { } cfg) _incidents.Append(cfg);
                        _loggedAdvisories = titles; _advisoriesSince = null;
                    }
                    else { _confirm.Stop(); _confirm.Interval = 10_100; _confirm.Start(); }
                }
            }
            _toast?.Render(snap);
            // Optional self-repair: a bounded ipconfig /renew when an advisory says a fresh lease could help.
            if (!_busy && _renew.ShouldRenew(_svc.Settings.Repair, _advisories, DateTimeOffset.Now) && work is { Dhcp: true })
                Renew(manual: false);
            // Announce against the last *announced* state, and only once the new state has held for its
            // grace period — a reconnect passes through "no address" while DHCP negotiates, which is noise.
            var change = Transitions.Detect(_announced, snap);
            if (change is null) { _pendingState = null; _announced ??= snap; }
            else
            {
                var grace = snap.State == NetState.NoAddress ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
                if (_pendingState != snap.State) { _pendingState = snap.State; _pendingSince = DateTime.UtcNow; }
                var held = DateTime.UtcNow - _pendingSince;
                if (held < grace) { _confirm.Stop(); _confirm.Interval = (int)(grace - held).TotalMilliseconds + 100; _confirm.Start(); }
                else
                {
                    _announced = snap; _pendingState = null;
                    if (_svc.Settings.Repair.IncidentLog) (_incidents ??= new IncidentLog(_svc.Store.IncidentsFile)).Append(IncidentLog.FromChange(snap, change, _advisories, [])!);
                    // Link-down/up is always announced (it needs no probes); the finer states only in monitor mode.
                    var linkEvent = change.To == NetState.LinkDown || change.From == NetState.LinkDown;
                    if (_svc.Settings.Checks.MonitorMode || linkEvent)
                    {
                        _svc.Store.Log($"monitor: {change.From} -> {change.To}: {change.Text}");
                        TelemetryHost.Event("monitor", change.To.ToString(), change.From.ToString());
                        Notify(change.Title, change.Text, change.IsProblem ? ToolTipIcon.Warning : ToolTipIcon.Info, force: true);
                    }
                }
            }
            if (_svc.Settings.Checks.StickyAlerts)
            {
                var problem = Snapshot.IsProblem(snap.State);
                if (problem || _toast is { Visible: true }) { _toast ??= new InfoToast(this); _toast.AutoPin(problem); }
            }
        }
        finally { _checking = false; }
    }

    /// <summary>ipconfig /renew on the work adapter; automatic runs count against the per-incident limit.</summary>
    public void Renew(bool manual)
    {
        var w = Work; if (w is null || !w.Dhcp) return;
        var plan = Advisor.PlanRenew(w);
        if (!manual) { _renew.Mark(DateTimeOffset.Now); _svc.Store.Log($"auto-renew attempt {_renew.Attempts}/{_svc.Settings.Repair.AutoRenewMaxAttempts} on {w.Name}: {string.Join("; ", _advisories.Where(a => a.CanRenew).Select(a => a.Title))}"); }
        TelemetryHost.Event("repair", manual ? "renew-manual" : "renew-auto", _advisories.FirstOrDefault(a => a.CanRenew)?.Title);
        RunInBackground($"Renewing DHCP lease on {w.Name}", () => _svc.Apply(plan), $"Renewed lease on {w.Name}");
        // Re-probe once the lease had time to land.
        var later = new System.Windows.Forms.Timer { Interval = 8000 }; later.Tick += (_, _) => { later.Dispose(); _ = RunCheck(); }; later.Start();
    }

    public void ShowScan()
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        if (_busy) { Notify("Busy", "Another change is still running.", ToolTipIcon.Warning); return; }
        using var f = new ScanForm(this, w);
        f.ShowDialog();
        RefreshState(); _ = RunCheck();
    }

    public void ShowInfo()
    {
        _toast ??= new InfoToast(this);
        _toast.Present();
        _ = RunCheck();
    }

    public void ShowHelp(string topic)
    {
        if (_help is null || _help.IsDisposed) _help = new HelpForm();
        _help.Show(); _help.Activate(); Native.ForceForeground(_help.Handle);
        _help.Open(topic);
    }

    // ---- state -----------------------------------------------------------------------------

    public void RefreshState()
    {
        try { _adapters = _svc.GetAdapters(); } catch (Exception ex) { _svc.Store.Log("adapters: " + ex.Message); }
        var w = Work;
        var temps = _svc.TempAddresses.Count;
        var color = _busy ? Theme.Busy : w is null || !w.Up ? Theme.Down : temps > 0 ? Theme.Temp : w.Dhcp ? Theme.Dhcp : Theme.Static;
        var problem = _snapshot is not null && Snapshot.IsProblem(_snapshot.State);
        _tray.Icon = Icons.Paw(color, problem ? Theme.Error : null);
        var current = w is null ? null : _svc.Profiles.FirstOrDefault(p => ApplyPlanner.Matches(p, w));
        var text = w is null ? "NetPaw — no adapter" : $"NetPaw — {w.Name}: {(current?.Name is { } n ? n + " · " : "")}{w.Summary()}{(_snapshot is { ChecksEnabled: true } ? " · " + Snapshot.Describe(_snapshot.State) : "")}";
        _tray.Text = text.Length > 127 ? text[..124] + "…" : text;
        _panel?.RefreshHeader();
    }

    public void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var problems = new List<string>();
        if (HotkeyParser.TryParse(_svc.Settings.PanelHotkey, out var panelKey))
            if (_hotkeys.Register(panelKey, () => ShowPanel()) is { } err) problems.Add(err);
        if (HotkeyParser.TryParse(_svc.Settings.InfoHotkey, out var infoKey))
            if (_hotkeys.Register(infoKey, ShowInfo) is { } err2) problems.Add(err2);
        foreach (var p in _svc.Profiles.Where(p => p.Hotkey is not null))
        {
            if (!HotkeyParser.TryParse(p.Hotkey, out var k)) { problems.Add($"'{p.Name}': bad hotkey {p.Hotkey}"); continue; }
            var profile = p;
            if (_hotkeys.Register(k, () => ApplyProfile(profile)) is { } err) problems.Add($"'{p.Name}': {err}");
        }
        if (problems.Count > 0) Notify("Hotkeys not registered", string.Join("\n", problems), ToolTipIcon.Warning);
    }

    // ---- menu ------------------------------------------------------------------------------

    void BuildMenu()
    {
        _menu.Items.Clear();
        var w = Work;
        var header = new ToolStripMenuItem(w is null ? "No adapter" : $"{w.Name}  ·  {w.Summary()}") { Enabled = false, Tag = Theme.Muted, Font = Theme.Small };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        var profiles = _svc.Profiles.Where(p => !p.Temporary).ToList();
        if (profiles.Count == 0) _menu.Items.Add(new ToolStripMenuItem("No profiles yet — Manage profiles…") { Enabled = false });
        foreach (var p in profiles)
        {
            var item = new ToolStripMenuItem(p.Name) { ShortcutKeyDisplayString = p.Hotkey, Image = Icons.Dot(p.Dhcp ? Theme.Dhcp : Theme.Static) };
            item.ToolTipText = p.Summary();
            if (w is not null && ApplyPlanner.Matches(p, w)) item.Checked = true;
            item.Click += (_, _) => ApplyProfile(p);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        if (_svc.Allowed(Capability.Dhcp))
            _menu.Items.Add(new ToolStripMenuItem("DHCP now", Icons.Dot(Theme.Dhcp), (_, _) => ApplyDhcp()) { Checked = w?.Dhcp == true });
        if (w is { Dhcp: true })
            _menu.Items.Add(new ToolStripMenuItem("Renew DHCP lease", null, (_, _) => Renew(manual: true)));
        if (_svc.Allowed(Capability.Reach))
            _menu.Items.Add(new ToolStripMenuItem("Reach IP…", null, (_, _) => ShowPanel()) { ShortcutKeyDisplayString = _svc.Settings.PanelHotkey });

        var presets = new ToolStripMenuItem("Device presets") { Visible = _svc.Allowed(Capability.Reach) && _svc.Allowed(Capability.TempAddresses) };
        foreach (var vendor in _svc.Presets.GroupBy(p => p.Vendor).OrderBy(g => g.Key))
        {
            var vm = new ToolStripMenuItem(vendor.Key);
            foreach (var preset in vendor)
            {
                var pr = preset;
                vm.DropDownItems.Add(new ToolStripMenuItem($"{pr.Model}  ({pr.Ip})", null, (_, _) => ApplyPreset(pr)) { ToolTipText = pr.Note });
            }
            presets.DropDownItems.Add(vm);
        }
        foreach (ToolStripDropDownItem d in presets.DropDownItems) d.DropDown.Renderer = _menu.Renderer;
        presets.DropDown.Renderer = _menu.Renderer;
        _menu.Items.Add(presets);

        var temps = _svc.TempAddresses;
        if (temps.Count > 0)
            _menu.Items.Add(new ToolStripMenuItem($"Remove {temps.Count} temporary address{(temps.Count == 1 ? "" : "es")}", Icons.Dot(Theme.Temp), (_, _) => ClearTemp()));

        _menu.Items.Add(new ToolStripSeparator());
        var adapters = new ToolStripMenuItem("Work adapter");
        foreach (var a in _adapters.Where(a => a.IsPhysical || a.Up))
        {
            var ad = a;
            var it = new ToolStripMenuItem($"{a.Name}  ·  {a.Summary()}") { Checked = w?.Name == a.Name, Image = Icons.Dot(a.Up ? Theme.Static : Theme.Down) };
            it.Click += (_, _) => { _svc.Settings.WorkAdapter = ad.Name; _svc.SaveSettings(); RefreshState(); };
            adapters.DropDownItems.Add(it);
        }
        adapters.DropDownItems.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("Auto-detect") { Checked = _svc.Settings.WorkAdapter is null };
        auto.Click += (_, _) => { _svc.Settings.WorkAdapter = null; _svc.SaveSettings(); RefreshState(); };
        adapters.DropDownItems.Add(auto);
        adapters.DropDown.Renderer = _menu.Renderer;
        _menu.Items.Add(adapters);
        if (_svc.Allowed(Capability.UserProfiles))
            _menu.Items.Add(new ToolStripMenuItem("Capture current as profile…", null, (_, _) => CaptureCurrent()));
        _menu.Items.Add(new ToolStripMenuItem("Network info", null, (_, _) => ShowInfo()) { ShortcutKeyDisplayString = _svc.Settings.InfoHotkey });
        _menu.Items.Add(new ToolStripMenuItem("Scan for a working profile…", null, (_, _) => ShowScan()));
        if (_svc.Settings.Repair.IncidentLog)
            _menu.Items.Add(new ToolStripMenuItem("Open incident log", null, (_, _) => { if (File.Exists(_svc.Store.IncidentsFile)) Process.Start(new ProcessStartInfo(_svc.Store.IncidentsFile) { UseShellExecute = true }); }));
        _menu.Items.Add(new ToolStripMenuItem("Manage profiles…", null, (_, _) => ShowEditor()));
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        _menu.Items.Add(new ToolStripMenuItem("Help", null, (_, _) => ShowHelp("overview")) { ShortcutKeyDisplayString = "F1" });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Quit()));
    }

    // ---- actions ---------------------------------------------------------------------------

    public void ApplyProfile(Profile p)
    {
        try { Execute(_svc.PlanProfile(p, _svc.ResolveAdapter(p.Adapter, _adapters))); }
        catch (InvalidOperationException ex) { Status?.Invoke(ex.Message, "error"); Notify("Cannot apply " + p.Name, ex.Message, ToolTipIcon.Error); }
    }

    public void ApplyDhcp()
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        if (!_svc.Allowed(Capability.Dhcp)) { Denied(Capability.Dhcp); return; }
        Execute(ApplyPlanner.PlanDhcp(w));
    }

    void Denied(Capability c) { var msg = new DeniedByPolicyException(c).Message; Status?.Invoke(msg, "error"); Notify("Not allowed", msg, ToolTipIcon.Warning); }

    public void ApplyPreset(Presets.Preset preset)
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        var d = _svc.ResolvePreset(preset, w);
        if (d.Kind == ReachKind.AlreadyReachable) { Status?.Invoke(d.Explanation, "ok"); Notify("Already reachable", d.Explanation, ToolTipIcon.Info); return; }
        var (plan, _) = _svc.Reach(d, w, dryRun: true);
        Execute(plan, () => _svc.Reach(d, w).Outcome!, d.Explanation);
    }

    public void Reach(string target, bool? replace = null)
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        if (!_svc.Allowed(Capability.Reach)) { Denied(Capability.Reach); return; }
        var d = _svc.ResolveReach(target, w);
        if (d.Kind is ReachKind.TempAddress or ReachKind.UsePreset && !_svc.Allowed(Capability.TempAddresses)) { Denied(Capability.TempAddresses); return; }
        switch (d.Kind)
        {
            case ReachKind.Invalid: Status?.Invoke(d.Explanation, "error"); Notify("Reach", d.Explanation, ToolTipIcon.Warning); return;
            case ReachKind.AlreadyReachable: Status?.Invoke(d.Explanation, "ok"); Notify("Already reachable", d.Explanation, ToolTipIcon.Info); return;
        }
        var (plan, _) = _svc.Reach(d, w, dryRun: true, replace);
        Execute(plan, () =>
        {
            // Re-run through the service so temp addresses get tracked; plan is identical.
            var (_, outcome) = _svc.Reach(d, w, dryRun: false, replace);
            return outcome!;
        }, d.Explanation);
    }

    public void ClearTemp()
    {
        RunInBackground("Removing temporary addresses", () => _svc.ClearTemp() ?? new ApplyOutcome(), "Temporary addresses removed");
    }

    void CaptureCurrent()
    {
        var w = Work; if (w is null) return;
        if (!_svc.Allowed(Capability.UserProfiles)) { Denied(Capability.UserProfiles); return; }
        var name = Prompt.Ask("Capture current configuration", $"Save {w.Name} ({w.Summary()}) as profile:", w.Dhcp ? "DHCP" : w.Primary?.Network ?? "profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = _svc.Capture(name.Trim(), w);
        _svc.Profiles.Add(p); _svc.SaveProfiles();
        Notify("Profile saved", $"{p.Name}: {p.Summary()}", ToolTipIcon.Info);
    }

    /// <summary>Runs a plan on a worker thread (netsh can take a couple of seconds), optionally after a preview.</summary>
    public void Execute(ApplyPlan plan, Func<ApplyOutcome>? custom = null, string? subtitle = null)
    {
        if (_busy) { Notify("Busy", "Another change is still running.", ToolTipIcon.Warning); return; }
        if (plan.IsEmpty)
        {
            var text = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : "Nothing to do.";
            Status?.Invoke(text, "warn"); Notify(plan.Title, text, ToolTipIcon.Info);
            return;
        }
        if (_svc.Settings.ConfirmBeforeApply || plan.Warnings.Count > 0)
            if (!PlanPreviewForm.Confirm(plan, subtitle)) { Status?.Invoke("Cancelled.", "warn"); return; }
        var kind = plan.Title == "DHCP" ? "dhcp" : plan.Title.StartsWith("reach ") ? "reach" : subtitle is not null ? "preset" : "profile";
        RunInBackground($"Applying {plan.Title}", () => { ApplyOutcome? o = null; try { o = (custom ?? (() => _svc.Apply(plan)))(); return o; } finally { TelemetryHost.Apply(kind, plan, o); } }, $"{plan.Title} → {plan.Adapter}");
    }

    void RunInBackground(string title, Func<ApplyOutcome> work, string doneText)
    {
        _busy = true; RefreshState();
        Status?.Invoke(title + "…", "busy");
        Task.Run(work).ContinueWith(t =>
        {
            _busy = false;
            var outcome = t.IsFaulted ? null : t.Result;
            if (outcome is null)
            {
                _svc.Store.Log("apply crashed: " + t.Exception);
                if (t.Exception is not null) TelemetryHost.Error(t.Exception.GetBaseException(), "apply");
                var msg = t.Exception?.GetBaseException().Message ?? "unknown";
                Status?.Invoke(msg, "error"); Notify("NetPaw error", msg, ToolTipIcon.Error);
            }
            else if (outcome.Success)
            {
                var soft = outcome.Failures.ToList();
                var text = soft.Count == 0 ? "Done." : "Done, with warnings: " + string.Join("; ", soft.Select(f => f.Step.Description + ": " + f.Output));
                Status?.Invoke(doneText + " — " + text, soft.Count == 0 ? "ok" : "warn");
                Notify(doneText, text, soft.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            else
            {
                var text = string.Join("; ", outcome.Failures.Select(f => f.Step.Description + ": " + f.Output));
                Status?.Invoke(title + " failed — " + text, "error");
                Notify(title + " failed", text, ToolTipIcon.Error);
            }
            RefreshState();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Notify(string title, string text, ToolTipIcon icon, bool force = false)
    {
        _svc.Store.Log($"notify [{icon}] {title}: {text.ReplaceLineEndings(" | ")}");
        if (!force && icon == ToolTipIcon.Info && (!_svc.Settings.ShowNotifications || PanelShowing)) return; // the panel shows it inline
        _tray.ShowBalloonTip(icon == ToolTipIcon.Error ? 8000 : 3000, title, text.Length > 250 ? text[..247] + "…" : text, icon);
    }

    // ---- windows ---------------------------------------------------------------------------

    public void ShowPanel()
    {
        _panel ??= new QuickPanel(this);
        _panel.Present();
    }

    void TogglePanel() { if (_panel is { Visible: true }) _panel.Hide(); else ShowPanel(); }

    public void ShowEditor(Profile? select = null)
    {
        if (_editor is null || _editor.IsDisposed) { _editor = new ProfileEditorForm(this); _editor.FormClosed += (_, _) => { _editor = null; RegisterHotkeys(); RefreshState(); }; }
        if (_editor.WindowState == FormWindowState.Minimized) _editor.WindowState = FormWindowState.Normal;
        _editor.Show(); _editor.Activate(); Native.ForceForeground(_editor.Handle);
        if (select is not null && select.Source is not null && !select.Managed && !_svc.Profiles.Contains(select) && _svc.Allowed(Capability.UserProfiles))
        {
            // A repo profile: editing means "make my own copy".
            var copy = select.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Source = null;
            _svc.Profiles.Add(copy); _svc.SaveProfiles(); select = copy;
        }
        if (select is not null) _editor.Select(select);
    }

    public void ShowSettings() { using var f = new SettingsForm(this); Native.ForceForeground(f.Handle); f.ShowDialog(); }

    void OpenLog()
    {
        if (File.Exists(_svc.Store.LogFile)) Process.Start(new ProcessStartInfo(_svc.Store.LogFile) { UseShellExecute = true });
    }

    void Quit()
    {
        _tray.Visible = false;
        _hotkeys.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _menu.Dispose(); _refresh.Dispose(); _checkTimer.Dispose(); _confirm.Dispose(); _hotkeys.Dispose(); _toast?.Dispose(); _help?.Dispose(); }
        base.Dispose(disposing);
    }
}

static class Prompt
{
    public static string? Ask(string title, string label, string initial)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(420, 120), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var l = new Label { Text = label, AutoSize = false, Left = 14, Top = 12, Width = 392, Height = 34 };
        var t = new TextBox { Left = 14, Top = 50, Width = 392, Text = initial };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 300, Top = 84, Width = 106 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 190, Top = 84, Width = 106 };
        f.Controls.AddRange([l, t, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        Theme.Apply(f); Theme.Primary(ok); Native.Dress(f);
        t.SelectAll();
        return f.ShowDialog() == DialogResult.OK ? t.Text : null;
    }
}
