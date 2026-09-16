using NetPaw.Connectivity;

namespace NetPaw.Tray;

/// <summary>
/// The rows of the network info card (link, address, gateway, DNS, internet, Wi-Fi, MTU, other adapters,
/// advice with repair links …) as one control, so the toast and the main window paint the same thing.
/// The host reads <see cref="TitleText"/>, <see cref="StateText"/>, <see cref="StateColor"/> after <see cref="Render"/>.
/// </summary>
sealed class InfoCardPanel : UserControl
{
    readonly TrayApp _app;
    readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 6, 12, 4), AutoSize = true };

    public string TitleText { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public Color StateColor { get; private set; } = Theme.Muted;
    /// <summary>Wrap width of the value column: 230 in the toast, wider in the main window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public int TextWidth { get; set; } = 230;
    /// <summary>Fires after every render so a host can resize itself.</summary>
    public event Action? Rendered;
    public int PreferredHeight => _grid.PreferredSize.Height;

    public InfoCardPanel(TrayApp app)
    {
        _app = app;
        BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Base; AutoSize = true; DoubleBuffered = true;
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_grid);
    }

    public void Render(Snapshot? s)
    {
        _grid.SuspendLayout(); _grid.Controls.Clear(); _grid.RowStyles.Clear();
        var a = s?.Adapter;
        TitleText = a is null ? "No adapter" : a.Name + (a.SpeedText.Length > 0 ? "  ·  " + a.SpeedText : "");
        var state = s?.State ?? NetState.Unknown;
        var stale = s is not null && s.ChecksEnabled && Cadence.Stale(s.At, DateTimeOffset.Now, _app.CurrentInterval);
        StateText = Snapshot.Describe(state) + (s is null ? "" : stale ? $"   ·   last check {(int)(DateTimeOffset.Now - s.At).TotalMinutes} min ago" : $"   ·   {s.At:HH:mm:ss}");
        StateColor = Snapshot.IsProblem(state) ? Theme.Error : state is NetState.Online ? Theme.Static : Theme.Muted;
        void Row(string k, string v, bool? ok = null)
        {
            _grid.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
            var text = (ok switch { true => "✓  ", false => "✗  ", null => "" }) + v;
            _grid.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = ok switch { true => Theme.Static, false => Theme.Error, null => Theme.Text }, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(TextWidth, 0) });
        }
        // Per-row visibility (Settings → Info card): a row renders when its mode is Always, or OnIssue and its own verdict is bad.
        var card = _app.Service.Settings.CardSettings();
        void Show(string kind, bool issue, string v, bool? ok = null) { if (card.Show(kind, issue)) Row(kind, v, ok); }
        if (a is not null)
        {
            Show("Link", !a.Up, a.Up ? "up" : "down", a.Up);
            if (s!.Wlan is { } wl) Show("Wi-Fi", wl.Weak, wl.Summary, wl.Signal is null ? null : !wl.Weak);
            if (s.Dot1x is { } dx && (dx.Failed || dx.InProgress)) Show("802.1X", true, dx.State, !dx.Failed);
            if (s.PathMtu is { } pm) Show("Path MTU", pm.Reduced, $"{pm.Mtu} to {pm.Host}", !pm.Reduced);
            Show("Address", !s.HasAddress, a.Addresses.Count == 0 ? "none" : string.Join(", ", a.Addresses.Select(x => x.ToString())) + (a.Dhcp ? "  (DHCP)" : "") + (s.HasAddress ? "" : "  self-assigned"), s.HasAddress);
            var gwOk = a.HasGateway && (!s!.ChecksEnabled || s.Intranet.Count == 0 || s.GatewayOk);
            Show("Gateway", !gwOk, a.HasGateway ? a.Gateways[0] + (s!.ChecksEnabled && s.Intranet.Count > 0 ? (s.GatewayOk ? $"  {s.Intranet[0].Ms} ms" : "  no reply") : "") : "not set", gwOk);
            // With checks on, each server carries its own verdict: "10.0.0.53 ✗, 10.0.0.54 ✓ 3 ms".
            var dnsText = a.Dns.Count == 0 ? "none" : s!.DnsServers.Count == 0 ? string.Join(", ", a.Dns) : string.Join(", ", s.DnsServers.Select(d => d.Ok ? $"{d.Target} ✓ {d.Ms} ms" : $"{d.Target} ✗"));
            var dnsOk = a.Dns.Count > 0 && (s!.DnsCheck is null || s.DnsOk) && (s.DnsServers.Count == 0 || s.AnyDnsServerAnswers);
            Show("DNS", !dnsOk || s!.DnsServers.Any(d => !d.Ok), dnsText, dnsOk);
            if (s!.ChecksEnabled)
            {
                if (s.Intranet.Count > 1) Show("Intranet", s.Intranet.Skip(1).Any(p => !p.Ok), string.Join(", ", s.Intranet.Skip(1).Select(p => $"{p.Target} {(p.Ok ? p.Ms + " ms" : "✗")}")), s.Intranet.Skip(1).Any(p => p.Ok));
                Show("Internet", !s.InternetOk, string.Join(", ", s.Internet.Select(p => $"{p.Target} {(p.Ok ? p.Ms + " ms" : "✗")}")), s.InternetOk);
                if (s.HasAddress && (!s.InternetOk || !s.GatewayOk) && (s.Internet.FirstOrDefault()?.Target ?? s.Intranet.FirstOrDefault()?.Target) is { } traceTarget)
                {
                    _grid.Controls.Add(new Label { Text = "Path", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                    var trace = new LinkLabel { Text = $"Trace route to {traceTarget} — where does it stop?", AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(TextWidth, 0) };
                    trace.LinkClicked += (_, _) => _app.ShowTrace(traceTarget); Theme.Apply(trace); _grid.Controls.Add(trace);
                }
            }
            else Show("Checks", false, "off — enable in Settings for intranet/internet probes");
            var temps = _app.Service.TempAddresses.Count;
            if (temps > 0) Show("Temporary", true, $"{temps} address{(temps == 1 ? "" : "es")} added by NetPaw");
            foreach (var v in _app.Vpns) Show("VPN", !v.Up, $"{v.Kind} '{v.Adapter}' — {v.Mode}", v.Up ? true : null);
            foreach (var o in _app.Secondaries)
            {
                var oa = o.Adapter!;
                var gw = !oa.HasGateway ? "no gateway" : !o.ChecksEnabled ? "gw " + oa.Gateways[0] : o.GatewayOk ? $"gw {oa.Gateways[0]} {o.Intranet[0].Ms} ms" : $"gw {oa.Gateways[0]} no reply";
                var secondaryAdvice = _app.SecondaryAdvisories.Where(x => x.Adapter == oa.Name).ToList();
                var secondaryIssue = (o.ChecksEnabled && Snapshot.IsProblem(o.State)) || secondaryAdvice.Count > 0;
                Show("Also", secondaryIssue, $"{oa.Name}: {(oa.RealAddress?.ToString() ?? "no address")}{(oa.Dhcp ? " (DHCP)" : "")} · {gw}", !o.ChecksEnabled ? null : Snapshot.IsProblem(o.State) ? false : true);
                foreach (var adv in secondaryAdvice.Where(x => card.ShowAdvice(x.Severity)))
                    Row("Advice", $"{oa.Name}: {adv.Title} — {adv.Text}", adv.Severity == AdvisorySeverity.Error ? false : null);
            }
            foreach (var sw in _app.SwitchNeighbors(a.Name)) if (card.Show("Switch", false)) Row("Switch", sw.Headline + (sw.PortDescription is null ? "" : "  ·  " + sw.PortDescription) + (sw.ManagementAddress is null ? "" : "  ·  " + sw.ManagementAddress), true);
            if (_app.LastVerifyDiffs.Count > 0)
            {
                _grid.Controls.Add(new Label { Text = "Verify", AutoSize = true, ForeColor = Theme.Temp, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
                host.Controls.Add(new Label { Text = "Last apply not fully effective: " + string.Join("; ", _app.LastVerifyDiffs), AutoSize = true, MaximumSize = new Size(TextWidth, 0), ForeColor = Theme.Temp, Font = Theme.Small, Margin = Padding.Empty });
                var reset = new LinkLabel { Text = $"Reset adapter {_app.LastVerifyAdapter} (disable → enable)", AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                reset.LinkClicked += (_, _) => _app.ResetAdapter(_app.LastVerifyAdapter); Theme.Apply(reset); host.Controls.Add(reset);
                _grid.Controls.Add(host);
            }
            var adviceRows = (_app.Service.Settings.Repair.DhcpAdvisory ? _app.Advisories : []).Concat(_app.IntentAdvisories);
            foreach (var adv in adviceRows.Where(x => card.ShowAdvice(x.Severity)))
                {
                    var color = adv.Severity switch { AdvisorySeverity.Error => Theme.Error, AdvisorySeverity.Warning => Theme.Temp, _ => Theme.Muted };
                    _grid.Controls.Add(new Label { Text = "Advice", AutoSize = true, ForeColor = color, Font = Theme.Small, Margin = new Padding(0, 3, 0, 3) });
                    var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
                    host.Controls.Add(new Label { Text = adv.Title + " — " + adv.Text, AutoSize = true, MaximumSize = new Size(TextWidth, 0), ForeColor = color, Font = Theme.Small, Margin = Padding.Empty });
                    if (adv.Repair == RepairKind.Renew && a.Dhcp)
                    {
                        var renew = new LinkLabel { Text = _app.RenewInfo, AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                        renew.LinkClicked += (_, _) => _app.Renew(manual: true);
                        Theme.Apply(renew);
                        host.Controls.Add(renew);
                    }
                    else if (adv.Repair != RepairKind.None)
                    {
                        var text = adv.Repair switch { RepairKind.Release => $"Release lease on {adv.RepairAdapter}", RepairKind.Reset => $"Reset adapter {adv.RepairAdapter}", RepairKind.Prefer => $"Prefer {adv.RepairAdapter} (pin metrics)", RepairKind.SetMtu => $"Set MTU {adv.Value} on {adv.RepairAdapter}", RepairKind.DeleteRoute => $"Delete route {adv.Route?.Prefix} via {adv.Route?.Gateway}", RepairKind.Reach => $"Reach {adv.Target} (profile / preset / temporary address)", _ => adv.Repair.ToString() };
                        var fix = new LinkLabel { Text = text, AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                        var captured = adv; fix.LinkClicked += (_, _) => { if (captured.Repair == RepairKind.Reach && captured.Target is { } target) _app.Reach(target); else _app.Repair(captured); };
                        Theme.Apply(fix);
                        host.Controls.Add(fix);
                    }
                    _grid.Controls.Add(host);
                }
        }
        _grid.ResumeLayout();
        Rendered?.Invoke();
    }
}
