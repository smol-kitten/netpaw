using System.Diagnostics;
using NetPaw.Hotkeys;

namespace NetPaw.Tray;

sealed class SettingsForm : Form
{
    public SettingsForm(TrayApp app)
    {
        var s = app.Service.Settings;
        Text = "NetPaw — settings"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ClientSize = new Size(460, 372);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 8, 0, 8) }); c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Margin = new Padding(0, 5, 0, 5); grid.Controls.Add(c); }

        var adapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        adapter.Items.Add("(auto-detect)");
        foreach (var a in app.Service.GetAdapters()) adapter.Items.Add(a.Name);
        adapter.SelectedIndex = s.WorkAdapter is null ? 0 : Math.Max(0, adapter.Items.IndexOf(s.WorkAdapter));
        var hotkey = new TextBox { Text = s.PanelHotkey };
        var confirm = new CheckBox { Text = "show the command plan before applying", Checked = s.ConfirmBeforeApply, AutoSize = true };
        var notify = new CheckBox { Text = "balloon notifications", Checked = s.ShowNotifications, AutoSize = true };
        var secondary = new CheckBox { Text = "reach mode adds a secondary address (keeps current config)", Checked = s.ReachAsSecondary, AutoSize = true };
        var prefix = new NumericUpDown { Minimum = 8, Maximum = 30, Value = s.ReachDefaultPrefix, Width = 70 };
        var startup = new CheckBox { Text = "start with Windows (elevated scheduled task)", Checked = Startup.IsEnabled(), AutoSize = true };

        Row("Work adapter", adapter);
        Row("Panel hotkey", hotkey);
        Row("Confirm", confirm);
        Row("Notifications", notify);
        Row("Reach mode", secondary);
        Row("Reach default prefix", prefix);
        Row("Startup", startup);
        var folder = new Button { Text = "Open config folder", Width = 150, Height = 28 };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo(app.Service.Store.Directory) { UseShellExecute = true });
        Row("Files", folder);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Text = "Save", Width = 110, Height = 30 };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        var err = new Label { AutoSize = true, ForeColor = Theme.Error, Margin = new Padding(0, 8, 12, 0) };
        bar.Controls.AddRange([ok, cancel, err]);
        ok.Click += (_, _) =>
        {
            if (!HotkeyParser.TryParse(hotkey.Text, out var hk)) { err.Text = "hotkey needs a modifier, e.g. Ctrl+Alt+N"; return; }
            s.WorkAdapter = adapter.SelectedIndex <= 0 ? null : (string)adapter.SelectedItem!;
            s.PanelHotkey = hk.ToString(); s.ConfirmBeforeApply = confirm.Checked; s.ShowNotifications = notify.Checked;
            s.ReachAsSecondary = secondary.Checked; s.ReachDefaultPrefix = (int)prefix.Value;
            app.Service.SaveSettings();
            var startupErr = Startup.Set(startup.Checked);
            if (startupErr is not null) { err.Text = startupErr; return; }
            app.RegisterHotkeys(); app.RefreshState();
            DialogResult = DialogResult.OK; Close();
        };
        Controls.Add(grid); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this); Theme.Primary(ok);
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
            ? Run($"/create /f /tn {TaskName} /sc onlogon /rl highest /tr \"\\\"{exe}\\\"\" /ru \"{Environment.UserDomainName}\\{Environment.UserName}\"")
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
