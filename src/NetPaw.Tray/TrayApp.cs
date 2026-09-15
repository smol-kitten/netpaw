using System.Diagnostics;
using NetPaw.Adapters;
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
    bool _busy;
    IReadOnlyList<AdapterInfo> _adapters = [];

    public NetPawService Service => _svc;
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
        if (showPanelAtStart) { var once = new System.Windows.Forms.Timer { Interval = 200 }; once.Tick += (_, _) => { once.Dispose(); ShowPanel(); }; once.Start(); }
    }

    // ---- state -----------------------------------------------------------------------------

    public void RefreshState()
    {
        try { _adapters = _svc.GetAdapters(); } catch (Exception ex) { _svc.Store.Log("adapters: " + ex.Message); }
        var w = Work;
        var temps = _svc.TempAddresses.Count;
        var color = _busy ? Theme.Busy : w is null || !w.Up ? Theme.Down : temps > 0 ? Theme.Temp : w.Dhcp ? Theme.Dhcp : Theme.Static;
        _tray.Icon = Icons.Paw(color);
        var current = w is null ? null : _svc.Profiles.FirstOrDefault(p => ApplyPlanner.Matches(p, w));
        var text = w is null ? "NetPaw — no adapter" : $"NetPaw — {w.Name}: {(current?.Name is { } n ? n + " · " : "")}{w.Summary()}";
        _tray.Text = text.Length > 127 ? text[..124] + "…" : text;
        _panel?.RefreshHeader();
    }

    public void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var problems = new List<string>();
        if (HotkeyParser.TryParse(_svc.Settings.PanelHotkey, out var panelKey))
            if (_hotkeys.Register(panelKey, () => ShowPanel()) is { } err) problems.Add(err);
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
        _menu.Items.Add(new ToolStripMenuItem("Manage profiles…", null, (_, _) => ShowEditor()));
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
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
        RunInBackground($"Applying {plan.Title}", custom ?? (() => _svc.Apply(plan)), $"{plan.Title} → {plan.Adapter}");
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

    public void Notify(string title, string text, ToolTipIcon icon)
    {
        _svc.Store.Log($"notify [{icon}] {title}: {text.ReplaceLineEndings(" | ")}");
        if (icon == ToolTipIcon.Info && (!_svc.Settings.ShowNotifications || PanelShowing)) return; // the panel shows it inline
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
        if (disposing) { _tray.Dispose(); _menu.Dispose(); _refresh.Dispose(); _hotkeys.Dispose(); }
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
