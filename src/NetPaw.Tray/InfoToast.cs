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
    readonly InfoCardPanel _card;
    readonly Button _pin = new() { Text = "📌", Width = 28, Height = 22, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Emoji", 9f), Cursor = Cursors.Hand, TabStop = false };
    readonly Button _close = new() { Text = "✕", Width = 24, Height = 22, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TabStop = false };
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 6000 };
    bool _pinned, _autoPinned;

    public bool Pinned => _pinned;

    public InfoToast(TrayApp app)
    {
        _app = app; _card = new InfoCardPanel(app) { Dock = DockStyle.Fill };
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(340, 200); BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Base; DoubleBuffered = true;
        foreach (var b in new[] { _pin, _close }) { b.FlatAppearance.BorderSize = 0; b.BackColor = Theme.Panel; b.ForeColor = Theme.Muted; b.FlatAppearance.MouseOverBackColor = Theme.Selection; }
        _pin.Click += (_, _) => SetPinned(!_pinned, manual: true);
        _close.Click += (_, _) => { _autoPinned = false; SetPinned(false, manual: true); Hide(); };
        Controls.Add(_card); Controls.Add(_state); Controls.Add(_title); Controls.Add(_pin); Controls.Add(_close);
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
        _card.Render(s);
        _title.Text = _card.TitleText; _state.Text = _card.StateText; _state.ForeColor = _card.StateColor;
        var h = _title.Height + _state.Height + _card.PreferredHeight + 8;
        if (ClientSize.Height != h) { ClientSize = new Size(ClientSize.Width, h); var area = Screen.PrimaryScreen!.WorkingArea; Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12); try { Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 10, 10)); } catch { } }
        _pin.Location = new Point(Width - 58, 4); _close.Location = new Point(Width - 28, 4);
    }
}
