using System.Diagnostics;
using System.Net.Http;
using NetPaw.Hotkeys;

namespace NetPaw.Tray;

sealed class SettingsForm : Form
{
    public SettingsForm(TrayApp app)
    {
        var s = app.Service.Settings; var pol = app.Service.Policy;
        Text = "NetPaw — settings"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ClientSize = new Size(700, 720);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoSize = true, AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 8, 0, 8) }); c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Margin = new Padding(0, 5, 0, 5); grid.Controls.Add(c); }

        var adapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        adapter.Items.Add("(auto-detect)");
        foreach (var a in app.Service.GetAdapters()) adapter.Items.Add(a.Name);
        adapter.SelectedIndex = s.WorkAdapter is null ? 0 : Math.Max(0, adapter.Items.IndexOf(s.WorkAdapter));
        var hotkey = new TextBox { Text = s.PanelHotkey };
        var infoHotkey = new TextBox { Text = s.InfoHotkey };
        var confirm = new CheckBox { Text = "show the command plan before applying", Checked = s.ConfirmBeforeApply, AutoSize = true };
        var notify = new CheckBox { Text = "balloon notifications", Checked = s.ShowNotifications, AutoSize = true };
        var secondary = new CheckBox { Text = "add a secondary address (keeps current config)", Checked = s.ReachAsSecondary, AutoSize = true };
        var prefix = new NumericUpDown { Minimum = 8, Maximum = 30, Value = s.ReachDefaultPrefix, Width = 70 };
        var startup = new CheckBox { Text = "start with Windows (elevated task, no UAC prompt)", Checked = Startup.IsEnabled(), AutoSize = true };

        Row("Work adapter", adapter); adapter.Enabled = !pol.IsSet("WorkAdapter");
        Row("Panel hotkey", hotkey); hotkey.Enabled = !pol.IsSet("PanelHotkey");
        Row("Info hotkey", infoHotkey);
        Row("Confirm", confirm); confirm.Enabled = !pol.IsSet("ConfirmBeforeApply");
        if (pol.Any) Row("Policy", new Label { Text = "Greyed values are set by your organisation.", AutoSize = true, ForeColor = Theme.Temp, Font = Theme.Small });
        Row("Notifications", notify);
        Row("Reach mode", secondary);
        Row("Reach default prefix", prefix);
        Row("Startup", startup);
        // ---- connectivity -------------------------------------------------------------------
        var ck = s.Checks;
        var checksOn = new CheckBox { Text = "check reachability (gateway, intranet, internet, DNS)", Checked = ck.Enabled, AutoSize = true };
        var interval = new NumericUpDown { Minimum = 5, Maximum = 3600, Value = Math.Clamp(ck.IntervalSeconds, 5, 3600), Width = 70 };
        var intranet = new TextBox { Text = string.Join(" ", ck.IntranetTargets), PlaceholderText = "extra intranet hosts, e.g. 10.0.0.53 fileserver" };
        var internet = new TextBox { Text = string.Join(" ", ck.InternetTargets) };
        var dnsHost = new TextBox { Text = ck.DnsCheckHost, PlaceholderText = "empty = no DNS check" };
        var monitor = new CheckBox { Text = "monitor mode: notify when the state changes (internet lost, link down, back online)", Checked = ck.MonitorMode, AutoSize = true };
        var sticky = new CheckBox { Text = "keep the info card on screen while there is a problem", Checked = ck.StickyAlerts, AutoSize = true };
        void ToggleChecks() { foreach (Control c in new Control[] { interval, intranet, internet, dnsHost, monitor }) c.Enabled = checksOn.Checked; }
        checksOn.CheckedChanged += (_, _) => ToggleChecks(); ToggleChecks();
        var intervalRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        intervalRow.Controls.Add(interval); intervalRow.Controls.Add(new Label { Text = "seconds between rounds", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(6, 6, 0, 0) });
        Row("Connectivity", checksOn);
        Row("Interval", intervalRow);
        Row("Intranet hosts", intranet);
        Row("Internet targets", internet);
        Row("DNS check host", dnsHost);
        Row("Monitor", monitor);
        Row("Sticky alert", sticky);

        CheckBox? telemetry = null;
        if (TelemetryHost.IsTelemetryBuild)
        {
            telemetry = new CheckBox { Text = "send crash reports and anonymous usage counts to telemetry.catboy.systems", Checked = s.TelemetryEnabled, AutoSize = true, Enabled = !pol.IsSet("AllowTelemetry") };
            var what = new LinkLabel { Text = "what is sent (docs/TELEMETRY.md)", AutoSize = true, Font = Theme.Small };
            what.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/smol-kitten/netpaw/blob/main/docs/TELEMETRY.md") { UseShellExecute = true });
            var box = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            box.Controls.Add(telemetry); box.Controls.Add(what);
            Row("Telemetry", box);
        }

        var folder = new Button { Text = "Open config folder", Width = 150, Height = 28 };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo(app.Service.Store.Directory) { UseShellExecute = true });
        Row("Files", folder);

        // ---- repositories -------------------------------------------------------------------
        var repos = new ListBox { Height = 96, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        repos.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || repos.Items[e.Index] is not NetPaw.Repos.RepoState st) return;
            var sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Theme.Selection : Theme.Field)) e.Graphics.FillRectangle(bg, e.Bounds);
            var color = st.Trust switch { NetPaw.Repos.TrustState.Verified => Theme.Static, NetPaw.Repos.TrustState.Unsigned => Theme.Temp, _ => Theme.Error };
            using (var dot = new SolidBrush(color)) e.Graphics.FillEllipse(dot, e.Bounds.X + 8, e.Bounds.Y + 11, 8, 8);
            var right = e.Bounds.Right - 6;
            if (st.Repo.FromPolicy) Theme.DrawBadge(e.Graphics, "policy", Theme.Temp, ref right, e.Bounds.Y + 2, 16);
            var sub = st.Pack is null ? st.Error ?? "not synced" : $"{st.Count} entries · {st.Trust.ToString().ToLowerInvariant()}{(st.Fetched is { } f ? " · " + f.ToString("dd.MM HH:mm") : "")}{(st.Error is null ? "" : " · " + st.Error)}";
            TextRenderer.DrawText(e.Graphics, st.Name + "  " + st.Repo.Url, Theme.Small, new Rectangle(e.Bounds.X + 22, e.Bounds.Y + 1, right - e.Bounds.X - 26, 14), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, sub, Theme.Small, new Rectangle(e.Bounds.X + 22, e.Bounds.Y + 15, e.Bounds.Width - 26, 14), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        void FillRepos() { repos.Items.Clear(); foreach (var st in app.Service.RepoStates) repos.Items.Add(st); }
        FillRepos();
        var canEdit = app.Service.Allowed(Capability.UserRepos);
        var url = new TextBox { PlaceholderText = "https://…/index.json  or  url|keyId:publicKey", Width = 296, Enabled = canEdit };
        var add = new Button { Text = "Add", Width = 52, Height = 26, Enabled = canEdit };
        var remove = new Button { Text = "Remove", Width = 66, Height = 26, Enabled = canEdit };
        var sync = new Button { Text = "Sync now", Width = 78, Height = 26 };
        var repoErr = new Label { AutoSize = true, ForeColor = Theme.Error, Font = Theme.Small, MaximumSize = new Size(510, 0) };
        add.Click += (_, _) => { try { app.Service.AddRepo(url.Text); url.Text = ""; repoErr.Text = ""; FillRepos(); } catch (InvalidOperationException ex) { repoErr.Text = ex.Message; } };
        remove.Click += (_, _) => { if (repos.SelectedItem is NetPaw.Repos.RepoState st) try { app.Service.RemoveRepo(st.Repo.Url); repoErr.Text = ""; FillRepos(); } catch (InvalidOperationException ex) { repoErr.Text = ex.Message; } };
        sync.Click += async (_, _) =>
        {
            sync.Enabled = false; sync.Text = "Syncing…"; repoErr.Text = "";
            try { var states = await app.Service.SyncRepos(); FillRepos(); repoErr.ForeColor = states.All(x => x.Pack is not null) ? Theme.Static : Theme.Error; repoErr.Text = string.Join("; ", states.Select(x => $"{x.Name}: {(x.Pack is null ? x.Error : x.Count + " entries")}")); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { repoErr.ForeColor = Theme.Error; repoErr.Text = ex.Message; }
            finally { sync.Enabled = true; sync.Text = "Sync now"; app.RefreshState(); }
        };
        var repoBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in new Control[] { url, add, remove, sync }) { c.Margin = new Padding(0, 0, 4, 0); repoBar.Controls.Add(c); }
        var repoBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        repos.Width = 510; repoBox.Controls.Add(repos); repoBox.Controls.Add(repoBar); repoBox.Controls.Add(repoErr);
        Row("Repositories", repoBox);
        if (!canEdit) Row("", new Label { Text = "Repositories are set by your organisation.", AutoSize = true, ForeColor = Theme.Temp, Font = Theme.Small });

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Text = "Save", Width = 110, Height = 30 };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        var err = new Label { AutoSize = true, ForeColor = Theme.Error, Margin = new Padding(0, 8, 12, 0) };
        bar.Controls.AddRange([ok, cancel, err]);
        ok.Click += (_, _) =>
        {
            if (!HotkeyParser.TryParse(hotkey.Text, out var hk)) { err.Text = "hotkey needs a modifier, e.g. Ctrl+Alt+N"; return; }
            if (!HotkeyParser.TryParse(infoHotkey.Text, out var ik)) { err.Text = "info hotkey needs a modifier, e.g. Ctrl+Alt+I"; return; }
            if (hk.ToString() == ik.ToString()) { err.Text = "panel and info hotkeys must differ"; return; }
            static List<string> Hosts(string t) => t.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            s.InfoHotkey = ik.ToString();
            ck.Enabled = checksOn.Checked; ck.IntervalSeconds = (int)interval.Value; ck.IntranetTargets = Hosts(intranet.Text); ck.InternetTargets = Hosts(internet.Text);
            ck.DnsCheckHost = dnsHost.Text.Trim(); ck.MonitorMode = monitor.Checked; ck.StickyAlerts = sticky.Checked;
            if (telemetry is not null) { s.TelemetryEnabled = telemetry.Checked; TelemetryHost.Refresh(app.Service); }
            if (!pol.IsSet("WorkAdapter")) s.WorkAdapter = adapter.SelectedIndex <= 0 ? null : (string)adapter.SelectedItem!;
            if (!pol.IsSet("PanelHotkey")) s.PanelHotkey = hk.ToString();
            if (!pol.IsSet("ConfirmBeforeApply")) s.ConfirmBeforeApply = confirm.Checked;
            s.ShowNotifications = notify.Checked;
            s.ReachAsSecondary = secondary.Checked; s.ReachDefaultPrefix = (int)prefix.Value;
            app.Service.SaveSettings();
            var startupErr = Startup.Set(startup.Checked);
            if (startupErr is not null) { err.Text = startupErr; return; }
            app.RegisterHotkeys(); app.ConfigureChecks(); app.RefreshState(); _ = app.RunCheck();
            DialogResult = DialogResult.OK; Close();
        };
        Controls.Add(grid); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this); Theme.Primary(ok);
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) { app.ShowHelp("settings"); e.Handled = true; } };
        Load += (_, _) => Native.Dress(this);
    }
}

/// <summary>"Run at logon" through a highest-privilege scheduled task — the only way to autostart an elevated app without a UAC prompt each login.</summary>
static class Startup
{
    const string TaskName = "NetPaw";

    public static bool IsEnabled() => Run($"/query /tn {TaskName}") == 0;

    public static string? Set(bool enable)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var code = enable
            ? Run($"/create /f /tn {TaskName} /sc onlogon /rl highest /tr \"\\\"{exe}\\\"\"")   // no /ru: defaults to the current user, verified on Win11
            : IsEnabled() ? Run($"/delete /f /tn {TaskName}") : 0;
        return code == 0 ? null : $"schtasks failed (exit {code})";
    }

    static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
    }
}
