namespace NetPaw.Tray;

/// <summary>
/// A titled group of label/field rows with a thin rule, optionally collapsible. Every field can
/// show an inline error under itself, which reads better than one error list at the bottom.
/// </summary>
sealed class SectionPanel : Panel
{
    readonly TableLayoutPanel _grid = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(0, Theme.Spacing.Xs, 0, Theme.Spacing.S) };
    readonly Label _title;
    readonly Dictionary<string, Label> _errors = [];
    bool _collapsed;

    public SectionPanel(string title, bool collapsible = false, bool collapsed = false)
    {
        Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(0, 0, 0, Theme.Spacing.S);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _title = new Label { Text = title, Font = Theme.Section, ForeColor = Theme.Muted, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, Theme.Spacing.S, 0, 2), Cursor = collapsible ? Cursors.Hand : Cursors.Default };
        var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border, Margin = Padding.Empty };
        Controls.Add(_grid); Controls.Add(rule); Controls.Add(_title);
        if (collapsible)
        {
            _title.Click += (_, _) => Collapsed = !Collapsed;
            Collapsed = collapsed;
        }
        else _title.Text = title;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => _collapsed;
        set { _collapsed = value; _grid.Visible = !value; _title.Text = (value ? "▸  " : "▾  ") + _title.Text.TrimStart('▸', '▾', ' '); }
    }

    /// <summary>Adds a row. <paramref name="key"/> names the field for <see cref="SetError"/>; <paramref name="hint"/> is grey help text under the control.</summary>
    public T Row<T>(string label, T control, string? key = null, string? hint = null, int? width = null) where T : Control
    {
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, Theme.Spacing.S + 1, 0, 0) });
        var host = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        control.Margin = new Padding(0, Theme.Spacing.Xs, 0, 0);
        if (width is { } w) control.Width = w; else if (control is not FlowLayoutPanel) control.Width = 560;
        host.Controls.Add(control);
        if (hint is not null) host.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) });
        var err = new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Error, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0), Visible = false };
        host.Controls.Add(err);
        if (key is not null) _errors[key] = err;
        _grid.Controls.Add(host);
        return control;
    }

    public void SetError(string key, string? message)
    {
        if (!_errors.TryGetValue(key, out var l)) return;
        l.Text = message ?? ""; l.Visible = message is not null;
        if (message is not null && Collapsed) Collapsed = false;
    }

    public void ClearErrors() { foreach (var l in _errors.Values) { l.Visible = false; l.Text = ""; } }
}
