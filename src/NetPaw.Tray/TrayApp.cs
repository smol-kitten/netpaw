using System.Diagnostics;
using System.Net.NetworkInformation;
using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Hotkeys;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Reach;
using NetPaw.Scan;

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
    public IProbe Probe { get; } = new NetworkProbe();
    readonly ConnectivityChecker _checker;
    readonly System.Windows.Forms.Timer _checkTimer = new();
    Snapshot? _snapshot, _previous, _announced;
    readonly RenewScheduler _renew = new();
    IReadOnlyList<Advisory> _advisories = [];
    IReadOnlyList<VpnInfo> _vpns = [];
    readonly Dictionary<string, IReadOnlyList<Discovery.SwitchNeighbor>> _switches = new(StringComparer.OrdinalIgnoreCase);
    bool _discovering;
    public IReadOnlyList<Discovery.SwitchNeighbor> SwitchNeighbors(string adapter) => _switches.TryGetValue(adapter, out var l) ? l : [];
    public void SetSwitchNeighbors(string adapter, IReadOnlyList<Discovery.SwitchNeighbor> list) { _switches[adapter] = list; _switchSeen[adapter] = DateTime.UtcNow; _toast?.Render(_snapshot); }
    readonly Dictionary<string, DateTime> _switchSeen = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Switch neighbour usable as a fingerprint signal: seen on this adapter within the last 24 h (older captures may predate a re-patch).</summary>
    Discovery.SwitchNeighbor? RecentSwitch(string adapter) =>
        _switchSeen.TryGetValue(adapter, out var t) && DateTime.UtcNow - t < TimeSpan.FromHours(24) ? SwitchNeighbors(adapter).FirstOrDefault() : null;
    readonly Arp.WindowsArpProvider _arp = new();
    bool _autoSwitchArmed = true, _autoSwitching;
    Profile? _autoSwitchUndo;
    public IReadOnlyList<VpnInfo> Vpns => _vpns;
    IReadOnlyList<string> _loggedAdvisories = [];
    DateTime? _advisoriesSince;
    IncidentLog? _incidents;
    NetState? _pendingState; DateTime _pendingSince;
    readonly System.Windows.Forms.Timer _confirm = new() { Interval = 3000 };
    bool _checking;
    bool _busy;
    bool _menuOpen;
    readonly Queue<(string Title, string Text, ToolTipIcon Icon)> _deferred = new();
    string? _lastStatus;
    IReadOnlyList<AdapterInfo> _adapters = [];

    public NetPawService Service => _svc;
    public Snapshot? LastSnapshot => _snapshot;
    public IReadOnlyList<Advisory> Advisories => _advisories;
    /// <summary>Snapshots of the other host-facing adapters (gateway ping only) and their findings; the work adapter stays the target of repairs and renews.</summary>
    public IReadOnlyList<Snapshot> Secondaries { get; private set; } = [];
    public IReadOnlyList<Advisory> SecondaryAdvisories { get; private set; } = [];
    public string RenewInfo => _svc.Settings.Repair.AutoRenew
        ? $"Renew lease now  (auto-renew {_renew.Attempts}/{_svc.Settings.Repair.AutoRenewMaxAttempts} this incident)"
        : "Renew lease now  (ipconfig /renew)";
    /// <summary>(text, kind) kind: "busy" | "ok" | "warn" | "error". The quick panel shows it inline instead of a balloon.</summary>
    public event Action<string, string>? Status;
    bool PanelShowing => _panel is { Visible: true };
    public AdapterInfo? Work => _svc.WorkAdapter(_adapters);

    public TrayApp(NetPawService svc, bool showPanelAtStart)
    {
        _checker = new ConnectivityChecker(Probe);
        _svc = svc;
        _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.Base, ShowImageMargin = true, ShowCheckMargin = false };
        _menu.Opening += (_, _) => { _menuOpen = true; BuildMenu(); };
        _menu.Closed += (_, _) => { _menuOpen = false; FlushDeferred(); };
        _tray = new NotifyIcon { Icon = Icons.Paw(Theme.Down), Text = "NetPaw", Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        _tray.BalloonTipClicked += (_, _) => { if (_balloonAction is { } act) { _balloonAction = null; act(); } else OpenLog(); };
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
        AskUpdateOptIn();
        _ = CheckForUpdates();
        var leftovers = _svc.TempAddresses;
        if (leftovers.Count > 0)
            Notify("Temporary addresses from a previous session", $"{leftovers.Count} address{(leftovers.Count == 1 ? "" : "es")} NetPaw added earlier {(leftovers.Count == 1 ? "is" : "are")} still tracked ({string.Join(", ", leftovers.Select(t => t.Address.ToString()))}). Remove them from the panel or tray menu if you are done.", ToolTipIcon.Warning, force: true);
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
            _advisories = _svc.Settings.Repair.DhcpAdvisory ? Advisor.Analyze(snap, _adapters) : [];
            var secondaries = AdapterSelector.Secondaries(_adapters, work);
            Secondaries = secondaries.Count == 0 ? [] : await Task.Run(() => _checker.CheckSecondaries(secondaries, _svc.Settings.Checks));
            // Secondary findings are shown, never auto-repaired: the renew scheduler and the incident log stay on the work adapter.
            SecondaryAdvisories = _svc.Settings.Repair.DhcpAdvisory ? Secondaries.SelectMany(x => Advisor.Analyze(x, null)).Where(x => x.Severity != AdvisorySeverity.Info).ToList() : [];
            // Auto-switch: one decision per link-up (armed again when the link drops), never while busy.
            var linkUp = snap.Link && snap.Adapter is not null && (_previous is null || !_previous.Link || _previous.Adapter?.Name != snap.Adapter.Name);
            if (!snap.Link) { _autoSwitchArmed = true; if (snap.Adapter is not null) _switches.Remove(snap.Adapter.Name); }
            if (linkUp && _svc.Settings.Repair.DiscoverSwitchOnLinkUp && !_discovering && Discovery.PktmonCapture.Available && _svc.Allowed(Capability.Capture) && snap.Adapter is not null)
                _ = DiscoverSwitch(snap.Adapter.Name);
            if (linkUp && _autoSwitchArmed && !_busy && !_autoSwitching && _svc.Allowed(Capability.AutoSwitch) && _svc.Profiles.Any(p => p.AutoSwitch && p.Fingerprint is not null))
            {
                _autoSwitchArmed = false;
                _ = AutoSwitch(snap.Adapter!);
            }
            var vpns = VpnDetector.Detect(_adapters);
            foreach (var msg in VpnDetector.Changes(_vpns, vpns))
            {
                _svc.Store.Log("vpn: " + msg);
                if (_svc.Settings.Repair.VpnNotifications && _previous is not null) Notify("VPN", msg, ToolTipIcon.Info, force: true);
                if (_svc.Settings.Repair.IncidentLog) (_incidents ??= new IncidentLog(_svc.Store.IncidentsFile)).Append(new Incident(DateTimeOffset.Now, msg.Contains('\'') ? msg.Split('\'')[1] : "vpn", "vpn", "", "", msg, []));
            }
            _vpns = vpns;
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
                        if (IncidentLog.FromChange(snap, null, _advisories, _loggedAdvisories) is { } cfg) { _incidents.Append(cfg); TelemetryHost.Export(_svc, cfg.Kind == "config" ? "warn" : "info", cfg.Text, new() { ["adapter"] = cfg.Adapter, ["kind"] = cfg.Kind, ["state"] = cfg.To, ["findings"] = string.Join("; ", cfg.Advisories) }, isIncident: true); }
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
                        TelemetryHost.Export(_svc, change.IsProblem ? "warn" : "info", change.Title + ": " + change.Text, new() { ["adapter"] = snap.Adapter?.Name, ["from"] = change.From.ToString(), ["to"] = change.To.ToString(), ["kind"] = change.IsProblem ? "problem" : "recovery" }, isIncident: true);
                        Notify(change.Title, change.Text, change.IsProblem ? ToolTipIcon.Warning : ToolTipIcon.Info, force: true);
                    }
                }
            }
            if (_svc.Settings.Checks.StickyAlerts && !_menuOpen)
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
        if (!_svc.Allowed(Capability.Scan)) { Denied(Capability.Scan); return; }
        if (_busy) { Notify("Busy", "Another change is still running.", ToolTipIcon.Warning); return; }
        using var f = new ScanForm(this, w);
        f.ShowDialog();
        RefreshState(); _ = RunCheck();
    }

    AutoSwitcher MakeSwitcher(AdapterInfo live)
    {
        var (add, remove) = _svc.Borrower(live);
        return new AutoSwitcher(_arp, add, remove)
        {
            Switch = RecentSwitch,
            Wlan = (a, ct) => Task.Run(() => Wlan.Read(_svc.Runner, a.Name), ct),
        };
    }

    async Task AutoSwitch(AdapterInfo adapter)
    {
        _autoSwitching = true;
        try
        {
            // Give DHCP a moment; a lease makes identification a single ARP.
            await Task.Delay(4000);
            if (_busy) { _svc.Store.Log("auto-switch skipped: a manual change is running"); return; }
            var live = _svc.GetAdapters().FirstOrDefault(a => a.Name == adapter.Name); if (live is null || !live.Up) return;
            _busy = true; RefreshState(); // deciding may borrow addresses: hold the gate so a click cannot interleave
            AutoSwitcher.Decision d;
            try { d = await MakeSwitcher(live).Decide(live, _svc.Profiles, _svc.Settings.AutoSwitchDeclinedIds); }
            finally { _busy = false; RefreshState(); }
            _svc.Store.Log($"auto-switch on {live.Name}: {d.Reason}");
            if (d.Profile is null) return;
            var p = d.Profile;
            if (!_svc.AutoSwitchConfirmed(p))
            {
                var ok = MessageBox.Show($"NetPaw recognised this network ({d.Observed}) as profile '{p.Name}'.\n\nApply it automatically now and every time this network is detected on {live.Name}?\n\n(You can turn auto-switch off per profile in the editor.)",
                    "NetPaw — auto-switch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                if (!ok) { _svc.DeclineAutoSwitch(p); Notify("Auto-switch off", $"'{p.Name}' will not switch automatically on this machine.", ToolTipIcon.Info); return; }
                _svc.ConfirmAutoSwitch(p);
            }
            _autoSwitchUndo = _svc.Capture("before auto-switch", live);
            if (_svc.Settings.Repair.IncidentLog) (_incidents ??= new IncidentLog(_svc.Store.IncidentsFile)).Append(new Incident(DateTimeOffset.Now, live.Name, "auto-switch", "", p.Name, d.Reason, []));
            TelemetryHost.Event("apply", "auto-switch", p.Dhcp ? "dhcp" : "static");
            ApplyProfileVerified(p, live);
            Status?.Invoke($"Auto-switched to '{p.Name}' ({d.Reason}). Undo: tray menu.", "ok");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { _svc.Store.Log("auto-switch failed: " + ex.Message); }
        finally { _autoSwitching = false; }
    }

    async Task DiscoverSwitch(string adapter)
    {
        _discovering = true;
        try
        {
            var mac = _adapters.FirstOrDefault(a => a.Name == adapter)?.Mac;
            var (found, err) = await Task.Run(() => new Discovery.PktmonCapture().Listen(adapter, mac, Math.Clamp(_svc.Settings.Repair.DiscoverSeconds, 10, 120), null, _svc.Store.Log));
            if (err is not null) { _svc.Store.Log("switch discovery: " + err); return; }
            SetSwitchNeighbors(adapter, found);
            if (found.Count > 0)
            {
                Status?.Invoke($"Connected to {found[0].Headline}", "ok");
                if (_svc.Settings.Repair.IncidentLog) (_incidents ??= new IncidentLog(_svc.Store.IncidentsFile)).Append(new Incident(DateTimeOffset.Now, adapter, "switch", "", found[0].Protocol, found[0].Headline, []));
            }
        }
        finally { _discovering = false; }
    }

    /// <summary>First start: one explicit yes/no for the daily update check. Nothing leaves the machine before the answer; policy-denied installs are never asked.</summary>
    void AskUpdateOptIn()
    {
        var s = _svc.Settings;
        if (s.UpdateCheckAsked) return;
        if (!_svc.Allowed(Capability.UpdateCheck)) { s.UpdateCheckAsked = true; s.UpdateCheck = false; _svc.SaveSettings(); return; }
        using var f = new Form
        {
            Text = "NetPaw — updates", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = true, ClientSize = new Size(460, 150), TopMost = true,
        };
        var text = new Label
        {
            Text = "May NetPaw ask GitHub once a day whether a newer release exists?\n\nOne request to api.github.com (version number only, nothing about you or your networks). " +
                   "A newer release shows one balloon; nothing is ever downloaded or installed. Change it any time in Settings → Updates.",
            Location = new Point(16, 14), Size = new Size(428, 86), Font = Theme.Base,
        };
        var yes = new Button { Text = "Yes, check daily", DialogResult = DialogResult.Yes, Location = new Point(216, 108), Size = new Size(130, 30) };
        var no = new Button { Text = "No", DialogResult = DialogResult.No, Location = new Point(354, 108), Size = new Size(90, 30) };
        f.Controls.Add(text); f.Controls.Add(yes); f.Controls.Add(no); f.AcceptButton = yes; f.CancelButton = no;
        Theme.Apply(f); Theme.Primary(yes);
        f.Shown += (_, _) => Native.ForceForeground(f.Handle);
        var answer = f.ShowDialog();
        s.UpdateCheck = answer == DialogResult.Yes; s.UpdateCheckAsked = true; _svc.SaveSettings();
        TelemetryHost.Event("updates", s.UpdateCheck ? "opt-in" : "opt-out");
    }

    /// <summary>What a click on the current balloon does (default: open the log). Set right before Notify.</summary>
    Action? _balloonAction;
    static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Daily, opt-in, policy-gated: one balloon per newer release, click opens the release page. Never downloads.</summary>
    public async Task CheckForUpdates(bool manual = false)
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            var checker = new Updates.UpdateChecker(async (url, ct) =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd($"NetPaw/{version}"); req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await UpdateHttp.SendAsync(req, ct); resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }, version);
            var s = _svc.Settings;
            if (manual) { s.UpdateLastCheck = null; s.UpdateLastVersionSeen = null; }
            var info = await Task.Run(() => checker.Run(s, _svc.Allowed(Capability.UpdateCheck), DateTimeOffset.Now));
            _svc.SaveSettings();
            if (info is null) { if (manual) Notify("NetPaw is up to date", $"Version {version} is the latest release.", ToolTipIcon.Info, force: true); return; }
            _balloonAction = () => Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
            Notify($"NetPaw {info.Version} is available", (info.Headline is null ? "" : info.Headline + " — ") + "click to open the release page.", ToolTipIcon.Info, force: true);
        }
        catch (Exception ex) { _svc.Store.Log("update check: " + ex.Message); if (manual) Notify("Update check failed", ex.Message, ToolTipIcon.Warning, force: true); }
    }

    public void UndoAutoSwitch()
    {
        if (_autoSwitchUndo is null) return;
        var p = _autoSwitchUndo; _autoSwitchUndo = null; _autoSwitchArmed = false;
        Execute(_svc.PlanProfile(p, _svc.AdapterFor(p, _adapters)), subtitle: "Undo the last automatic switch.");
    }

    /// <summary>Editor helper: learn the fingerprint of whatever the adapter runs now.</summary>
    public async Task<NetworkFingerprint?> LearnFingerprint(AdapterInfo adapter)
    {
        var live = _svc.GetAdapters().FirstOrDefault(a => a.Name == adapter.Name) ?? adapter;
        return await MakeSwitcher(live).Learn(live);
    }

    public void ShowMap()
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        using var f = new NetworkMapForm(this, w);
        f.ShowDialog();
        RefreshState();
    }

    public void ShowInfo()
    {
        if (_menuOpen) return;
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
        var color = _busy ? Theme.Busy : w is null || !w.Up || w.VSwitchUplink ? Theme.Down : temps > 0 ? Theme.Temp : w.Dhcp ? Theme.Dhcp : Theme.Static;
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
        if (_snapshot is { ChecksEnabled: true } || _lastStatus is not null)
        {
            var line = _snapshot is { ChecksEnabled: true } ? Snapshot.Describe(_snapshot.State) + (_lastStatus is null ? "" : "  ·  " + _lastStatus) : _lastStatus!;
            _menu.Items.Add(new ToolStripMenuItem(line.Length > 90 ? line[..87] + "…" : line) { Enabled = false, Tag = _snapshot is not null && Snapshot.IsProblem(_snapshot.State) ? Theme.Error : Theme.Muted, Font = Theme.Small, ToolTipText = _lastStatus });
        }
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
        if (w is not null)
            _menu.Items.Add(new ToolStripMenuItem("Reset adapter (disable → enable)", null, (_, _) => ResetAdapter()));
        if (_autoSwitchUndo is not null)
            _menu.Items.Add(new ToolStripMenuItem("Undo last auto-switch", Icons.Dot(Theme.Temp), (_, _) => UndoAutoSwitch()));
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
        foreach (var a in _adapters.Where(a => a.HostFacing || a.Up))
        {
            var ad = a;
            var it = new ToolStripMenuItem($"{a.Name}  ·  {a.Summary()}") { Checked = w?.Name == a.Name, Image = Icons.Dot(a.VSwitchUplink ? Theme.Muted : a.Up ? Theme.Static : Theme.Down) };
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
        if (_svc.Allowed(Capability.Scan))
            _menu.Items.Add(new ToolStripMenuItem("Scan for a working profile…", null, (_, _) => ShowScan()));
        _menu.Items.Add(new ToolStripMenuItem("Network map (ARP / find routers)…", null, (_, _) => ShowMap()));
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
        try { ApplyProfileVerified(p, _svc.AdapterFor(p, _adapters)); }
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

    /// <summary>One TCP connect, verdict in the status line and a balloon. No address change — reach mode's read-only cousin.</summary>
    public async Task CheckPort(string host, int port)
    {
        Status?.Invoke($"Checking {host}:{port}…", "busy");
        var r = await PortCheck.Run(Probe, host, port, 2000);
        _svc.Store.Log($"check {host}:{port}: {r.Verdict} {r.Ms} ms");
        Status?.Invoke(r.Text, r.Open ? "ok" : "warn");
        Notify(r.Open ? "Port open" : "Port check", r.Text, r.Open ? ToolTipIcon.Info : ToolTipIcon.Warning, force: true);
        TelemetryHost.Event("check", "port", r.Verdict.ToString());
    }

    public void Reach(string target, bool? replace = null)
    {
        var w = Work; if (w is null) { Notify("No adapter", "Pick a work adapter first.", ToolTipIcon.Warning); return; }
        if (!_svc.Allowed(Capability.Reach)) { Denied(Capability.Reach); return; }
        var d = _svc.ResolveReach(target, w);
        if (d.Kind == ReachKind.PortCheck && PortCheck.TryParse(d.Target, out var h, out var p)) { _ = CheckPort(h, p); return; }
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

    public IReadOnlyList<string> LastVerifyDiffs { get; private set; } = [];
    public string? LastVerifyAdapter { get; private set; }

    public void ApplyProfileVerified(Profile p, AdapterInfo adapter) => Execute(_svc.PlanProfile(p, adapter));

    /// <summary>Default work for <see cref="Execute"/>: apply through the service's verify path. A mismatch (the documented
    /// "still DHCP / two gateways" state, a route Windows refused, a metric it ignored) becomes a warning with a Reset action on that adapter.</summary>
    ApplyOutcome ApplyVerified(ApplyPlan plan)
    {
        LastVerifyDiffs = []; LastVerifyAdapter = null;
        var o = _svc.ApplyAndVerify(plan);
        if (o.VerifyDiffs.Count > 0)
        {
            LastVerifyDiffs = o.VerifyDiffs; LastVerifyAdapter = plan.Adapter;
            var text = $"Windows did not fully take '{plan.Title}' on {plan.Adapter}: {string.Join("; ", o.VerifyDiffs)}. Network info → Reset adapter usually clears it.";
            _hotkeys.BeginInvoke(() => { Status?.Invoke(text, "warn"); Notify("Apply not fully effective", text, ToolTipIcon.Warning, force: true); });
            TelemetryHost.Event("apply", "verify-mismatch", plan.Profile?.Dhcp == true ? "dhcp" : "static", o.VerifyDiffs.Count);
        }
        return o;
    }

    /// <summary>Repair actions from advisories: Release/Reset on the named adapter, Prefer the work adapter.</summary>
    public void Repair(Advisory adv)
    {
        var target = _adapters.FirstOrDefault(a => a.Name == adv.RepairAdapter); if (target is null) return;
        var plan = adv.Repair switch
        {
            RepairKind.Renew => Advisor.PlanRenew(target),
            RepairKind.Release => Advisor.PlanRelease(target),
            RepairKind.Reset => ApplyPlanner.PlanResetAdapter(target),
            RepairKind.Prefer => Advisor.PlanPrefer(target, _adapters),
            _ => null,
        };
        if (plan is null) return;
        TelemetryHost.Event("repair", adv.Repair.ToString().ToLowerInvariant(), adv.Title);
        Execute(plan, subtitle: adv.Title + " — " + adv.Text);
    }

    /// <summary>Reset the named adapter, else the one the last verify complained about, else the work adapter.</summary>
    public void ResetAdapter(string? adapterName = null)
    {
        var name = adapterName ?? LastVerifyAdapter ?? Work?.Name; if (name is null) return;
        var target = _adapters.FirstOrDefault(a => a.Name == name) ?? Work; if (target is null) return;
        Execute(ApplyPlanner.PlanResetAdapter(target), subtitle: "Disable → enable. The documented cure for 'static applied but Windows still shows DHCP / two gateways'.");
    }

    /// <summary>Runs a plan on a worker thread (netsh can take a couple of seconds), optionally after a preview.</summary>
    public void Execute(ApplyPlan plan, Func<ApplyOutcome>? custom = null, string? subtitle = null, Action? after = null)
    {
        if (_busy) { Notify("Busy", "Another change is still running.", ToolTipIcon.Warning); return; }
        if (plan.IsEmpty)
        {
            var text = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : "Nothing to do.";
            Status?.Invoke(text, "warn"); Notify(plan.Title, text, ToolTipIcon.Info);
            return;
        }
        // Shift while choosing = show the exact netsh lines first, even when "Confirm" is off.
        if (_svc.Settings.ConfirmBeforeApply || plan.Warnings.Count > 0 || (Control.ModifierKeys & Keys.Shift) == Keys.Shift)
            if (!PlanPreviewForm.Confirm(plan, subtitle)) { Status?.Invoke("Cancelled.", "warn"); return; }
        var kind = plan.Title == "DHCP" ? "dhcp" : plan.Title.StartsWith("reach ") ? "reach" : subtitle is not null ? "preset" : "profile";
        RunInBackground($"Applying {plan.Title}", () => { ApplyOutcome? o = null; try { o = (custom ?? (() => ApplyVerified(plan)))(); return o; } finally { TelemetryHost.Apply(kind, plan, o); TelemetryHost.Export(_svc, o is { Success: true } ? "info" : "error", $"apply {kind} '{plan.Title}' on {plan.Adapter}: {(o is null ? "crashed" : o.Success ? "ok" : "failed")}", new() { ["adapter"] = plan.Adapter, ["kind"] = kind, ["profile"] = plan.Title, ["steps"] = plan.Steps.Count, ["result"] = o is null ? "crashed" : o.Success ? "ok" : "failed" }, isIncident: false); } }, $"{plan.Title} → {plan.Adapter}", after);
    }

    void RunInBackground(string title, Func<ApplyOutcome> work, string doneText, Action? after = null)
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
                after?.Invoke();
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
        _lastStatus = $"{title}: {text.ReplaceLineEndings(" ")}";
        if (!force && icon == ToolTipIcon.Info && (!_svc.Settings.ShowNotifications || PanelShowing)) return; // the panel shows it inline
        // A balloon popping while the context menu is open steals the click and closes the menu: defer it.
        if (_menuOpen) { _deferred.Enqueue((title, text, icon)); if (_deferred.Count > 3) _deferred.Dequeue(); return; }
        _tray.ShowBalloonTip(icon == ToolTipIcon.Error ? 8000 : 3000, title, text.Length > 250 ? text[..247] + "…" : text, icon);
    }

    void FlushDeferred()
    {
        // Only the newest deferred message is worth showing after the menu closes.
        if (_deferred.Count == 0) return;
        var (title, text, icon) = _deferred.Last(); _deferred.Clear();
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
