using System.Drawing.Drawing2D;

namespace NetPaw.Tray;

/// <summary>One small dark palette; every form styles itself through <see cref="Apply"/> so the look stays consistent.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 26, 32);
    public static readonly Color Panel = Color.FromArgb(32, 35, 43);
    public static readonly Color Field = Color.FromArgb(40, 44, 54);
    public static readonly Color Border = Color.FromArgb(58, 63, 76);
    public static readonly Color Text = Color.FromArgb(230, 232, 238);
    public static readonly Color Muted = Color.FromArgb(140, 146, 160);
    public static readonly Color Accent = Color.FromArgb(120, 170, 255);
    public static readonly Color Selection = Color.FromArgb(46, 66, 104);
    public static readonly Color Dhcp = Color.FromArgb(79, 195, 247);
    public static readonly Color Static = Color.FromArgb(102, 187, 106);
    public static readonly Color Temp = Color.FromArgb(255, 167, 38);
    public static readonly Color Down = Color.FromArgb(120, 120, 130);
    public static readonly Color Busy = Color.FromArgb(171, 130, 255);
    public static readonly Color Error = Color.FromArgb(239, 83, 80);

    public static readonly Font Base = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Big = new("Segoe UI", 13f);
    public static readonly Font Mono = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Section = new("Segoe UI Semibold", 9f);

    /// <summary>The only spacing values the UI uses, so everything lines up.</summary>
    public static class Spacing { public const int Xs = 4, S = 8, M = 12, L = 16; }

    /// <summary>Small pill drawn behind a word: "managed", "repo", "temp", "✓ current".</summary>
    public static void DrawBadge(Graphics g, string text, Color color, ref int rightEdge, int y, int height)
    {
        var size = TextRenderer.MeasureText(g, text, Small, Size.Empty, TextFormatFlags.NoPadding);
        var w = size.Width + 12; var h = Math.Min(height, size.Height + 4);
        var r = new Rectangle(rightEdge - w, y + (height - h) / 2, w, h);
        using var path = RoundRect(r, h / 2);
        using var fill = new SolidBrush(Color.FromArgb(48, color));
        using var pen = new Pen(Color.FromArgb(140, color));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(fill, path); g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, Small, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        rightEdge = r.Left - 6;
    }

    public static void Apply(Control root)
    {
        Style(root);
        foreach (Control c in root.Controls) Apply(c);
    }

    static void Style(Control c)
    {
        c.Font = c.Font.Name == Base.Name && c.Font.Size == Base.Size ? c.Font : c switch { Label l when l.Font.Bold => c.Font, _ => Base };
        switch (c)
        {
            case Form f: f.BackColor = Bg; f.ForeColor = Text; break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Field; b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Selection; b.FlatAppearance.MouseDownBackColor = Accent;
                b.Cursor = Cursors.Hand; break;
            case TextBox t: t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; break;
            case NumericUpDown n: n.BackColor = Field; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle; break;
            case ComboBox cb: cb.BackColor = Field; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat; break;
            case ListBox lb: lb.BackColor = Panel; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None; break;
            case CheckBox or RadioButton: c.ForeColor = Text; c.BackColor = Color.Transparent; break;
            case LinkLabel ll: ll.LinkColor = Accent; ll.ActiveLinkColor = Text; ll.VisitedLinkColor = Accent; ll.LinkBehavior = LinkBehavior.HoverUnderline; ll.BackColor = Color.Transparent; break;
            case Label l: if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text; l.BackColor = Color.Transparent; break;
            case GroupBox g: g.ForeColor = Muted; break;
            case System.Windows.Forms.Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer: if (c.BackColor == SystemColors.Control) c.BackColor = Bg; break;
        }
    }

    public static Button Primary(Button b) { b.BackColor = Accent; b.ForeColor = Bg; b.FlatAppearance.BorderColor = Accent; b.Font = new Font(Base, FontStyle.Bold); return b; }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

/// <summary>Dark ContextMenuStrip renderer (WinForms' own dark mode leaves menus light in places).</summary>
sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var r = new Rectangle(Point.Empty, e.Item.Size);
        using var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.Selection : Theme.Panel);
        e.Graphics.FillRectangle(b, r);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? (e.Item.Tag as Color?) ?? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var p = new Pen(Theme.Static, 2f);
        var r = e.ImageRectangle; var cx = r.X + r.Width / 2; var cy = r.Y + r.Height / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(p, [new Point(cx - 5, cy), new Point(cx - 1, cy + 4), new Point(cx + 6, cy - 4)]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 30, e.Item.Height / 2, e.Item.Width - 4, e.Item.Height / 2);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Selection;
        public override Color MenuItemSelected => Theme.Selection;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
