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
    sealed record Item(string Title, string Subtitle, Color Color, Action Run, string? Hint = null, Profile? Profile = null);

    readonly TrayApp _app;
    readonly Label _header = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(14, 8, 14, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly TextBox _input = new() { Dock = DockStyle.Top, Font = Theme.Big, BorderStyle = BorderStyle.None, Margin = Padding.Empty, PlaceholderText = "profile, device, or IP…" };
    readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 6, 14, 0), ForeColor = Theme.Muted, Font = Theme.Small, Text = "↵ apply    Ctrl+D DHCP    Ctrl+E edit    Ctrl+R replace primary    Esc close" };
    readonly List<Item> _items = [];

    public QuickPanel(TrayApp app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(520, 420); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Base; DoubleBuffered = true;
        var inputHost = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 10, 14, 6), BackColor = Theme.Panel };
        _input.BackColor = Theme.Panel; _input.ForeColor = Theme.Text;
        inputHost.Controls.Add(_input);
        _list.BackColor = Theme.Bg; _list.ForeColor = Theme.Text;
        Controls.Add(_list); Controls.Add(_footer); Controls.Add(inputHost); Controls.Add(_header);
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
        try { Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12)); } catch { }
    }

    protected override bool ShowWithoutActivation => false;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */; return cp; } }

    public void Present()
    {
        _app.RefreshState();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 5);
        _input.Text = "";
        Rebuild();
        Show(); Activate(); Native.SetForegroundWindow(Handle);
        _input.Focus();
    }

    public void RefreshHeader()
    {
        var w = _app.Work;
        var temps = _app.Service.TempAddresses.Count;
        _header.Text = w is null ? "no adapter" : $"{w.Name}  ·  {w.Summary()}{(temps > 0 ? $"  ·  {temps} temp" : "")}";
    }

    void Rebuild()
    {
        _items.Clear();
        var q = _input.Text.Trim();
        var svc = _app.Service; var w = _app.Work;
        var looksLikeIp = q.Length > 0 && char.IsAsciiDigit(q[0]) && q.Contains('.');

        if (looksLikeIp)
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
                _items.Add(new Item(p.Name + (current ? "   ✓" : ""), p.Summary() + (p.Hotkey is null ? "" : "   " + p.Hotkey), p.Dhcp ? Theme.Dhcp : Theme.Static, () => _app.ApplyProfile(p), null, p));
            }
            if (q.Length == 0 || Fuzzy("dhcp", q))
                _items.Add(new Item("DHCP now" + (w?.Dhcp == true ? "   ✓" : ""), $"Let {w?.Name ?? "the adapter"} take a lease", Theme.Dhcp, _app.ApplyDhcp));
            if (q.Length > 0)
                foreach (var pr in PresetLibrary.Search(svc.Presets, q).Take(8))
                    _items.Add(new Item($"{pr.Vendor} {pr.Model}", $"{pr.Ip}/{pr.Prefix}{(pr.Note is null ? "" : "   " + pr.Note)}", Theme.Temp, () => _app.ApplyPreset(pr)));
            var temps = svc.TempAddresses;
            if (temps.Count > 0 && (q.Length == 0 || Fuzzy("temporary", q) || Fuzzy("clear", q)))
                _items.Add(new Item($"Remove {temps.Count} temporary address{(temps.Count == 1 ? "" : "es")}", string.Join(", ", temps.Select(t => t.Address.ToString())), Theme.Temp, _app.ClearTemp));
            if (q.Length == 0 || Fuzzy("manage profiles", q) || Fuzzy("edit", q))
                _items.Add(new Item("Manage profiles…", "Create, edit, hotkeys, routes, VLAN", Theme.Muted, () => _app.ShowEditor()));
        }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in _items) _list.Items.Add(i);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.EndUpdate();
    }

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
            case Keys.Enter: RunSelected(); break;
            case Keys.Down: if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++; break;
            case Keys.Up: if (_list.SelectedIndex > 0) _list.SelectedIndex--; break;
            case Keys.Control | Keys.D: Hide(); _app.ApplyDhcp(); break;
            case Keys.Control | Keys.E: Hide(); _app.ShowEditor((_list.SelectedItem as Item)?.Profile); break;
            case Keys.Control | Keys.R: if (_items.FirstOrDefault(i => i.Hint == "reach-replace") is { } r) { Hide(); r.Run(); } break;
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }

    void RunSelected()
    {
        if (_list.SelectedItem is not Item it) return;
        Hide();
        it.Run();
    }

    void DrawItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not Item it) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Theme.Selection : Theme.Bg)) e.Graphics.FillRectangle(bg, e.Bounds);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var dot = new SolidBrush(it.Color)) e.Graphics.FillEllipse(dot, e.Bounds.X + 16, e.Bounds.Y + 17, 10, 10);
        TextRenderer.DrawText(e.Graphics, it.Title, Theme.Base, new Rectangle(e.Bounds.X + 36, e.Bounds.Y + 5, e.Bounds.Width - 44, 20), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, it.Subtitle, Theme.Small, new Rectangle(e.Bounds.X + 36, e.Bounds.Y + 24, e.Bounds.Width - 44, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
