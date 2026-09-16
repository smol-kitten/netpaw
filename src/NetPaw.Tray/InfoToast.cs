using NetPaw.Connectivity;

namespace NetPaw.Tray;

/// <summary>
/// Compact always-on-top card with the live network facts an admin glances at: adapter, addresses,
/// gateway/DNS/link/intranet/internet as ✓/✗. Unpinned it fades after a few seconds; pinned it stays.
/// Monitor mode pins it automatically while there is a problem (sticky alert) and releases it on recovery.
/// </summary>
sealed class InfoToast : Form
{
    readonly TrayApp _app;
    readonly Label _title = new() { AutoSize = false, Height = 22, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 9.5f), Padding = new Padding(12, 6, 0, 0) };
    readonly Label _state = new() { AutoSize = false, Height = 18, Dock = DockStyle.Top, Font = Theme.Small, Padding = new Padding(12, 0, 0, 0) };
    readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 6, 12, 4), AutoSize = true };
    readonly Button _pin = new() { Text = "📌", Width = 28, Height = 22, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Emoji", 9f), Cursor = Cursors.Hand, TabStop = false };
    readonly Button _close = new() { Text = "✕", Width = 24, Height = 22, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TabStop = false };
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 6000 };
    bool _pinned, _autoPinned;

    public bool Pinned => _pinned;

    public InfoToast(TrayApp app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(340, 200); BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Base; DoubleBuffered = true;
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var b in new[] { _pin, _close }) { b.FlatAppearance.BorderSize = 0; b.BackColor = Theme.Panel; b.ForeColor = Theme.Muted; b.FlatAppearance.MouseOverBackColor = Theme.Selection; }
        _pin.Click += (_, _) => SetPinned(!_pinned, manual: true);
        _close.Click += (_, _) => { _autoPinned = false; SetPinned(false, manual: true); Hide(); };
        Controls.Add(_grid); Controls.Add(_state); Controls.Add(_title); Controls.Add(_pin); Controls.Add(_close);
        _pin.BringToFront(); _close.BringToFront();
        _fade.Tick += (_, _) => { _fade.Stop(); if (!_pinned) Hide(); };
        MouseEnter += (_, _) => _fade.Stop();
        MouseLeave += (_, _) => { if (!_pinned && Visible) _fade.Start(); };
        Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _close.PerformClick(); e.Handled = true; } if (e.KeyCode == Keys.F1) { _app.ShowHelp("network-info"); e.Handled = true; } };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; /* TOOLWINDOW | NOACTIVATE */ return cp; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.Dress(this, round: true);
        try { Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 10, 10)); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public void Present(bool pinned = false)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Render(_app.LastSnapshot);
        if (pinned) SetPinned(true, manual: false);
        Show();
        if (!_pinned) { _fade.Stop(); _fade.Start(); }
    }

    void SetPinned(bool on, bool manual)
    {
        _pinned = on;
        if (manual) _autoPinned = false;
        _pin.ForeColor = on ? Theme.Accent : Theme.Muted;
        _pin.BackColor = on ? Theme.Selection : Theme.Panel;
        if (on) _fade.Stop(); else if (Visible) _fade.Start();
    }

    /// <summary>Sticky alert: pin while a problem persists, unpin (and fade) once it is gone — unless the user pinned it themselves.</summary>
    public void AutoPin(bool problem)
    {
        if (problem && !_pinned) { _autoPinned = true; if (!Visible) Present(); SetPinned(true, manual: false); }
        else if (!problem && _autoPinned) { _autoPinned = false; SetPinned(false, manual: false); }
    }

    public void Render(Snapshot? s)
    {
        _grid.SuspendLayout(); _grid.Controls.Clear(); _grid.RowStyles.Clear();
        var a = s?.Adapter;
        _title.Text = a is null ? "No adapter" : a.Name + (a.SpeedText.Length > 0 ? "  ·  " + a.SpeedText : "");
        var state = s?.State ?? NetState.Unknown;
        _state.Text = Snapshot.Describe(state) + (s is null ? "" : $"   ·   {s.At:HH:mm:ss}");
        _state.ForeColor = Snapshot.IsProblem(state) ? Theme.Error : state is NetState.Online ? Theme.Static : Theme.Muted;
        void Row(string k, string v, bool? ok = null)
        {
            _grid.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
            var text = (ok switch { true => "✓  ", false => "✗  ", null => "" }) + v;
            _grid.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = ok switch { true => Theme.Static, false => Theme.Error, null => Theme.Text }, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(230, 0) });
        }
        if (a is not null)
        {
            Row("Link", a.Up ? "up" : "down", a.Up);
            Row("Address", a.Addresses.Count == 0 ? "none" : string.Join(", ", a.Addresses.Select(x => x.ToString())) + (a.Dhcp ? "  (DHCP)" : "") + (s.HasAddress ? "" : "  self-assigned"), s.HasAddress);
            Row("Gateway", a.HasGateway ? a.Gateways[0] + (s!.ChecksEnabled && s.Intranet.Count > 0 ? (s.GatewayOk ? $"  {s.Intranet[0].Ms} ms" : "  no reply") : "") : "not set", a.HasGateway && (!s!.ChecksEnabled || s.Intranet.Count == 0 || s.GatewayOk));
            // With checks on, each server carries its own verdict: "10.0.0.53 ✗, 10.0.0.54 ✓ 3 ms".
            var dnsText = a.Dns.Count == 0 ? "none" : s!.DnsServers.Count == 0 ? string.Join(", ", a.Dns) : string.Join(", ", s.DnsServers.Select(d => d.Ok ? $"{d.Target} ✓ {d.Ms} ms" : $"{d.Target} ✗"));
            Row("DNS", dnsText, a.Dns.Count > 0 && (s!.DnsCheck is null || s.DnsOk) && (s.DnsServers.Count == 0 || s.AnyDnsServerAnswers));
            if (s!.ChecksEnabled)
            {
                if (s.Intranet.Count > 1) Row("Intranet", string.Join(", ", s.Intranet.Skip(1).Select(p => $"{p.Target} {(p.Ok ? p.Ms + " ms" : "✗")}")), s.Intranet.Skip(1).Any(p => p.Ok));
                Row("Internet", string.Join(", ", s.Internet.Select(p => $"{p.Target} {(p.Ok ? p.Ms + " ms" : "✗")}")), s.InternetOk);
                if (s.HasAddress && (!s.InternetOk || !s.GatewayOk) && (s.Internet.FirstOrDefault()?.Target ?? s.Intranet.FirstOrDefault()?.Target) is { } traceTarget)
                {
                    _grid.Controls.Add(new Label { Text = "Path", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                    var trace = new LinkLabel { Text = $"Trace route to {traceTarget} — where does it stop?", AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(230, 0) };
                    trace.LinkClicked += (_, _) => _app.ShowTrace(traceTarget); Theme.Apply(trace); _grid.Controls.Add(trace);
                }
            }
            else Row("Checks", "off — enable in Settings for intranet/internet probes");
            var temps = _app.Service.TempAddresses.Count;
            if (temps > 0) Row("Temporary", $"{temps} address{(temps == 1 ? "" : "es")} added by NetPaw");
            foreach (var v in _app.Vpns) Row("VPN", $"{v.Kind} '{v.Adapter}' — {v.Mode}", v.Up ? true : null);
            foreach (var o in _app.Secondaries)
            {
                var oa = o.Adapter!;
                var gw = !oa.HasGateway ? "no gateway" : !o.ChecksEnabled ? "gw " + oa.Gateways[0] : o.GatewayOk ? $"gw {oa.Gateways[0]} {o.Intranet[0].Ms} ms" : $"gw {oa.Gateways[0]} no reply";
                Row("Also", $"{oa.Name}: {(oa.RealAddress?.ToString() ?? "no address")}{(oa.Dhcp ? " (DHCP)" : "")} · {gw}", !o.ChecksEnabled ? null : Snapshot.IsProblem(o.State) ? false : true);
                foreach (var adv in _app.SecondaryAdvisories.Where(x => x.Adapter == oa.Name))
                    Row("Advice", $"{oa.Name}: {adv.Title} — {adv.Text}", adv.Severity == AdvisorySeverity.Error ? false : null);
            }
            foreach (var sw in _app.SwitchNeighbors(a.Name)) Row("Switch", sw.Headline + (sw.PortDescription is null ? "" : "  ·  " + sw.PortDescription) + (sw.ManagementAddress is null ? "" : "  ·  " + sw.ManagementAddress), true);
            if (_app.LastVerifyDiffs.Count > 0)
            {
                _grid.Controls.Add(new Label { Text = "Verify", AutoSize = true, ForeColor = Theme.Temp, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
                host.Controls.Add(new Label { Text = "Last apply not fully effective: " + string.Join("; ", _app.LastVerifyDiffs), AutoSize = true, MaximumSize = new Size(230, 0), ForeColor = Theme.Temp, Font = Theme.Small, Margin = Padding.Empty });
                var reset = new LinkLabel { Text = $"Reset adapter {_app.LastVerifyAdapter} (disable → enable)", AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                reset.LinkClicked += (_, _) => _app.ResetAdapter(_app.LastVerifyAdapter); Theme.Apply(reset); host.Controls.Add(reset);
                _grid.Controls.Add(host);
            }
            if (_app.Service.Settings.Repair.DhcpAdvisory)
                foreach (var adv in _app.Advisories)
                {
                    var color = adv.Severity switch { AdvisorySeverity.Error => Theme.Error, AdvisorySeverity.Warning => Theme.Temp, _ => Theme.Muted };
                    _grid.Controls.Add(new Label { Text = "Advice", AutoSize = true, ForeColor = color, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                    var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
                    host.Controls.Add(new Label { Text = adv.Title + " — " + adv.Text, AutoSize = true, MaximumSize = new Size(230, 0), ForeColor = color, Font = Theme.Small, Margin = Padding.Empty });
                    if (adv.Repair == RepairKind.Renew && a.Dhcp)
                    {
                        var renew = new LinkLabel { Text = _app.RenewInfo, AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                        renew.LinkClicked += (_, _) => _app.Renew(manual: true);
                        Theme.Apply(renew);
                        host.Controls.Add(renew);
                    }
                    else if (adv.Repair != RepairKind.None)
                    {
                        var text = adv.Repair switch { RepairKind.Release => $"Release lease on {adv.RepairAdapter}", RepairKind.Reset => $"Reset adapter {adv.RepairAdapter}", RepairKind.Prefer => $"Prefer {adv.RepairAdapter} (pin metrics)", _ => adv.Repair.ToString() };
                        var fix = new LinkLabel { Text = text, AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                        var captured = adv; fix.LinkClicked += (_, _) => _app.Repair(captured);
                        Theme.Apply(fix);
                        host.Controls.Add(fix);
                    }
                    _grid.Controls.Add(host);
                }
        }
        _grid.ResumeLayout();
        var h = _title.Height + _state.Height + _grid.PreferredSize.Height + 8;
        if (ClientSize.Height != h) { ClientSize = new Size(ClientSize.Width, h); var area = Screen.PrimaryScreen!.WorkingArea; Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12); try { Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 10, 10)); } catch { } }
        _pin.Location = new Point(Width - 58, 4); _close.Location = new Point(Width - 28, 4);
    }
}
