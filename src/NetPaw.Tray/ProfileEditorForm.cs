using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;

namespace NetPaw.Tray;

/// <summary>Profile list on the left, one profile's fields on the right. Plain text fields (one address per line etc.) — fast to type, easy to paste.</summary>
sealed class ProfileEditorForm : Form
{
    readonly TrayApp _app;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36 };
    readonly TextBox _name = new(), _gateway = new(), _dns = new(), _suffix = new(), _hotkey = new(), _note = new();
    readonly TextBox _addresses = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = Theme.Mono };
    readonly TextBox _routes = new() { Multiline = true, Height = 56, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = Theme.Mono };
    readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly RadioButton _dhcp = new() { Text = "DHCP", AutoSize = true }, _static = new() { Text = "Static", AutoSize = true, Checked = true };
    readonly NumericUpDown _gwMetric = new() { Minimum = 0, Maximum = 9999, Value = 0, Width = 70 }, _ifMetric = new() { Minimum = 0, Maximum = 9999, Width = 70 };
    readonly CheckBox _setVlan = new() { Text = "set VLAN id", AutoSize = true };
    readonly NumericUpDown _vlan = new() { Minimum = 0, Maximum = 4094, Width = 70 };
    readonly Label _vlanInfo = new() { AutoSize = true, ForeColor = Theme.Muted }, _errors = new() { AutoSize = true, ForeColor = Theme.Error, MaximumSize = new Size(560, 0) };
    readonly IReadOnlyList<AdapterInfo> _adapters;
    Profile? _current;
    bool _loading;

    public ProfileEditorForm(TrayApp app)
    {
        _app = app;
        _adapters = app.Service.GetAdapters();
        Text = "NetPaw — profiles"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(900, 560); MinimumSize = new Size(760, 480);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 240, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };

        // left: list + buttons
        _list.DrawItem += DrawProfile;
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) LoadCurrent(); };
        var lbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
        Button B(string t, EventHandler h, int w = 54) { var b = new Button { Text = t, Width = w, Height = 28 }; b.Click += h; return b; }
        lbar.Controls.AddRange([B("New", (_, _) => NewProfile()), B("Copy", (_, _) => Duplicate()), B("Delete", (_, _) => Delete()), B("Capture", (_, _) => CaptureLive(), 64)]);
        split.Panel1.Controls.Add(_list); split.Panel1.Controls.Add(lbar);

        // right: fields
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 8, 12, 0), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c, string? hint = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 9, 0, 0) });
            if (hint is null) { c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Margin = new Padding(0, 4, 0, 4); grid.Controls.Add(c); return; }
            var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, WrapContents = false };
            c.Margin = new Padding(0, 4, 0, 0); c.Width = 520;
            host.Controls.Add(c); host.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 0, 0, 4) });
            grid.Controls.Add(host);
        }
        Control Inline(params Control[] cs) { var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4), WrapContents = false }; foreach (var c in cs) { c.Margin = new Padding(0, 2, 10, 0); f.Controls.Add(c); } return f; }

        _adapter.Items.Add("(work adapter)"); foreach (var a in _adapters) _adapter.Items.Add(a.Name);
        _adapter.SelectedIndexChanged += (_, _) => UpdateVlanInfo();
        _dhcp.CheckedChanged += (_, _) => ToggleStatic();
        _setVlan.CheckedChanged += (_, _) => _vlan.Enabled = _setVlan.Checked;

        Row("Name", _name);
        Row("Adapter", _adapter);
        Row("Mode", Inline(_dhcp, _static));
        Row("Addresses", _addresses, "one per line as ip/prefix or ip mask — first line is the primary, the rest are added as secondaries");
        Row("Gateway", Inline(_gateway, new Label { Text = "metric", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 4, 0) }, _gwMetric));
        _gateway.Width = 200;
        Row("DNS", _dns, "space or comma separated; empty = none");
        Row("DNS suffix", _suffix);
        Row("Interface metric", Inline(_ifMetric, new Label { Text = "0 = leave automatic", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 0, 0) }));
        Row("VLAN", Inline(_setVlan, _vlan, _vlanInfo));
        Row("Routes", _routes, "one per line: 10.0.0.0/8 via 192.168.1.1 metric 10   (via/metric optional)");
        Row("Hotkey", _hotkey, "global, needs a modifier — e.g. Ctrl+Alt+1");
        Row("Note", _note);
        grid.Controls.Add(new Label()); grid.Controls.Add(_errors);

        var rbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var save = new Button { Text = "Save", Width = 100, Height = 30 }; save.Click += (_, _) => Save();
        var apply = new Button { Text = "Save && apply", Width = 120, Height = 30 }; apply.Click += (_, _) => { if (Save()) { _app.ApplyProfile(_current!); } };
        var preview = new Button { Text = "Preview plan", Width = 120, Height = 30 }; preview.Click += (_, _) => Preview();
        rbar.Controls.AddRange([apply, save, preview]);
        split.Panel2.Controls.Add(grid); split.Panel2.Controls.Add(rbar);

        Controls.Add(split);
        Theme.Apply(this); Theme.Primary(apply);
        _list.BackColor = Theme.Panel; split.Panel1.BackColor = Theme.Panel;
        Load += (_, _) => { Native.Dress(this); Reload(); };
    }

    // ---- list ------------------------------------------------------------------------------

    void Reload(Profile? select = null)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var p in _app.Service.Profiles.Where(p => !p.Temporary)) _list.Items.Add(p);
        _loading = false;
        if (_list.Items.Count > 0) _list.SelectedItem = select ?? _list.Items[0];
        else { _current = null; NewProfile(); }
    }

    public void Select(Profile p) => _list.SelectedItem = _app.Service.Profiles.FirstOrDefault(x => x.Id == p.Id) ?? _list.SelectedItem;

    void DrawProfile(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not Profile p) return;
        var sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? Theme.Selection : Theme.Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var dot = new SolidBrush(p.Dhcp ? Theme.Dhcp : Theme.Static)) e.Graphics.FillEllipse(dot, e.Bounds.X + 10, e.Bounds.Y + 13, 9, 9);
        TextRenderer.DrawText(e.Graphics, p.Name, Theme.Base, new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 3, e.Bounds.Width - 30, 17), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, p.Summary(), Theme.Small, new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 19, e.Bounds.Width - 30, 15), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void NewProfile()
    {
        _current = new Profile { Name = "new profile", Addresses = [new IpAddr("192.168.1.10", 24)], Gateway = "192.168.1.1" };
        _list.ClearSelected();
        LoadFields(_current);
        _name.Focus(); _name.SelectAll();
    }

    void Duplicate()
    {
        if (_list.SelectedItem is not Profile p) return;
        var c = p.Clone(); c.Id = Guid.NewGuid().ToString("N"); c.Name = p.Name + " copy"; c.Hotkey = null;
        _app.Service.Profiles.Add(c); _app.Service.SaveProfiles();
        Reload(c);
    }

    void Delete()
    {
        if (_list.SelectedItem is not Profile p) return;
        if (MessageBox.Show(this, $"Delete profile '{p.Name}'?", "NetPaw", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _app.Service.Profiles.Remove(p); _app.Service.SaveProfiles();
        Reload();
    }

    void CaptureLive()
    {
        var w = _app.Work; if (w is null) return;
        var p = _app.Service.Capture(w.Dhcp ? "DHCP" : w.Primary?.Network ?? "captured", w);
        _app.Service.Profiles.Add(p); _app.Service.SaveProfiles();
        Reload(p);
        _name.Focus(); _name.SelectAll();
    }

    // ---- fields ----------------------------------------------------------------------------

    void LoadCurrent() { if (_list.SelectedItem is Profile p) { _current = p; LoadFields(p); } }

    void LoadFields(Profile p)
    {
        _loading = true;
        _name.Text = p.Name;
        _adapter.SelectedIndex = p.Adapter is null ? 0 : Math.Max(0, _adapter.Items.IndexOf(p.Adapter));
        _dhcp.Checked = p.Dhcp; _static.Checked = !p.Dhcp;
        _addresses.Text = string.Join(Environment.NewLine, p.Addresses.Select(a => a.ToString()));
        _gateway.Text = p.Gateway ?? ""; _gwMetric.Value = p.GatewayMetric ?? 0; _ifMetric.Value = p.InterfaceMetric ?? 0;
        _dns.Text = string.Join(" ", p.Dns); _suffix.Text = p.DnsSuffix ?? "";
        _setVlan.Checked = p.VlanId is not null; _vlan.Value = p.VlanId ?? 0; _vlan.Enabled = _setVlan.Checked;
        _routes.Text = string.Join(Environment.NewLine, p.Routes.Select(r => r.ToString()));
        _hotkey.Text = p.Hotkey ?? ""; _note.Text = p.Note ?? "";
        _errors.Text = "";
        _loading = false;
        ToggleStatic(); UpdateVlanInfo();
    }

    void ToggleStatic()
    {
        var on = _static.Checked;
        foreach (var c in new Control[] { _addresses, _gateway, _gwMetric, _dns, _suffix }) c.Enabled = on;
    }

    void UpdateVlanInfo()
    {
        var name = _adapter.SelectedIndex <= 0 ? _app.Work?.Name : (string)_adapter.SelectedItem!;
        if (name is null) { _vlanInfo.Text = ""; return; }
        var v = _app.Service.Vlan.Query(name);
        _vlanInfo.Text = v.Capable ? $"{name}: driver supports VlanID (now {v.VlanId?.ToString() ?? "?"})" : $"{name}: driver exposes no VlanID — tagging unavailable";
        _vlanInfo.ForeColor = v.Capable ? Theme.Static : Theme.Muted;
    }

    Profile? ReadFields()
    {
        var p = (_current ?? new Profile()).Clone();
        var errors = new List<string>();
        p.Name = _name.Text.Trim();
        p.Adapter = _adapter.SelectedIndex <= 0 ? null : (string)_adapter.SelectedItem!;
        p.Dhcp = _dhcp.Checked;
        p.Addresses = [];
        foreach (var line in Lines(_addresses.Text))
            if (IpMath.TryParseCidr(line, out var a)) p.Addresses.Add(a); else errors.Add($"address '{line}' is not ip/prefix");
        p.Gateway = string.IsNullOrWhiteSpace(_gateway.Text) ? null : _gateway.Text.Trim();
        p.GatewayMetric = _gwMetric.Value == 0 ? null : (int)_gwMetric.Value;
        p.InterfaceMetric = _ifMetric.Value == 0 ? null : (int)_ifMetric.Value;
        p.Dns = _dns.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        p.DnsSuffix = string.IsNullOrWhiteSpace(_suffix.Text) ? null : _suffix.Text.Trim();
        p.VlanId = _setVlan.Checked ? (int)_vlan.Value : null;
        p.Routes = [];
        foreach (var line in Lines(_routes.Text))
            if (StaticRoute.TryParse(line, out var r)) p.Routes.Add(r); else errors.Add($"route '{line}' is not dest/prefix [via gw] [metric n]");
        p.Hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? null : _hotkey.Text.Trim();
        p.Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();
        errors.AddRange(p.Validate());
        if (p.Hotkey is not null && _app.Service.Profiles.Any(o => o.Id != p.Id && string.Equals(o.Hotkey, p.Hotkey, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"hotkey {p.Hotkey} is already used by another profile");
        _errors.Text = string.Join(Environment.NewLine, errors);
        return errors.Count == 0 ? p : null;
    }

    static IEnumerable<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    bool Save()
    {
        var p = ReadFields(); if (p is null) return false;
        var list = _app.Service.Profiles;
        var i = list.FindIndex(x => x.Id == p.Id);
        if (i >= 0) list[i] = p; else list.Add(p);
        _app.Service.SaveProfiles();
        _current = p;
        Reload(p);
        _app.RegisterHotkeys();
        return true;
    }

    void Preview()
    {
        var p = ReadFields(); if (p is null) return;
        try { PlanPreviewForm.Confirm(_app.Service.PlanProfile(p, _app.Service.ResolveAdapter(p.Adapter, _adapters)), "Preview only — nothing is applied from here."); }
        catch (InvalidOperationException ex) { _errors.Text = ex.Message; }
    }
}
