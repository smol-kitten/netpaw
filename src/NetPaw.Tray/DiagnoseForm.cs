using NetPaw.Adapters;
using NetPaw.Connectivity;

namespace NetPaw.Tray;

/// <summary>"Why can one address on this cable reach it and another not?" — one row per check per source address, the
/// verdict lines at the bottom. Nothing is changed; only source-bound pings, one ARP per gateway and one optional TCP connect.</summary>
sealed class DiagnoseForm : Form
{
    readonly TextBox _target = new() { Width = 180, PlaceholderText = "192.168.0.20[:445]" };
    readonly TextBox _via = new() { Width = 150, PlaceholderText = "router far side (opt.)" };
    readonly Button _run = new() { Text = "Diagnose", Width = 100, Height = 28 };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, MultiSelect = false };
    readonly TextBox _verdict = new() { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = Theme.Small };
    readonly TrayApp _app; readonly AdapterInfo _adapter; CancellationTokenSource? _cts;

    public DiagnoseForm(TrayApp app, AdapterInfo adapter, string? target = null)
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
        _app = app; _adapter = adapter;
        Text = $"NetPaw — reachability diagnosis on {adapter.Name}"; ClientSize = new Size(760, 460); StartPosition = FormStartPosition.CenterScreen; MinimizeBox = false; MinimumSize = new Size(560, 320);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
        var hint = new Label { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(6, 7, 0, 0),
            Text = $"from every address on {adapter.Name}: route → gateway ARP → gateway ping → hop 1 → far side → target → TCP. Far side = the router's address in the target's network (splits router policy from the target's return path)." };
        bar.Controls.AddRange([new Label { Text = "Target", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, _target, new Label { Text = "via", AutoSize = true, Padding = new Padding(8, 7, 4, 0) }, _via, _run]);
        _list.Columns.Add("Source", 130); _list.Columns.Add("Check", 110); _list.Columns.Add("", 50); _list.Columns.Add("Detail", 440);
        Controls.Add(_list); Controls.Add(_verdict); Controls.Add(hint); Controls.Add(bar);
        hint.Dock = DockStyle.Top;
        Theme.Apply(this); _verdict.BackColor = Theme.Panel; _verdict.ForeColor = Theme.Text;
        _target.Text = target ?? ""; AcceptButton = _run;
        _run.Click += async (_, _) => await RunAsync();
        FormClosed += (_, _) => _cts?.Cancel();
        if (target is not null) Shown += async (_, _) => await RunAsync();
    }

    async Task RunAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        var spec = _target.Text.Trim(); int? port = null; var host = spec;
        if (PortCheck.TryParse(spec, out var h, out var p)) { host = h; port = p; }
        if (!NetPaw.Net.IpMath.IsIPv4(host)) { _verdict.Text = "Target must be an IPv4 address (optionally :port). Names are a DNS question, not a path question."; return; }
        var via = _via.Text.Trim(); if (via.Length > 0 && !NetPaw.Net.IpMath.IsIPv4(via)) { _verdict.Text = "'via' must be the router's IPv4 address in the target's network."; return; }
        var sources = _adapter.Addresses.Where(a => !a.Address.StartsWith("169.254.")).ToList();
        if (sources.Count == 0) { _verdict.Text = $"{_adapter.Name} has no IPv4 address."; return; }
        _list.Items.Clear(); _verdict.Text = "Running…"; _run.Text = "Stop"; _cts = new CancellationTokenSource();
        try
        {
            var routes = await Task.Run(() => NetPaw.Planning.RouteTable.ReadAll(_app.Service.Runner));
            var prober = new WindowsSourceProber(new NetPaw.Arp.WindowsArpProvider());
            var progress = new Progress<DiagStep>(st => _list.Items.Add(new ListViewItem([st.Source, st.Check, st.Ok switch { true => "ok", false => "FAIL", _ => "" }, st.Detail]) { ForeColor = st.Ok == false ? Theme.Temp : Theme.Text }));
            var rep = await PathDiagnosis.Run(host, port, _adapter, sources, routes, via.Length == 0 ? null : via, prober, progress: progress, ct: _cts.Token);
            _verdict.Text = string.Join(Environment.NewLine, rep.Conclusions);
            _verdict.ForeColor = rep.Sources.All(s => s.Verdict is DiagVerdict.Reachable or DiagVerdict.IcmpFiltered) ? Theme.Static : Theme.Temp;
        }
        catch (OperationCanceledException) { _verdict.Text = "Stopped."; }
        catch (Exception ex) { _verdict.Text = ex.Message; }
        finally { _cts = null; _run.Text = "Diagnose"; }
    }
}
