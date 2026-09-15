using NetPaw.Planning;

namespace NetPaw.Tray;

/// <summary>Shows exactly which commands will run. Used for "confirm before apply" and whenever a plan carries warnings.</summary>
sealed class PlanPreviewForm : Form
{
    PlanPreviewForm(ApplyPlan plan, string? subtitle)
    {
        Text = $"NetPaw — apply '{plan.Title}' on {plan.Adapter}";
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(720, 380); MinimizeBox = false; ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable; MinimumSize = new Size(520, 260);
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = Theme.Mono };
        var lines = new List<string>();
        if (subtitle is not null) { lines.Add(subtitle); lines.Add(""); }
        foreach (var w in plan.Warnings) lines.Add("⚠ " + w);
        if (plan.Warnings.Count > 0) lines.Add("");
        foreach (var s in plan.Steps) lines.Add($"{(s.Critical ? "*" : " ")} {s.Description}\r\n    {s.CommandLine}");
        lines.Add(""); lines.Add("* = critical: a failure here aborts the remaining steps.");
        text.Text = string.Join("\r\n", lines);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var apply = new Button { Text = plan.IsEmpty ? "Nothing to do" : "Apply", Width = 120, Height = 30, DialogResult = DialogResult.OK, Enabled = !plan.IsEmpty };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        var copy = new Button { Text = "Copy commands", Width = 130, Height = 30 };
        copy.Click += (_, _) => Clipboard.SetText(string.Join("\r\n", plan.Steps.Select(s => s.CommandLine)));
        bar.Controls.AddRange([apply, cancel, copy]);
        Controls.Add(text); Controls.Add(bar);
        AcceptButton = apply; CancelButton = cancel;
        Theme.Apply(this); Theme.Primary(apply);
        Load += (_, _) => { Native.Dress(this); text.Select(0, 0); };
    }

    public static bool Confirm(ApplyPlan plan, string? subtitle = null)
    {
        using var f = new PlanPreviewForm(plan, subtitle);
        return f.ShowDialog() == DialogResult.OK;
    }
}
