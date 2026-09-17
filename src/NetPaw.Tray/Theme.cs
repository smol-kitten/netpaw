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
            case Form f:
                f.BackColor = Bg; f.ForeColor = Text;
                if (f.ShowIcon) f.Icon = Icons.App;                       // every window: the paw, not the WinForms default
                if (f.IsHandleCreated) Native.Dress(f); else f.HandleCreated += (_, _) => Native.Dress(f);
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Field; b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Selection; b.FlatAppearance.MouseDownBackColor = Accent;
                b.Cursor = Cursors.Hand; break;
            case TextBox t: t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; break;
            case NumericUpDown n: n.BackColor = Field; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle; foreach (Control part in n.Controls) { part.BackColor = Field; part.ForeColor = Text; } break;
            case DarkComboBox: break;
            case ComboBox cb: cb.BackColor = Field; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat; break;
            case ListBox lb: lb.BackColor = Panel; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None; break;
            case ListView lv: DarkListView(lv); break;
            case DarkTabControl: break;
            case CheckBox cb:
                // Flat = the box is drawn with ForeColor/BackColor instead of the light system glyph.
                // The box interior is BackColor (opaque, else it falls back to white) and CheckedBackColor; the tick is ForeColor.
                cb.ForeColor = Text; cb.BackColor = Bg; cb.FlatStyle = FlatStyle.Flat; cb.FlatAppearance.BorderSize = 0; cb.FlatAppearance.CheckedBackColor = Accent; cb.FlatAppearance.MouseOverBackColor = Bg; cb.FlatAppearance.MouseDownBackColor = Bg; break;
            case RadioButton rb: rb.ForeColor = Text; rb.BackColor = Bg; rb.FlatStyle = FlatStyle.Flat; rb.FlatAppearance.BorderSize = 0; rb.FlatAppearance.CheckedBackColor = Accent; break;
            case LinkLabel ll: ll.LinkColor = Accent; ll.ActiveLinkColor = Text; ll.VisitedLinkColor = Accent; ll.LinkBehavior = LinkBehavior.HoverUnderline; ll.BackColor = Color.Transparent; break;
            case Label l: if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text; l.BackColor = Color.Transparent; break;
            case GroupBox g: g.ForeColor = Muted; break;
            case System.Windows.Forms.Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer: if (c.BackColor == SystemColors.Control) c.BackColor = Bg; break;
        }
    }

    /// <summary>
    /// WinForms paints ListView column headers with the light system theme whatever the colours say. Owner-draw the
    /// header only; rows keep the default painting. Group headers cannot be owner-drawn at all, so callers use
    /// <see cref="SeparatorItem"/> rows instead of <see cref="ListViewGroup"/>s.
    /// </summary>
    static void DarkListView(ListView lv)
    {
        lv.BackColor = Panel; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.None;
        if (lv.OwnerDraw) return;
        void Scrollbars() { try { Native.SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
        if (lv.IsHandleCreated) Scrollbars(); else lv.HandleCreated += (_, _) => Scrollbars();
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Field); e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Border); e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1); e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Small, Rectangle.Inflate(e.Bounds, -6, 0), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (_, e) =>
        {
            if (e.Item.Tag is not SeparatorTag) { e.DrawDefault = true; return; }
            using var bg = new SolidBrush(Bg); e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Item.Text, new Font(Small, FontStyle.Bold), Rectangle.Inflate(e.Bounds, -4, 0), Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        lv.DrawSubItem += (_, e) => { e.DrawDefault = e.Item.Tag is not SeparatorTag; };
        lv.ItemSelectionChanged += (_, e) => { if (e.Item?.Tag is SeparatorTag && e.IsSelected) e.Item.Selected = false; };
    }

    /// <summary>
    /// TabControl paints its strip background and the 3D page frame in system colours whatever the properties say
    /// (owner-draw covers the tabs only). UserPaint takes the whole control: dark strip, accent underline, 1 px border.
    /// </summary>
    public sealed class DarkTabControl : TabControl
    {
        public DarkTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SizeMode = TabSizeMode.Fixed; ItemSize = new Size(120, 28); Padding = new Point(12, 4);
        }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is TabPage p) { p.BackColor = Theme.Bg; p.ForeColor = Theme.Text; p.UseVisualStyleBackColor = false; p.BorderStyle = BorderStyle.None; }
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Bg);
            using var border = new Pen(Theme.Border);
            for (var i = 0; i < TabCount; i++)
            {
                var r = GetTabRect(i); var selected = i == SelectedIndex;
                using var bg = new SolidBrush(selected ? Theme.Panel : Theme.Bg); g.FillRectangle(bg, r);
                if (selected) { using var accent = new SolidBrush(Theme.Accent); g.FillRectangle(accent, r.Left, r.Bottom - 2, r.Width, 2); }
                TextRenderer.DrawText(g, TabPages[i].Text, selected ? new Font(Theme.Base, FontStyle.Bold) : Theme.Base, r, selected ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            var page = DisplayRectangle; g.DrawRectangle(border, page.Left - 1, page.Top - 1, page.Width + 1, page.Height + 1);
        }
    }

    /// <summary>
    /// ComboBox draws its arrow button and border with system colours even in FlatStyle.Flat. Items are owner-drawn;
    /// the frame and arrow are painted over the control right after each WM_PAINT.
    /// </summary>
    public sealed class DarkComboBox : ComboBox
    {
        const int WM_PAINT = 0x000F;
        public DarkComboBox() { FlatStyle = FlatStyle.Flat; BackColor = Theme.Field; ForeColor = Theme.Text; DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? Theme.Selection : Theme.Field); e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -2, 0), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT || !IsHandleCreated) return;
            using var g = Graphics.FromHwnd(Handle);
            var button = new Rectangle(Width - 18, 1, 17, Height - 2);
            using var bg = new SolidBrush(Theme.Field); g.FillRectangle(bg, button);
            using var pen = new Pen(Theme.Border); g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var arrow = new SolidBrush(Theme.Muted); var cx = button.Left + button.Width / 2; var cy = button.Top + button.Height / 2;
            g.FillPolygon(arrow, [new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3)]);
        }
    }

    sealed class SeparatorTag { public static readonly SeparatorTag Instance = new(); }
    /// <summary>A full-width dark heading row (the replacement for a ListViewGroup header).</summary>
    public static ListViewItem SeparatorItem(string text) => new(text) { Tag = SeparatorTag.Instance };

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
