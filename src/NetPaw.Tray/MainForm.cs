using System.Diagnostics;
using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;

namespace NetPaw.Tray;

/// <summary>
/// The optional main window: everything the tray menu reaches, in one resizable dark window with a left
/// navigation. Closing hides it (the tray stays). The tray, quick panel, hotkeys and info card are unchanged.
/// </summary>
sealed class MainForm : Form
{
    public static readonly string[] Pages = ["Overview", "Profiles", "Tools", "Map", "Log"];
    static readonly string[] HelpTopics = ["network-info", "profiles", "main-window", "network-info", "main-window"];

    readonly TrayApp _app;
    readonly ListBox _nav = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36, IntegralHeight = false, Font = Theme.Base };
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 4, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Dictionary<string, Control> _pages = [];
    bool _closingForReal;

    public MainForm(TrayApp app)
    {
        _app = app;
        Text = "NetPaw"; MinimumSize = new Size(640, 400); StartPosition = FormStartPosition.Manual; KeyPreview = true; DoubleBuffered = true;
        Icon = Icons.Paw(Theme.Static);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 180, IsSplitterFixed = true, SplitterWidth = 1 };
        split.Panel1.BackColor = Theme.Panel; split.Panel2.BackColor = Theme.Bg;
        _nav.BackColor = Theme.Panel; _nav.ForeColor = Theme.Text;
        _nav.Items.AddRange(Pages);
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? Theme.Selection : Theme.Panel); e.Graphics.FillRectangle(bg, e.Bounds);
            if (selected) { using var accent = new SolidBrush(Theme.Accent); e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height); }
            TextRenderer.DrawText(e.Graphics, Pages[e.Index], selected ? new Font(Theme.Base, FontStyle.Bold) : Theme.Base, Rectangle.Inflate(e.Bounds, -14, 0), selected ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _nav.SelectedIndexChanged += (_, _) => ShowPage(Pages[Math.Max(0, _nav.SelectedIndex)]);
        var brand = new Label { Text = "🐾 NetPaw", Dock = DockStyle.Top, Height = 44, Padding = new Padding(14, 12, 0, 0), Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Text };
        split.Panel1.Controls.Add(_nav); split.Panel1.Controls.Add(brand);
        split.Panel2.Controls.Add(_host);
        Controls.Add(split); Controls.Add(_status);
        Theme.Apply(this);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            if (e.KeyCode == Keys.F1) { _app.ShowHelp(HelpTopics[Math.Max(0, _nav.SelectedIndex)]); e.Handled = true; }
        };
        _app.Status += (text, _) => { if (IsHandleCreated) BeginInvoke(() => _status.Text = text); };
        FormClosing += (_, e) => { SaveState(); if (!_closingForReal) { e.Cancel = true; Hide(); } };
        Load += (_, _) => { Native.Dress(this); RestoreState(); };
    }

    /// <summary>Show (or bring back) the window on a page; the size and page survive hide/show and restarts.</summary>
    public void Open(string? page = null)
    {
        page ??= _app.Service.Settings.MainWindow.LastPage;
        if (!Visible) Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _nav.SelectedIndex = Math.Max(0, Array.IndexOf(Pages, page));
        if (_nav.SelectedIndex == 0 && !_pages.ContainsKey("Overview")) ShowPage("Overview");
        Native.ForceForeground(Handle);
    }

    /// <summary>The tray calls this after every state change; only a visible window re-renders.</summary>
    public void OnStateChanged() { if (Visible && _host.Controls.Count > 0 && _host.Controls[0] is IPage p) p.Refresh(); }

    public void ExitApp() { _closingForReal = true; Close(); }

    void ShowPage(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Profiles" => new ProfilesPage(_app),
                "Tools" => new ToolsPage(_app, this),
                "Map" => new MapPage(_app, this),
                "Log" => new LogPage(_app),
                _ => new OverviewPage(_app),
            };
            page.Dock = DockStyle.Fill; Theme.Apply(page); _pages[name] = page;
        }
        _host.SuspendLayout(); _host.Controls.Clear(); _host.Controls.Add(page); _host.ResumeLayout();
        if (page is IPage p) p.Refresh();
        _app.Service.Settings.MainWindow.LastPage = name;
    }

    public void ShowMapTab(int tab) { Open("Map"); if (_pages.TryGetValue("Map", out var m) && m is MapPage mp) mp.SelectTab(tab); }

    void RestoreState()
    {
        var st = _app.Service.Settings.MainWindow;
        var area = Screen.FromPoint(st.HasPosition ? new Point(st.X, st.Y) : Cursor.Position).WorkingArea;
        var c = st.ClampTo(area.X, area.Y, area.Width, area.Height);
        Bounds = new Rectangle(c.X, c.Y, c.Width, c.Height);
        if (c.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveState()
    {
        var st = _app.Service.Settings.MainWindow;
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        st.X = r.X; st.Y = r.Y; st.Width = r.Width; st.Height = r.Height; st.Maximized = WindowState == FormWindowState.Maximized;
        _app.Service.SaveSettings();
    }

    interface IPage { void Refresh(); }

    static Label Heading(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Text, Padding = new Padding(0, 4, 0, 0) };
    static Label Hint(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(0, 0, 0, 6), MaximumSize = new Size(700, 0) };
    static Button Btn(string text, Action click, int width = 150) { var b = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 8, 8) }; b.Click += (_, _) => click(); return b; }

    // ---- Overview: the info card and the actions an admin reaches for first ----------------------------
    sealed class OverviewPage : UserControl, IPage
    {
        readonly TrayApp _app; readonly InfoCardPanel _card; readonly Label _title = Heading(""), _state = Hint("");
        readonly TextBox _reach = new() { Width = 220, PlaceholderText = "IP, IP/prefix or host:port" };
        public OverviewPage(TrayApp app)
        {
            _app = app; _card = new InfoCardPanel(app) { Dock = DockStyle.Top, TextWidth = 560, BackColor = Theme.Bg };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            actions.Controls.Add(Btn("DHCP now", app.ApplyDhcp, 110)); actions.Controls.Add(Btn("Renew lease", () => app.Renew(manual: true), 110));
            actions.Controls.Add(Btn("Reset adapter", () => app.ResetAdapter(), 120)); actions.Controls.Add(Btn("Re-check now", () => _ = app.RunCheck(), 120));
            actions.Controls.Add(_reach); actions.Controls.Add(Btn("Reach", () => { if (_reach.Text.Trim().Length > 0) app.Reach(_reach.Text.Trim()); }, 80));
            _reach.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _reach.Text.Trim().Length > 0) { app.Reach(_reach.Text.Trim()); e.SuppressKeyPress = true; } };
            Controls.Add(actions); Controls.Add(_card); Controls.Add(_state); Controls.Add(_title);
        }
        public new void Refresh() { _card.Render(_app.LastSnapshot); _title.Text = _card.TitleText; _state.Text = _card.StateText; _state.ForeColor = _card.StateColor; }
    }

    // ---- Profiles: the list with apply/edit, the editor stays the place for details ---------------------
    sealed class ProfilesPage : UserControl, IPage
    {
        readonly TrayApp _app; readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Dictionary<ListViewItem, Profile> _rows = [];
        public ProfilesPage(TrayApp app)
        {
            _app = app;
            foreach (var (h, w) in new[] { ("Profile", 200), ("Configuration", 300), ("Adapter", 130), ("Hotkey", 90), ("Status", 90) }) _list.Columns.Add(h, w);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            bar.Controls.Add(Btn("Apply", ApplySelected, 100)); bar.Controls.Add(Btn("Edit…", () => app.ShowEditor(), 100)); bar.Controls.Add(Btn("New…", () => app.ShowEditor(), 100));
            bar.Controls.Add(Btn("Capture current…", app.CaptureCurrent, 150)); bar.Controls.Add(Btn("Scan for a working profile…", app.ShowScan, 200));
            _list.DoubleClick += (_, _) => ApplySelected();
            _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplySelected(); e.Handled = true; } };
            Controls.Add(_list); Controls.Add(bar); Controls.Add(Hint("Double-click applies. Details, routes, VLAN and auto-switch live in the editor.")); Controls.Add(Heading("Profiles"));
        }
        void ApplySelected() { if (_list.SelectedItems.Count == 1 && _rows.TryGetValue(_list.SelectedItems[0], out var p)) { try { _app.ApplyProfileVerified(p, _app.Service.AdapterFor(p, _app.Service.GetAdapters())); } catch (Exception ex) { _app.Notify("Apply", ex.Message, ToolTipIcon.Warning, force: true); } } }
        public new void Refresh()
        {
            var w = _app.Work; var selected = _list.SelectedItems.Count == 1 && _rows.TryGetValue(_list.SelectedItems[0], out var sp) ? sp.Id : null;
            _list.BeginUpdate(); _list.Items.Clear(); _rows.Clear();
            foreach (var p in _app.Service.Profiles.Where(p => !p.Temporary))
            {
                var current = w is not null && ApplyPlanner.Matches(p, w);
                var item = new ListViewItem([p.Name, p.Summary(), p.Adapter ?? "(work adapter)", p.Hotkey ?? "", current ? "current" : p.Managed ? "managed" : p.Source ?? ""]) { ForeColor = current ? Theme.Static : p.Dhcp ? Theme.Dhcp : Theme.Text };
                _rows[item] = p; _list.Items.Add(item); if (p.Id == selected) item.Selected = true;
            }
            _list.EndUpdate();
        }
    }

    // ---- Tools: one row per tool, results for trace/port check inline --------------------------------
    sealed class ToolsPage : UserControl, IPage
    {
        readonly TextBox _out = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Cascadia Mono", 9f), BorderStyle = BorderStyle.None };
        CancellationTokenSource? _cts;
        public ToolsPage(TrayApp app, MainForm main)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 6, 0, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string button, Action click, string hint, Control? input = null)
            {
                grid.Controls.Add(Btn(button, click, 180));
                grid.Controls.Add(input ?? new Label { Text = "", AutoSize = true });
                grid.Controls.Add(Hint(hint));
            }
            var traceHost = new TextBox { Width = 220, Text = "1.1.1.1" };
            var port = new TextBox { Width = 220, PlaceholderText = "host:port" };
            var mac = new TextBox { Width = 220, PlaceholderText = "aa:bb:cc:dd:ee:ff" };
            Row("Which switch port am I on?", () => main.ShowMapTab(2), "LLDP/CDP via pktmon: switch name, port, VLAN. Listens 35 s; nothing is sent.");
            Row("Route table", () => main.ShowMapTab(3), "Live IPv4 routes with interface names; delete a stale manual route.");
            Row("Find routers", () => main.ShowMapTab(1), "Borrows a temporary address per common subnet and ARPs the usual gateway addresses.");
            Row("Trace route", () => Trace(app, traceHost.Text.Trim()), "Where does the path stop? Three ICMP probes per hop, results below.", traceHost);
            Row("Check a TCP port", () => Check(app, port.Text.Trim()), "One connect: open / refused / timeout. Nothing is scanned.", port);
            Row("Wake-on-LAN", () => { if (WakeOnLan.TryParseMac(mac.Text, out _)) _ = app.Wake(mac.Text.Trim(), app.Work?.RealAddress); else Say($"'{mac.Text}' is not a MAC address"); }, "Magic packet to a MAC on this segment (global + subnet broadcast).", mac);
            Row("Scan for a working profile", app.ShowScan, "Tries DHCP and your profiles on this port, keeps the one that works.");
            Row("Export diagnostics", app.ExportDiagnostics, "One zip for the helpdesk: ipconfig, routes, arp, netsh, log, incidents, redacted settings.");
            _out.BackColor = Theme.Panel; _out.ForeColor = Theme.Text;
            Controls.Add(_out); Controls.Add(grid); Controls.Add(Heading("Tools"));
        }
        void Say(string line) { if (InvokeRequired) { BeginInvoke(() => Say(line)); return; } _out.AppendText(line + Environment.NewLine); }
        async void Trace(TrayApp app, string host)
        {
            if (host.Length == 0) return;
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
            _out.Clear(); Say($"trace to {host}");
            try
            {
                var r = await Connectivity.Trace.Run(app.Probe, host, progress: new Progress<Hop>(h => Say($"{h.Ttl,3}  {h.Address ?? "*",-18} {h.MsText}")), ct: ct);
                Say(r.Summary);
            }
            catch (OperationCanceledException) { Say("cancelled"); }
            catch (Exception ex) { Say("failed: " + ex.Message); }
        }
        async void Check(TrayApp app, string target)
        {
            if (!PortCheck.TryParse(target, out var h, out var p)) { Say($"'{target}' is not host:port"); return; }
            _out.Clear(); Say($"connecting to {h}:{p}…");
            try { var r = await PortCheck.Run(app.Probe, h, p, 2000); Say(r.Text); } catch (Exception ex) { Say("failed: " + ex.Message); }
        }
        public new void Refresh() { }
    }

    // ---- Map: the network map panel, rebuilt when the work adapter changes -------------------------------
    sealed class MapPage : UserControl, IPage
    {
        readonly TrayApp _app; readonly MainForm _main; NetworkMapPanel? _panel; string? _adapterName;
        public MapPage(TrayApp app, MainForm main) { _app = app; _main = main; }
        public new void Refresh()
        {
            var w = _app.Work;
            if (w is null) { Controls.Clear(); Controls.Add(Hint("Pick a work adapter first (tray menu → Work adapter).")); _panel = null; _adapterName = null; return; }
            if (_panel is not null && _adapterName == w.Name) return;
            Controls.Clear(); _panel?.Dispose();
            _panel = new NetworkMapPanel(_app, w) { Dock = DockStyle.Fill }; _adapterName = w.Name;
            _panel.CloseRequested += () => _main.Open("Overview");
            Controls.Add(_panel); Theme.Apply(_panel);
        }
        public void SelectTab(int tab) { Refresh(); _panel?.SelectTab(tab); }
    }

    // ---- Log: the last lines of netpaw.log and the incident log ---------------------------------------
    sealed class LogPage : UserControl, IPage
    {
        readonly TrayApp _app; readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Cascadia Mono", 9f), BorderStyle = BorderStyle.None };
        readonly Theme.DarkComboBox _which = new() { Width = 160 };
        public LogPage(TrayApp app)
        {
            _app = app; _which.Items.AddRange(["netpaw.log", "incidents.jsonl"]); _which.SelectedIndex = 0; _which.SelectedIndexChanged += (_, _) => Refresh();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            bar.Controls.Add(_which); bar.Controls.Add(Btn("Refresh", Refresh, 90)); bar.Controls.Add(Btn("Open config folder", () => Process.Start(new ProcessStartInfo(app.Service.Store.Directory) { UseShellExecute = true }), 150));
            _log.BackColor = Theme.Panel; _log.ForeColor = Theme.Text;
            Controls.Add(_log); Controls.Add(bar); Controls.Add(Heading("Log"));
        }
        public new void Refresh()
        {
            var file = _which.SelectedIndex == 1 ? _app.Service.Store.IncidentsFile : _app.Service.Store.LogFile;
            try { var lines = File.Exists(file) ? File.ReadAllLines(file) : []; _log.Text = string.Join(Environment.NewLine, lines.TakeLast(200)); _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
            catch (IOException ex) { _log.Text = ex.Message; }
        }
    }
}
