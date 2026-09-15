using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Scan;

namespace NetPaw.Tray;

/// <summary>
/// "Find me a working profile": applies candidates one by one, measures each, and either stops at
/// the first fully working one or ranks them all. The original configuration is captured first and
/// restored unless the user keeps a result. Progress is visible per row; Cancel restores.
/// </summary>
sealed class ScanForm : Form
{
    readonly TrayApp _app;
    readonly AdapterInfo _adapter;
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, MultiSelect = false };
    readonly RadioButton _first = new() { Text = "stop at the first fully working profile", AutoSize = true, Checked = true }, _all = new() { Text = "try all and rank by usability", AutoSize = true };
    readonly CheckBox _dhcp = new() { Text = "include DHCP as a candidate", AutoSize = true };
    readonly Button _start = new() { Text = "Start scan", Width = 120, Height = 30 }, _keep = new() { Text = "Keep selected", Width = 130, Height = 30, Enabled = false }, _close = new() { Text = "Restore && close", Width = 130, Height = 30 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 4, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Dictionary<string, ListViewItem> _rows = [];
    CancellationTokenSource? _cts;
    Profile? _original;
    bool _kept;

    public ScanForm(TrayApp app, AdapterInfo adapter)
    {
        _app = app; _adapter = adapter;
        Text = $"NetPaw — scan for a working profile on {adapter.Name}"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(760, 460); MinimumSize = new Size(640, 360);
        _dhcp.Checked = app.Service.Settings.Repair.ScanIncludeDhcp && app.Service.Allowed(Capability.Dhcp);
        _dhcp.Enabled = app.Service.Allowed(Capability.Dhcp);
        foreach (var (h, w) in new[] { ("Profile", 200), ("Verdict", 150), ("Score", 60), ("Gateway", 110), ("Internet", 110), ("DNS", 90) }) _list.Columns.Add(h, w);
        _list.BackColor = Theme.Panel; _list.ForeColor = Theme.Text; _list.Font = Theme.Base;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12, 8, 12, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty }; modes.Controls.Add(_first); modes.Controls.Add(_all);
        _first.Margin = new Padding(0, 0, 16, 0);
        top.Controls.Add(modes); top.Controls.Add(_dhcp);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bar.Controls.AddRange([_keep, _start, _close]);
        Controls.Add(_list); Controls.Add(_status); Controls.Add(bar); Controls.Add(top);
        _start.Click += async (_, _) => await Scan();
        _keep.Click += (_, _) => Keep();
        _close.Click += (_, _) => Close();
        _list.SelectedIndexChanged += (_, _) => _keep.Enabled = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ScanResult;
        FormClosing += async (_, e) =>
        {
            if (_cts is { IsCancellationRequested: false }) { _cts.Cancel(); }
            if (!_kept && _original is not null) { e.Cancel = true; await Restore(); _original = null; Close(); }
        };
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F1) _app.ShowHelp("network-info"); };
        Theme.Apply(this); Theme.Primary(_start); AcceptButton = _start;
        Load += (_, _) => { Native.Dress(this); Fill(); Native.ForceForeground(Handle); _start.Focus(); };
    }

    void Fill()
    {
        _list.Items.Clear(); _rows.Clear();
        foreach (var c in NetworkScanner.Candidates(_app.Service.Profiles.Concat(_app.Service.RepoProfiles), _adapter, _dhcp.Checked))
        {
            var it = new ListViewItem([c.Name + (c.Managed ? "  (managed)" : c.Source is not null ? $"  ({c.Source})" : ""), "", "", "", "", ""]) { Tag = c, ForeColor = Theme.Muted };
            _list.Items.Add(it); _rows[c.Id] = it;
        }
        _status.Text = $"{_list.Items.Count} candidate(s). The current configuration is restored unless you keep a result.";
    }

    async Task Scan()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        Fill();
        _original ??= _app.Service.Capture("scan-original", _adapter);
        _cts = new CancellationTokenSource();
        _start.Text = "Cancel"; _first.Enabled = _all.Enabled = _dhcp.Enabled = false; _keep.Enabled = false;
        var checks = new CheckSettings { Enabled = true, InternetTargets = _app.Service.Settings.Checks.InternetTargets, DnsCheckHost = _app.Service.Settings.Checks.DnsCheckHost, TimeoutMs = 1000 };
        var checker = new ConnectivityChecker(new NetworkProbe());
        var scanner = new NetworkScanner(async (p, ct) =>
        {
            var plan = _app.Service.PlanProfile(p, _adapter);
            await Task.Run(() => _app.Service.Apply(plan), ct);
            // Static settles in ~2 s; DHCP needs a lease — poll up to 12 s for an address.
            var deadline = DateTime.UtcNow.AddSeconds(p.Dhcp ? 12 : 2);
            AdapterInfo live;
            do { await Task.Delay(1000, ct); live = _app.Service.GetAdapters().First(a => a.Name == _adapter.Name); }
            while (DateTime.UtcNow < deadline && (!live.Up || live.Addresses.All(a => a.Address.StartsWith("169.254."))));
            return await checker.Check(live, checks, ct);
        });
        var candidates = _list.Items.Cast<ListViewItem>().Select(i => (Profile)i.Tag!).ToList();
        var progress = new Progress<ScanProgress>(pr =>
        {
            var row = _rows[pr.Candidate.Id];
            if (pr.Result is null) { row.SubItems[1].Text = "trying…"; row.ForeColor = Theme.Accent; _status.Text = $"Trying {pr.Candidate.Name} ({pr.Index + 1}/{pr.Total})…"; return; }
            var s = pr.Result.Snapshot;
            row.SubItems[1].Text = pr.Result.Verdict; row.SubItems[2].Text = pr.Result.Score.ToString();
            row.SubItems[3].Text = s.Intranet.FirstOrDefault() is { } g ? (g.Ok ? $"✓ {g.Ms} ms" : "✗") : "—";
            row.SubItems[4].Text = s.Internet.Count == 0 ? "—" : s.InternetOk ? $"✓ {s.Internet.First(p => p.Ok).Ms} ms" : "✗";
            row.SubItems[5].Text = s.DnsCheck is null ? "—" : s.DnsOk ? "✓" : "✗";
            row.ForeColor = pr.Result.FullyWorking ? Theme.Static : pr.Result.Working ? Theme.Temp : Theme.Error;
            row.Tag = pr.Result;
        });
        try
        {
            var results = await scanner.Run(candidates, _all.Checked ? ScanMode.RankAll : ScanMode.FirstWorking, progress, _cts.Token);
            // Re-order rows by rank.
            _list.BeginUpdate();
            foreach (var (r, i) in results.Select((r, i) => (r, i))) { var row = _rows[r.Candidate.Id]; _list.Items.Remove(row); _list.Items.Insert(i, row); }
            _list.EndUpdate();
            var best = results.FirstOrDefault();
            _status.Text = best is null ? "No candidates." : best.FullyWorking ? $"Best: {best.Candidate.Name} — online. Keep it or restore." : best.Working ? $"Best: {best.Candidate.Name} — gateway answers, no internet." : "Nothing worked on this port. Restore keeps your previous configuration.";
            if (best is not null) { _rows[best.Candidate.Id].Selected = true; _keep.Enabled = true; }
            TelemetryHost.Event("scan", _all.Checked ? "rank-all" : "first-working", best?.Verdict, results.Count);
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled — restoring the previous configuration…"; await Restore(); _original = null; _status.Text = "Cancelled; previous configuration restored."; }
        finally { _cts = null; _start.Text = "Start scan"; _first.Enabled = _all.Enabled = _dhcp.Enabled = true; }
    }

    void Keep()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not ScanResult r) return;
        _kept = true; _original = null;
        _app.Service.Store.Log($"scan: kept '{r.Candidate.Name}' on {_adapter.Name} ({r.Verdict})");
        // The adapter already runs it if it was the last one tried; otherwise apply again.
        _app.ApplyProfile(r.Candidate);
        Close();
    }

    async Task Restore()
    {
        if (_original is null) return;
        var live = _app.Service.GetAdapters().FirstOrDefault(a => a.Name == _adapter.Name) ?? _adapter;
        var plan = _app.Service.PlanProfile(_original, live);
        await Task.Run(() => _app.Service.Apply(plan));
        _app.RefreshState();
    }
}
