using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Net;

namespace NetPaw.Tray;

/// <summary>
/// Profile list on the left, one profile's fields on the right in three sections:
/// Identity, IPv4, Advanced (collapsed). Errors show under the field they belong to.
/// Managed profiles (deployed machine-wide) are shown read-only.
/// </summary>
sealed class ProfileEditorForm : Form
{
    readonly TrayApp _app;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 38 };
    readonly SectionPanel _identity = new("Identity"), _ipv4 = new("IPv4"), _advanced = new("Advanced", collapsible: true, collapsed: true);
    readonly TextBox _name = new(), _gateway = new(), _dns = new(), _suffix = new(), _hotkey = new(), _note = new();
    readonly TextBox _addresses = new() { Multiline = true, Height = 66, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = Theme.Mono };
    readonly TextBox _routes = new() { Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = Theme.Mono };
    readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly RadioButton _dhcp = new() { Text = "DHCP", AutoSize = true }, _static = new() { Text = "Static", AutoSize = true, Checked = true };
    readonly NumericUpDown _gwMetric = new() { Minimum = 0, Maximum = 9999, Width = 64 }, _ifMetric = new() { Minimum = 0, Maximum = 9999, Width = 64 };
    readonly CheckBox _setVlan = new() { Text = "set VLAN id", AutoSize = true };
    readonly NumericUpDown _vlan = new() { Minimum = 0, Maximum = 4094, Width = 64 };
    readonly Label _vlanInfo = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Label _macInfo = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Label _managedNote = new() { AutoSize = true, ForeColor = Theme.Temp, Font = Theme.Small, Visible = false, Dock = DockStyle.Top, Padding = new Padding(0, Theme.Spacing.S, 0, 0) };
    readonly Button _save, _apply, _preview, _delete;
    readonly IReadOnlyList<AdapterInfo> _adapters;
    Profile? _current;
    bool _loading;

    public ProfileEditorForm(TrayApp app)
    {
        _app = app;
        _adapters = app.Service.GetAdapters();
        Text = "NetPaw — profiles"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(940, 640); MinimumSize = new Size(780, 520);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };

        // left: list + buttons
        _list.DrawItem += DrawProfile;
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) LoadCurrent(); };
        var lbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(3, 4, 0, 0), WrapContents = false };
        Button B(string t, EventHandler h, int w = 50) { var b = new Button { Text = t, Width = w, Height = 26, Margin = new Padding(2, 0, 2, 0) }; b.Click += h; return b; }
        _delete = B("Delete", (_, _) => Delete(), 56);
        lbar.Controls.AddRange([B("New", (_, _) => NewProfile()), B("Copy", (_, _) => Duplicate()), _delete, B("Capture", (_, _) => CaptureLive(), 62)]);
        split.Panel1.Controls.Add(_list); split.Panel1.Controls.Add(lbar);

        // right: sections (docked Top in reverse order inside a scrolling panel)
        _adapter.Items.Add("(work adapter)"); foreach (var a in _adapters) _adapter.Items.Add(a.Name);
        _adapter.SelectedIndexChanged += (_, _) => { UpdateVlanInfo(); UpdateMacInfo(); };
        _dhcp.CheckedChanged += (_, _) => ToggleStatic();
        _setVlan.CheckedChanged += (_, _) => _vlan.Enabled = _setVlan.Checked;

        _identity.Row("Name", _name, "name");
        _identity.Row("Adapter", Inline(_adapter, _macInfo), "adapter", hint: "\"work adapter\" = the one selected in the tray menu. A named adapter is remembered by MAC, so renames and dock ports do not break the profile.");
        _adapter.Width = 300;
        _identity.Row("Hotkey", _hotkey, "hotkey", hint: "global, needs a modifier — e.g. Ctrl+Alt+1", width: 200);

        _ipv4.Row("Mode", Inline(_dhcp, _static), "mode");
        _ipv4.Row("Addresses", _addresses, "addresses", hint: "one per line: ip/prefix or ip mask. First line is the primary; the rest are added as secondaries.");
        _gateway.Width = 200;
        _ipv4.Row("Gateway", Inline(_gateway, Muted("metric"), _gwMetric), "gateway", hint: "metric 0 = automatic");
        _ipv4.Row("DNS", _dns, "dns", hint: "space or comma separated; empty = none");
        _ipv4.Row("DNS suffix", _suffix, "suffix", width: 300);

        _advanced.Row("Interface metric", Inline(_ifMetric, Muted("0 = leave automatic")), "ifmetric");
        _advanced.Row("VLAN", Inline(_setVlan, _vlan, _vlanInfo), "vlan");
        _advanced.Row("Routes", _routes, "routes", hint: "one per line: 10.0.0.0/8 via 192.168.1.1 metric 10   (via/metric optional)");
        _advanced.Row("Note", _note, "note");

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Theme.Spacing.L, 0, Theme.Spacing.L, 0) };
        scroll.Controls.Add(_advanced); scroll.Controls.Add(_ipv4); scroll.Controls.Add(_identity); scroll.Controls.Add(_managedNote);

        var rbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        _save = new Button { Text = "Save  (Ctrl+S)", Width = 120, Height = 30 }; _save.Click += (_, _) => Save();
        _apply = new Button { Text = "Save && apply  (Ctrl+Enter)", Width = 200, Height = 30 }; _apply.Click += (_, _) => SaveAndApply();
        _preview = new Button { Text = "Preview plan  (Ctrl+P)", Width = 160, Height = 30 }; _preview.Click += (_, _) => Preview();
        rbar.Controls.AddRange([_apply, _save, _preview]);
        split.Panel2.Controls.Add(scroll); split.Panel2.Controls.Add(rbar);

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            switch (e.KeyData)
            {
                case Keys.Control | Keys.S: Save(); break;
                case Keys.Control | Keys.P: Preview(); break;
                case Keys.Control | Keys.Enter: SaveAndApply(); break;
                case Keys.Escape: Close(); break;
                case Keys.F1: _app.ShowHelp("profiles"); break;
                default: return;
            }
            e.Handled = true; e.SuppressKeyPress = true;
        };

        Controls.Add(split);
        Theme.Apply(this); Theme.Primary(_apply);
        _list.BackColor = Theme.Panel; split.Panel1.BackColor = Theme.Panel;
        Load += (_, _) =>
        {
            Native.Dress(this); split.SplitterDistance = 260; Reload();
            if (!_app.Service.Allowed(Capability.UserProfiles))
                foreach (Control b in lbar.Controls) b.Enabled = false;
        };
    }

    static Label Muted(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 7, 4, 0) };
    static Control Inline(params Control[] cs) { var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty }; foreach (var c in cs) { c.Margin = new Padding(0, 2, 10, 0); f.Controls.Add(c); } return f; }

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
        using (var dot = new SolidBrush(p.Dhcp ? Theme.Dhcp : Theme.Static)) e.Graphics.FillEllipse(dot, e.Bounds.X + 10, e.Bounds.Y + 14, 9, 9);
        var right = e.Bounds.Right - 8;
        if (p.Managed) Theme.DrawBadge(e.Graphics, "managed", Theme.Temp, ref right, e.Bounds.Y + 4, 18);
        else if (p.Source is not null) Theme.DrawBadge(e.Graphics, p.Source, Theme.Accent, ref right, e.Bounds.Y + 4, 18);
        TextRenderer.DrawText(e.Graphics, p.Name, Theme.Base, new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 4, right - e.Bounds.X - 30, 17), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, p.Summary(), Theme.Small, new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 20, e.Bounds.Width - 30, 15), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
        var c = p.Clone(); c.Id = Guid.NewGuid().ToString("N"); c.Name = p.Name + " copy"; c.Hotkey = null; c.Managed = false; c.Source = null;
        _app.Service.Profiles.Add(c); _app.Service.SaveProfiles();
        Reload(c);
    }

    void Delete()
    {
        if (_list.SelectedItem is not Profile p || p.Managed) return;
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
        _advanced.Collapsed = p.InterfaceMetric is null && p.VlanId is null && p.Routes.Count == 0 && p.Note is null;
        foreach (var s in new[] { _identity, _ipv4, _advanced }) s.ClearErrors();
        _loading = false;
        SetReadOnly(p.Managed, p.Source);
        ToggleStatic(); UpdateVlanInfo(); UpdateMacInfo();
    }

    void SetReadOnly(bool managed, string? source)
    {
        var locked = !_app.Service.Allowed(Capability.UserProfiles);
        _managedNote.Visible = managed || locked;
        _managedNote.Text = managed ? $"Managed profile (deployed by your organisation{(source is null ? "" : ", " + source)}). You can apply or copy it, not change it."
                          : locked ? "Your organisation allows only managed profiles on this machine." : "";
        managed = managed || locked;
        foreach (var c in new Control[] { _name, _adapter, _dhcp, _static, _addresses, _gateway, _gwMetric, _dns, _suffix, _ifMetric, _setVlan, _vlan, _routes, _hotkey, _note })
            c.Enabled = !managed;
        _save.Enabled = !managed; _delete.Enabled = !managed;
        _apply.Text = managed ? "Apply  (Ctrl+Enter)" : "Save && apply  (Ctrl+Enter)";
    }

    void ToggleStatic()
    {
        if (_current?.Managed == true) return;
        var on = _static.Checked;
        foreach (var c in new Control[] { _addresses, _gateway, _gwMetric, _dns, _suffix }) c.Enabled = on;
    }

    void UpdateMacInfo()
    {
        var name = _adapter.SelectedIndex <= 0 ? null : (string)_adapter.SelectedItem!;
        var live = name is null ? null : _adapters.FirstOrDefault(a => a.Name == name);
        _macInfo.Text = live is null ? (_current?.AdapterMac is { Length: > 0 } m ? $"bound to {m} (not present now)" : "") : $"bound to {live.Mac}";
    }

    void UpdateVlanInfo()
    {
        var name = _adapter.SelectedIndex <= 0 ? _app.Work?.Name : (string)_adapter.SelectedItem!;
        if (name is null) { _vlanInfo.Text = ""; return; }
        var v = _app.Service.QueryVlan(name);
        _vlanInfo.Text = v.Capable ? $"{name}: driver supports VlanID (now {v.VlanId?.ToString() ?? "?"})" : $"{name}: driver exposes no VlanID — tagging unavailable";
        _vlanInfo.ForeColor = v.Capable ? Theme.Static : Theme.Muted;
    }

    Profile? ReadFields()
    {
        var p = (_current ?? new Profile()).Clone();
        foreach (var s in new[] { _identity, _ipv4, _advanced }) s.ClearErrors();
        var ok = true;
        void Err(SectionPanel s, string key, string msg) { s.SetError(key, msg); ok = false; }

        p.Name = _name.Text.Trim();
        if (p.Name.Length == 0) Err(_identity, "name", "Name is required.");
        p.Adapter = _adapter.SelectedIndex <= 0 ? null : (string)_adapter.SelectedItem!;
        p.AdapterMac = p.Adapter is null ? null : _adapters.FirstOrDefault(a => a.Name == p.Adapter)?.Mac ?? p.AdapterMac;
        p.Dhcp = _dhcp.Checked;
        p.Addresses = [];
        var badAddr = new List<string>();
        foreach (var line in Lines(_addresses.Text))
            if (IpMath.TryParseCidr(line, out var a)) p.Addresses.Add(a); else badAddr.Add(line);
        if (badAddr.Count > 0) Err(_ipv4, "addresses", "Not ip/prefix: " + string.Join(", ", badAddr));
        else if (!p.Dhcp && p.Addresses.Count == 0) Err(_ipv4, "addresses", "A static profile needs at least one address.");
        else if (p.Addresses.GroupBy(a => a.Address).FirstOrDefault(g => g.Count() > 1) is { } dup) Err(_ipv4, "addresses", $"{dup.Key} is listed twice.");
        p.Gateway = string.IsNullOrWhiteSpace(_gateway.Text) ? null : _gateway.Text.Trim();
        if (p.Gateway is not null && !IpMath.IsIPv4(p.Gateway)) Err(_ipv4, "gateway", "Not an IPv4 address.");
        else if (p.Gateway is not null && p.Addresses.Count > 0 && !p.Addresses.Any(a => a.Contains(p.Gateway))) Err(_ipv4, "gateway", $"{p.Gateway} is not inside any of the profile's subnets.");
        p.GatewayMetric = _gwMetric.Value == 0 ? null : (int)_gwMetric.Value;
        p.InterfaceMetric = _ifMetric.Value == 0 ? null : (int)_ifMetric.Value;
        p.Dns = _dns.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (p.Dns.FirstOrDefault(d => !IpMath.IsIPv4(d)) is { } badDns) Err(_ipv4, "dns", $"'{badDns}' is not an IPv4 address.");
        p.DnsSuffix = string.IsNullOrWhiteSpace(_suffix.Text) ? null : _suffix.Text.Trim();
        p.VlanId = _setVlan.Checked ? (int)_vlan.Value : null;
        p.Routes = [];
        var badRoutes = new List<string>();
        foreach (var line in Lines(_routes.Text))
            if (StaticRoute.TryParse(line, out var r)) p.Routes.Add(r); else badRoutes.Add(line);
        if (badRoutes.Count > 0) Err(_advanced, "routes", "Not dest/prefix [via gw] [metric n]: " + string.Join(", ", badRoutes));
        p.Hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? null : _hotkey.Text.Trim();
        if (p.Hotkey is not null && !Hotkeys.HotkeyParser.TryParse(p.Hotkey, out _)) Err(_identity, "hotkey", "Needs a modifier and a key, e.g. Ctrl+Alt+1.");
        else if (p.Hotkey is not null && _app.Service.Profiles.Any(o => o.Id != p.Id && string.Equals(o.Hotkey, p.Hotkey, StringComparison.OrdinalIgnoreCase)))
            Err(_identity, "hotkey", $"{p.Hotkey} is already used by another profile.");
        p.Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();
        return ok ? p : null;
    }

    static IEnumerable<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    void SaveAndApply()
    {
        if (_current?.Managed == true) { _app.ApplyProfile(_current); return; }
        if (Save()) _app.ApplyProfile(_current!);
    }

    bool Save()
    {
        if (_current?.Managed == true) return false;
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
        var p = _current?.Managed == true ? _current : ReadFields(); if (p is null) return;
        try { PlanPreviewForm.ShowReadOnly(_app.Service.PlanProfile(p, _app.Service.ResolveAdapter(p, _adapters)), "Preview only — nothing is applied from here."); }
        catch (InvalidOperationException ex) { _identity.SetError("adapter", ex.Message); }
    }
}
