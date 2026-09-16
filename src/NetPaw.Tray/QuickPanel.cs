using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Planning;
using NetPaw.Presets;
using NetPaw.Reach;

namespace NetPaw.Tray;

/// <summary>
/// Launcher-style popup: type a profile name, a vendor, or an IP. Enter does the obvious thing.
/// Kept as one borderless form with an owner-drawn list so it opens instantly.
/// </summary>
sealed class QuickPanel : Form
{
    sealed record Item(string Title, string Subtitle, Color Color, Action Run, string? Hint = null, Profile? Profile = null, bool OpensWindow = false, string? Badge = null, Color? BadgeColor = null);

    readonly TrayApp _app;
    readonly Label _header = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(14, 8, 14, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly TextBox _input = new() { Dock = DockStyle.Top, Font = Theme.Big, BorderStyle = BorderStyle.None, Margin = Padding.Empty, PlaceholderText = "profile, device, or IP…" };
    readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 6, 14, 0), ForeColor = Theme.Muted, Font = Theme.Small, Text = "Enter apply    Ctrl+D DHCP    Ctrl+E edit    Ctrl+I info    Ctrl+R replace    F1 help    Esc" };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 0, Padding = new Padding(14, 5, 14, 0), Font = Theme.Small, AutoEllipsis = true, BackColor = Theme.Panel };
    readonly System.Windows.Forms.Timer _autoHide = new() { Interval = 1400 };
    readonly ToolTip _tip = new();
    readonly List<Item> _items = [];
    string? _linkText;

    public QuickPanel(TrayApp app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(520, 420); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Base; DoubleBuffered = true;
        var inputHost = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 10, 14, 6), BackColor = Theme.Panel };
        _input.BackColor = Theme.Panel; _input.ForeColor = Theme.Text;
        inputHost.Controls.Add(_input);
        _list.BackColor = Theme.Bg; _list.ForeColor = Theme.Text;
        Controls.Add(_list); Controls.Add(_status); Controls.Add(_footer); Controls.Add(inputHost); Controls.Add(_header);
        _app.Status += (text, kind) => { if (IsHandleCreated) BeginInvoke(() => ShowStatus(text, kind)); };
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); if (Visible) Hide(); };
        _header.MouseEnter += (_, _) => { if (_linkText is not null) _tip.SetToolTip(_header, _linkText); };
        _input.TextChanged += (_, _) => Rebuild();
        _input.KeyDown += OnKey;
        _list.DrawItem += DrawItem;
        _list.MouseDoubleClick += (_, _) => RunSelected();
        _list.KeyDown += OnKey;
        Deactivate += (_, _) => Hide();
        Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.Dress(this, round: true);
        ApplyRegion();
    }

    void ApplyRegion()
    {
        if (!IsHandleCreated) return;
        try { Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12)); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    protected override bool ShowWithoutActivation => false;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */; return cp; } }

    public void Present()
    {
        _app.RefreshState();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 5);
        _input.Text = "";
        ClearStatus();
        Rebuild();
        Show(); Activate(); Native.ForceForeground(Handle);
        _input.Focus();
    }

    public void RefreshHeader()
    {
        var w = _app.Work;
        var temps = _app.Service.TempAddresses.Count;
        var link = w is null ? "" : w.Up ? "●" : "○";
        _header.ForeColor = w is null || !w.Up ? Theme.Down : Theme.Muted;
        _header.Text = w is null ? "no adapter" : $"{link}  {w.Name}  ·  {w.Summary()}{(w.SpeedText.Length > 0 ? "  ·  " + w.SpeedText : "")}{(temps > 0 ? $"  ·  {temps} temp" : "")}";
        _linkText = w is null ? null : $"{w.Description}\nMAC {w.Mac}\nDNS {(w.Dns.Count == 0 ? "—" : string.Join(", ", w.Dns))}\n{(w.Up ? "link up" : "link down")}";
    }

    void ShowStatus(string text, string kind)
    {
        _autoHide.Stop();
        _status.Text = text;
        _status.ForeColor = kind switch { "ok" => Theme.Static, "warn" => Theme.Temp, "error" => Theme.Error, _ => Theme.Accent };
        _status.Height = 26;
        FitHeight();
        if (kind == "ok" && Visible) _autoHide.Start();
    }

    void ClearStatus() { _autoHide.Stop(); _status.Text = ""; _status.Height = 0; }

    void Rebuild()
    {
        _items.Clear();
        var q = _input.Text.Trim();
        var svc = _app.Service; var w = _app.Work;
        var looksLikeIp = q.Length > 0 && char.IsAsciiDigit(q[0]) && q.Contains('.');
        var isPortCheck = PortCheck.TryParse(q, out var pcHost, out var pcPort);

        if (isPortCheck)
            _items.Add(new Item($"Check TCP port {pcPort} on {pcHost}", "One connect: open / refused / timeout. Nothing is changed.", Theme.Accent, () => _ = _app.CheckPort(pcHost, pcPort), "check"));
        else if (looksLikeIp && !svc.Allowed(Capability.Reach))
            _items.Add(new Item("Reach mode is disabled by policy", "Your organisation manages this setting.", Theme.Error, () => { }));
        else if (looksLikeIp)
        {
            if (IpMath.TryParseCidr(q, out _, svc.Settings.ReachDefaultPrefix))
            {
                var d = svc.ResolveReach(q, w);
                var (title, color) = d.Kind switch
                {
                    ReachKind.AlreadyReachable => ($"Already reachable: {d.Target}", Theme.Static),
                    ReachKind.UseProfile => ($"Reach {d.Target} via profile '{d.Profile!.Name}'", Theme.Static),
                    ReachKind.UsePreset => ($"Reach {d.Target} — {d.Preset!.Vendor} {d.Preset.Model}", Theme.Temp),
                    ReachKind.TempAddress => ($"Reach {d.Target} — add temporary {d.Address}", Theme.Temp),
                    _ => (d.Explanation, Theme.Error),
                };
                _items.Add(new Item(title, d.Explanation, color, () => _app.Reach(q), "reach"));
                if (d.Kind is ReachKind.TempAddress or ReachKind.UsePreset)
                    _items.Add(new Item($"Reach {d.Target} — replace primary with {d.Address}", "Full switch: gateway/DNS of the current config are dropped (Ctrl+R).", Theme.Temp, () => _app.Reach(q, replace: true), "reach-replace"));
            }
            else _items.Add(new Item($"'{q}' is not a valid IPv4 address", "e.g. 192.168.88.1 or 10.0.0.1/16", Theme.Error, () => { }));
        }
        else
        {
            var profiles = svc.Profiles.Where(p => !p.Temporary && (q.Length == 0 || Fuzzy(p.Name, q) || p.Summary().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var p in profiles)
            {
                var current = w is not null && ApplyPlanner.Matches(p, w);
                var badge = current ? "current" : p.Managed ? "managed" : p.Source;
                var badgeColor = current ? Theme.Static : p.Managed ? Theme.Temp : Theme.Accent;
                _items.Add(new Item(p.Name, p.Summary() + (p.Hotkey is null ? "" : "   " + p.Hotkey), p.Dhcp ? Theme.Dhcp : Theme.Static, () => _app.ApplyProfile(p), null, p, Badge: badge, BadgeColor: badgeColor));
            }
            if ((q.Length == 0 || Word(q, "dhcp")) && svc.Allowed(Capability.Dhcp))
                _items.Add(new Item("DHCP now", $"Let {w?.Name ?? "the adapter"} take a lease", Theme.Dhcp, _app.ApplyDhcp, Badge: w?.Dhcp == true ? "current" : null, BadgeColor: Theme.Static));
            if (q.Length > 0)
                foreach (var rp in svc.RepoProfiles.Where(p => Fuzzy(p.Name, q) || p.Summary().Contains(q, StringComparison.OrdinalIgnoreCase)).Take(6))
                    _items.Add(new Item(rp.Name, rp.Summary() + (rp.Note is null ? "" : "   " + rp.Note), rp.Dhcp ? Theme.Dhcp : Theme.Static, () => _app.ApplyProfile(rp), null, rp, Badge: rp.Source, BadgeColor: Theme.Accent));
            if (q.Length > 0 && svc.Allowed(Capability.Reach) && svc.Allowed(Capability.TempAddresses))
                foreach (var pr in PresetLibrary.Search(svc.Presets, q).Take(8))
                    _items.Add(new Item($"{pr.Vendor} {pr.Model}", $"{pr.Ip}/{pr.Prefix}{(pr.Note is null ? "" : "   " + pr.Note)}", Theme.Temp, () => _app.ApplyPreset(pr), Badge: pr.Source, BadgeColor: Theme.Accent));
            var temps = svc.TempAddresses;
            if (temps.Count > 0 && (q.Length == 0 || Word(q, "temporary", "clear", "remove")))
                _items.Add(new Item($"Remove {temps.Count} temporary address{(temps.Count == 1 ? "" : "es")}", string.Join(", ", temps.Select(t => t.Address.ToString())), Theme.Temp, _app.ClearTemp));
            if (q.Length == 0 || Word(q, "manage profiles", "edit", "profiles"))
                _items.Add(new Item("Manage profiles…", "Create, edit, hotkeys, routes, VLAN", Theme.Muted, () => _app.ShowEditor(), OpensWindow: true));
            if (w is { Dhcp: true } && (q.Length == 0 || Word(q, "renew", "lease")))
                _items.Add(new Item("Renew DHCP lease", $"ipconfig /renew on {w.Name}" + (_app.Advisories.FirstOrDefault(a => a.CanRenew) is { } adv ? "   ·   " + adv.Title : ""), Theme.Dhcp, () => _app.Renew(manual: true)));
            if (Word(q, "arp", "map", "network map", "neighbours"))
                _items.Add(new Item("Network map…", "ARP neighbours per network, active re-check/sweep, find routers on this cable", Theme.Accent, _app.ShowMap, OpensWindow: true));
            if (Word(q, "switch", "lldp", "cdp", "port"))
                _items.Add(new Item("Which switch port am I on? (LLDP/CDP)…", "Listens 35 s for the switch's own announcements via pktmon — name, port, VLAN", Theme.Accent, () => _app.ShowMap(2), OpensWindow: true));
            if (Word(q, "find router", "router", "gateway"))
                _items.Add(new Item("Find routers on this cable…", "Borrows a temporary address per common subnet and ARPs the usual gateway addresses", Theme.Accent, () => _app.ShowMap(1), OpensWindow: true));
            if (Word(q, "route", "routes", "route table"))
                _items.Add(new Item("Route table…", "Live IPv4 routes with interface names; delete a stale manual route", Theme.Accent, () => _app.ShowMap(3), OpensWindow: true));
            if (Word(q, "trace", "traceroute", "path", "hops"))
                _items.Add(new Item("Trace route…", "Where does the path stop? Three ICMP probes per hop", Theme.Accent, _app.AskTrace, OpensWindow: true));
            if (Word(q, "check", "port", "open"))
                _items.Add(new Item("Check a TCP port…", "One connect: open / refused / timeout — or just type host:port here", Theme.Accent, _app.AskCheckPort, OpensWindow: true));
            if (Word(q, "wake", "wol", "magic"))
                _items.Add(new Item("Wake-on-LAN…", "Magic packet to a MAC on this segment", Theme.Accent, _app.AskWake, OpensWindow: true));
            if (Word(q, "diag", "diagnostics", "export", "support", "zip"))
                _items.Add(new Item("Export diagnostics…", "One zip for the helpdesk: ipconfig, routes, arp, netsh, log, incidents", Theme.Accent, _app.ExportDiagnostics, OpensWindow: true));
            if (Word(q, "mtu", "wifi", "wi-fi", "802.1x", "dns", "stuck", "intent"))
                _items.Add(new Item("Network info", "Wi-Fi signal, 802.1X, path MTU, per-server DNS and 'X cannot reach Y' advice live on the info card", Theme.Muted, _app.ShowInfo, OpensWindow: true));
            if (Word(q, "scan", "find working"))
                _items.Add(new Item("Scan for a working profile…", "Try DHCP and your profiles on this port, keep the one that works", Theme.Accent, _app.ShowScan, OpensWindow: true));
            if (q.Length == 0 || Word(q, "network info", "status", "info"))
                _items.Add(new Item("Network info", "Link, address, gateway, DNS, intranet/internet — pin it with 📌", Theme.Muted, _app.ShowInfo, OpensWindow: true));
            if (Word(q, "help"))
                _items.Add(new Item("Help  (F1)", "Topics for every view", Theme.Muted, () => _app.ShowHelp("overview"), OpensWindow: true));
            if (Word(q, "settings", "options"))
                _items.Add(new Item("Settings…", "Work adapter, hotkeys, info card rows, checks, repair, log level", Theme.Muted, _app.ShowSettings, OpensWindow: true));
            if (q.Length == 0)
                _items.Add(new Item("Tools…", "Switch port (LLDP), route table, trace, port check, wake, diagnostics — type any of them", Theme.Muted, () => _app.ShowMap(2), OpensWindow: true));
        }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in _items) _list.Items.Add(i);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.EndUpdate();
        FitHeight();
    }

    /// <summary>Grow with content (3..8 rows) instead of leaving a dark void under two items.</summary>
    void FitHeight()
    {
        var rows = Math.Clamp(_items.Count, 3, 8);
        var wanted = _header.Height + 48 + _footer.Height + _status.Height + rows * _list.ItemHeight + 8;
        if (ClientSize.Height != wanted) { ClientSize = new Size(ClientSize.Width, wanted); ApplyRegion(); }
    }

    /// <summary>Action rows match by substring only — subsequence matching made "map" open "Manage profiles".</summary>
    static bool Word(string q, params string[] keywords) => q.Length > 0 && keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase));

    static bool Fuzzy(string text, string q)
    {
        if (text.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        var i = 0;
        foreach (var c in text) if (i < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length;
    }

    void OnKey(object? s, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Escape: Hide(); break;
            case Keys.F1: Hide(); _app.ShowHelp("quick-panel"); break;
            case Keys.Control | Keys.I: Hide(); _app.ShowInfo(); break;
            case Keys.Enter: RunSelected(); break;
            case Keys.Down: if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++; break;
            case Keys.Up: if (_list.SelectedIndex > 0) _list.SelectedIndex--; break;
            case Keys.Control | Keys.D: _app.ApplyDhcp(); break;
            case Keys.Control | Keys.E: Hide(); _app.ShowEditor((_list.SelectedItem as Item)?.Profile); break;
            case Keys.Control | Keys.R: if (_items.FirstOrDefault(i => i.Hint == "reach-replace") is { } r) r.Run(); break;
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }

    void RunSelected()
    {
        if (_list.SelectedItem is not Item it) return;
        if (it.OpensWindow) { Hide(); it.Run(); return; }
        it.Run(); // the panel stays; TrayApp.Status shows progress and auto-hides on success
    }

    void DrawItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not Item it) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Theme.Selection : Theme.Bg)) e.Graphics.FillRectangle(bg, e.Bounds);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var dot = new SolidBrush(it.Color)) e.Graphics.FillEllipse(dot, e.Bounds.X + 16, e.Bounds.Y + 17, 10, 10);
        var right = e.Bounds.Right - 14;
        if (it.Badge is not null) Theme.DrawBadge(e.Graphics, it.Badge, it.BadgeColor ?? Theme.Accent, ref right, e.Bounds.Y + 6, 20);
        TextRenderer.DrawText(e.Graphics, it.Title, Theme.Base, new Rectangle(e.Bounds.X + 36, e.Bounds.Y + 5, right - e.Bounds.X - 40, 20), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, it.Subtitle, Theme.Small, new Rectangle(e.Bounds.X + 36, e.Bounds.Y + 24, e.Bounds.Width - 44, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
