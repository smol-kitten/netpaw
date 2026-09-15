using System.Reflection;

namespace NetPaw.Tray;

/// <summary>
/// F1 help: topics on the left, the current view's topic opens first. Content is embedded markdown
/// (docs/help/*.md) rendered with a tiny subset: #/## headings, - bullets, `code`, **bold**, blank lines.
/// </summary>
sealed class HelpForm : Form
{
    static readonly (string Id, string Title)[] Topics =
    [
        ("overview", "Overview"), ("quick-panel", "Quick panel"), ("reach", "Reach mode"), ("profiles", "Profiles"),
        ("presets-repos", "Presets & repositories"), ("network-info", "Network info & monitor"), ("settings", "Settings"),
        ("enterprise", "Enterprise"), ("cli", "Command line"), ("troubleshooting", "Troubleshooting"),
    ];

    readonly ListBox _nav = new() { Dock = DockStyle.Left, Width = 190, BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30 };
    readonly RichTextBox _body = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true };
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "search help…", Margin = Padding.Empty };
    readonly Stack<string> _history = new();
    static readonly Dictionary<string, string> Cache = [];

    public HelpForm()
    {
        Text = "NetPaw — help (F1)"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(860, 600); MinimumSize = new Size(640, 400);
        var left = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Theme.Panel, Padding = new Padding(0, 6, 0, 0) };
        foreach (var t in Topics) _nav.Items.Add(t);
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return; var (_, title) = ((string, string))_nav.Items[e.Index];
            var sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Theme.Selection : Theme.Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, title, Theme.Base, new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), sel ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        _nav.SelectedIndexChanged += (_, _) => { if (_nav.SelectedItem is (string id, string)) Render(id); };
        _search.TextChanged += (_, _) => Filter();
        left.Controls.Add(_nav); left.Controls.Add(_search);
        _body.BackColor = Theme.Bg; _body.ForeColor = Theme.Text; _body.Font = Theme.Base;
        _body.LinkClicked += (_, e) => { if (e.LinkText is { } l && l.StartsWith("help:")) Open(l[5..]); else if (e.LinkText is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText) { UseShellExecute = true }); };
        var bodyHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 16), BackColor = Theme.Bg };
        bodyHost.Controls.Add(_body);
        Controls.Add(bodyHost); Controls.Add(left);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyData == (Keys.Alt | Keys.Left) && _history.Count > 1) { _history.Pop(); Open(_history.Pop()); } };
        Theme.Apply(this);
        Load += (_, _) => { Native.Dress(this); _search.BackColor = Theme.Field; _search.ForeColor = Theme.Text; };
    }

    public void Open(string topic)
    {
        var i = Array.FindIndex(Topics, t => t.Id == topic);
        if (i < 0) i = 0;
        _search.Text = "";
        if (_nav.SelectedIndex == i) Render(Topics[i].Id); else _nav.SelectedIndex = i;
    }

    void Filter()
    {
        var q = _search.Text.Trim();
        _nav.Items.Clear();
        foreach (var t in Topics) if (q.Length == 0 || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || LoadTopic(t.Id).Contains(q, StringComparison.OrdinalIgnoreCase)) _nav.Items.Add(t);
        if (_nav.Items.Count > 0 && _nav.SelectedIndex < 0) _nav.SelectedIndex = 0;
    }

    static string LoadTopic(string id)
    {
        if (Cache.TryGetValue(id, out var c)) return c;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("help/" + id + ".md");
        var text = s is null ? $"# {id}\n\nNo help written yet." : new StreamReader(s).ReadToEnd();
        return Cache[id] = text;
    }

    void Render(string id)
    {
        _history.Push(id);
        _body.Clear();
        var h1 = new Font("Segoe UI Semibold", 16f); var h2 = new Font("Segoe UI Semibold", 11.5f); var mono = Theme.Mono; var bold = new Font(Theme.Base, FontStyle.Bold);
        foreach (var raw in LoadTopic(id).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ")) { Append(line[2..] + "\n", h1, Theme.Text); Append("\n", Theme.Small, Theme.Text); continue; }
            if (line.StartsWith("## ")) { Append("\n" + line[3..] + "\n", h2, Theme.Accent); continue; }
            if (line.StartsWith("- ")) { Append("   •  ", Theme.Base, Theme.Muted); Inline(line[2..], mono, bold); Append("\n", Theme.Base, Theme.Text); continue; }
            if (line.Length == 0) { Append("\n", Theme.Small, Theme.Text); continue; }
            Inline(line, mono, bold); Append("\n", Theme.Base, Theme.Text);
        }
        _body.Select(0, 0); _body.ScrollToCaret();
    }

    void Inline(string text, Font mono, Font bold)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`') { var j = text.IndexOf('`', i + 1); if (j > i) { Append(text[(i + 1)..j], mono, Theme.Temp); i = j + 1; continue; } }
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*') { var j = text.IndexOf("**", i + 2, StringComparison.Ordinal); if (j > i) { Append(text[(i + 2)..j], bold, Theme.Text); i = j + 2; continue; } }
            var next = text.IndexOfAny(['`', '*'], i + 1); if (next < 0) next = text.Length;
            Append(text[i..next], Theme.Base, Theme.Text); i = next;
        }
    }

    void Append(string s, Font f, Color c) { _body.SelectionStart = _body.TextLength; _body.SelectionFont = f; _body.SelectionColor = c; _body.AppendText(s); }
}
