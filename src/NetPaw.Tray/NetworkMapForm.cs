using NetPaw.Adapters;
using NetPaw.Arp;
using NetPaw.Model;

namespace NetPaw.Tray;

/// <summary>
/// Two views of "who is on this cable": the ARP neighbour cache bundled per network (passive, with a
/// refresh and an active re-check/sweep), and the router finder that borrows an address in each common
/// subnet and ARPs the usual gateway addresses. Both are explicit actions — nothing runs on its own.
/// </summary>
sealed class NetworkMapForm : Form
{
    readonly TrayApp _app;
    readonly AdapterInfo _adapter;
    readonly IArpProvider _arp = new WindowsArpProvider();
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly ListView _hosts = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, ShowGroups = true };
    readonly ListView _routers = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None };
    readonly ListView _switch = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None };
    readonly Button _listen = new() { Text = "Listen 35 s", Width = 110, Height = 28 }, _listen65 = new() { Text = "Listen 65 s (CDP)", Width = 130, Height = 28 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 4, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Button _refresh = new() { Text = "Refresh cache", Width = 120, Height = 28 }, _check = new() { Text = "Re-check (ARP)", Width = 130, Height = 28 }, _sweep = new() { Text = "Sweep subnet", Width = 120, Height = 28 };
    readonly Button _find = new() { Text = "Find routers", Width = 130, Height = 28 }, _copy = new() { Text = "Copy", Width = 70, Height = 28 };
    CancellationTokenSource? _cts;

    public NetworkMapForm(TrayApp app, AdapterInfo adapter)
    {
        _app = app; _adapter = adapter;
        Text = $"NetPaw — network map on {adapter.Name}"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(820, 520); MinimumSize = new Size(640, 400);
        foreach (var (h, w) in new[] { ("Address", 140), ("MAC", 150), ("Kind", 80), ("Alive", 90), ("Note", 300) }) _hosts.Columns.Add(h, w);
        foreach (var (h, w) in new[] { ("Network", 140), ("Responder", 130), ("MAC", 150), ("Round-trip", 80), ("Likely vendor(s)", 280) }) _routers.Columns.Add(h, w);
        foreach (var lv in new[] { _hosts, _routers }) { lv.BackColor = Theme.Panel; lv.ForeColor = Theme.Text; lv.Font = Theme.Base; }
        var t1 = new TabPage("Neighbours (ARP)") { BackColor = Theme.Bg }; var t2 = new TabPage("Find routers") { BackColor = Theme.Bg }; var t3 = new TabPage("Switch (LLDP/CDP)") { BackColor = Theme.Bg };
        foreach (var (h, w) in new[] { ("Field", 160), ("Value", 560) }) _switch.Columns.Add(h, w);
        _switch.BackColor = Theme.Panel; _switch.ForeColor = Theme.Text; _switch.Font = Theme.Base;
        var bar3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
        var hint3 = new Label { Text = NetPaw.Discovery.PktmonCapture.Available ? "Passive: Windows' pktmon captures the switch's LLDP (every 30 s) / CDP (every 60 s) announcements. Nothing is sent." : "pktmon is not available on this Windows version (needs 10 2004+ / 11).", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(8, 8, 0, 0) };
        bar3.Controls.AddRange([_listen, _listen65, hint3]);
        _listen.Enabled = _listen65.Enabled = NetPaw.Discovery.PktmonCapture.Available;
        t3.Controls.Add(_switch); t3.Controls.Add(bar3);
        _listen.Click += async (_, _) => await ListenSwitch(35);
        _listen65.Click += async (_, _) => await ListenSwitch(65);
        var bar1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) }; bar1.Controls.AddRange([_refresh, _check, _sweep, _copy]);
        var bar2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
        var hint = new Label { Text = "Borrows a temporary address in each common subnet, ARPs .1/.254 and vendor defaults, then removes it. ~1-2 s per subnet.", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(8, 8, 0, 0) };
        bar2.Controls.AddRange([_find, hint]);
        t1.Controls.Add(_hosts); t1.Controls.Add(bar1); t2.Controls.Add(_routers); t2.Controls.Add(bar2);
        _tabs.TabPages.Add(t1); _tabs.TabPages.Add(t2); _tabs.TabPages.Add(t3);
        Controls.Add(_tabs); Controls.Add(_status);
        _refresh.Click += (_, _) => { if (_cts is not null) _cts.Cancel(); else LoadCache(); };
        _check.Click += async (_, _) => await Recheck(sweep: false);
        _sweep.Click += async (_, _) => await Recheck(sweep: true);
        _find.Click += async (_, _) => await FindRouters();
        _copy.Click += (_, _) => Clipboard.SetText(string.Join("\r\n", _hosts.Items.Cast<ListViewItem>().Select(i => string.Join("\t", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)))));
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F1) _app.ShowHelp("network-map"); };
        FormClosing += (_, _) => _cts?.Cancel();
        Theme.Apply(this); Theme.Primary(_find);
        Load += (_, _) => { Native.Dress(this); Native.ForceForeground(Handle); LoadCache(); };
    }

    void LoadCache()
    {
        var entries = _arp.ReadCache();
        var bundles = ArpTable.Bundle(entries, _app.Service.GetAdapters());
        Render(bundles);
        _status.Text = $"{bundles.Sum(b => b.Hosts.Count)} neighbour(s) in {bundles.Count} network(s) from the ARP cache (passive). Re-check ARPs each one; Sweep asks every address of the adapter's subnet.";
    }

    void Render(IEnumerable<NetworkBundle> bundles)
    {
        _hosts.BeginUpdate(); _hosts.Items.Clear(); _hosts.Groups.Clear();
        foreach (var b in bundles)
        {
            var g = new ListViewGroup(b.Network == "other" ? "other (not in a local subnet)" : $"{b.Cidr}  ·  {b.Adapter}  ·  you are {b.OwnAddress}" + (b.Hosts.Any(h => h.Alive is not null) ? $"  ·  {b.Alive} alive" : ""));
            _hosts.Groups.Add(g);
            foreach (var h in b.Hosts)
            {
                var alive = h.Alive switch { true => $"✓ {h.Ms} ms", false => "✗", null => "—" };
                var note = _app.Service.Presets.FirstOrDefault(p => p.Ip == h.Ip) is { } pr ? $"default address of {pr.Vendor} {pr.Model}" : _adapter.Gateways.Contains(h.Ip) ? "default gateway" : "";
                _hosts.Items.Add(new ListViewItem([h.Ip, h.Mac, h.Kind, alive, note]) { Group = g, ForeColor = h.Alive switch { true => Theme.Static, false => Theme.Error, null => Theme.Text } });
            }
        }
        _hosts.EndUpdate();
    }

    async Task Recheck(bool sweep)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        var live = _app.Service.GetAdapters().FirstOrDefault(a => a.Name == _adapter.Name) ?? _adapter;
        var own = live.Addresses.FirstOrDefault(a => !a.Address.StartsWith("169.254."));
        if (own is null) { _status.Text = "The adapter has no usable address."; return; }
        var targets = sweep ? ArpTable.SweepTargets(own).ToList() : ArpTable.Bundle(_arp.ReadCache(), [live]).SelectMany(b => b.Hosts.Select(h => h.Ip)).Distinct().ToList();
        if (targets.Count == 0) { _status.Text = "Nothing to check."; return; }
        _cts = new CancellationTokenSource(); _check.Enabled = _sweep.Enabled = false; _refresh.Text = "Cancel";
        try
        {
            var prog = new Progress<int>(n => _status.Text = $"{(sweep ? "Sweeping" : "Re-checking")} {own.Network}/{own.PrefixLength}: {n}/{targets.Count}…");
            var alive = await ArpReachability.Check(_arp, targets, own.Address, 32, prog, _cts.Token);
            var aliveSet = alive.ToDictionary(a => a.Ip);
            var cache = _arp.ReadCache().Where(e => !e.IsBroadcastOrMulticast).ToList();
            var merged = targets.Select(ip => aliveSet.TryGetValue(ip, out var a) ? a with { Kind = cache.FirstOrDefault(c => c.Ip == ip)?.Kind ?? "probe" } : (cache.FirstOrDefault(c => c.Ip == ip) is { } c2 ? c2 with { Alive = false } : null)).Where(e => e is not null).Select(e => e!).ToList();
            Render(ArpTable.Bundle(merged, [live], hideNoise: true));
            _status.Text = $"{alive.Count} of {targets.Count} answered ARP on {own.Network}/{own.PrefixLength}.";
            TelemetryHost.Event("map", sweep ? "sweep" : "recheck", null, alive.Count);
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        finally { _cts = null; _check.Enabled = _sweep.Enabled = true; _refresh.Text = "Refresh cache"; }
    }

    async Task ListenSwitch(int seconds)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource(); _listen.Text = "Cancel"; _listen65.Enabled = false;
        _switch.Items.Clear();
        try
        {
            var prog = new Progress<int>(n => _status.Text = $"Listening for LLDP/CDP on {_adapter.Name}: {n}/{seconds} s…");
            var (found, err) = await new NetPaw.Discovery.PktmonCapture().Listen(_adapter.Name, seconds, prog, _app.Service.Store.Log, _cts.Token);
            if (err is not null) { _status.Text = err; return; }
            _app.SetSwitchNeighbors(_adapter.Name, found);
            foreach (var n in found)
            {
                void Row(string k, string? v) { if (!string.IsNullOrEmpty(v)) _switch.Items.Add(new ListViewItem([k, v]) { ForeColor = k == "Switch" ? Theme.Static : Theme.Text }); }
                Row("Switch", $"{n.SystemName ?? n.ChassisId}  ({n.Protocol})"); Row("Port", n.PortId); Row("Port description", n.PortDescription); Row("VLAN", n.PortVlan?.ToString());
                Row("Management", n.ManagementAddress); Row("System", n.SystemDescription); Row("Capabilities", string.Join(", ", n.Capabilities)); Row("Chassis", n.ChassisId); Row("Seen", n.SeenAt.ToString("HH:mm:ss"));
                _switch.Items.Add(new ListViewItem(["", ""]));
            }
            _status.Text = found.Count == 0 ? $"No LLDP/CDP announcement in {seconds} s. Causes: the switch does not send on this port, an unmanaged switch, or this NIC driver only passes the LLDP multicast group once an LLDP agent registered it (see Help)." : $"{found.Count} announcement(s) — {found[0].Headline}";
            TelemetryHost.Event("map", "lldp", found.FirstOrDefault()?.Protocol, found.Count);
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        finally { _cts = null; _listen.Text = "Listen 35 s"; _listen65.Enabled = NetPaw.Discovery.PktmonCapture.Available; }
    }

    async Task FindRouters()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        if (!_app.Service.Allowed(Capability.TempAddresses)) { _status.Text = "Temporary addresses are disabled by policy; the router finder needs them."; return; }
        _routers.Items.Clear();
        _cts = new CancellationTokenSource(); _find.Text = "Cancel";
        var svc = _app.Service;
        var restores = new Dictionary<string, Action>();
        var finder = new RouterFinder(
            async (addr, ct) => { var (ok, restore) = await Task.Run(() => svc.Borrow(_adapter.Name, addr), ct); if (ok) { restores[addr.ToString()] = restore; await Task.Delay(300, ct); } return ok; },
            async (addr, _) => { if (restores.Remove(addr.ToString(), out var r)) await Task.Run(r); },
            _arp);
        var cands = RouterFinder.Candidates(svc.Presets);
        try
        {
            var prog = new Progress<(int Index, int Total, RouterCandidate Candidate, RouterHit? Hit)>(p =>
            {
                _status.Text = $"Probing {p.Candidate.Cidr} ({p.Index + 1}/{p.Total})…";
                if (p.Hit is { } h) _routers.Items.Add(new ListViewItem([h.Candidate.Cidr, h.Ip, h.Mac, h.Ms + " ms", string.Join(", ", h.Vendors)]) { ForeColor = Theme.Static });
            });
            var hits = await finder.Find(cands, svc.GetAdapters(), _adapter.Name, prog, _cts.Token);
            _status.Text = hits.Count == 0 ? $"No router answered in {cands.Count} common subnets. The port may be a trunk, isolated, or on an uncommon range." : $"{hits.Count} responder(s) found — type one of the addresses in the panel to reach it.";
            TelemetryHost.Event("map", "find-routers", null, hits.Count);
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled; borrowed addresses removed."; }
        finally { _cts = null; _find.Text = "Find routers"; _app.RefreshState(); }
    }
}
