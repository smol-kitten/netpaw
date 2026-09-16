using NetPaw.Connectivity;

namespace NetPaw.Tray;

/// <summary>Traceroute window: one row per hop as they arrive, and the plain verdict ("path stops after hop 4") at the bottom.</summary>
sealed class TraceForm : Form
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, MultiSelect = false };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 5, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small };
    readonly CancellationTokenSource _cts = new();

    public TraceForm(IProbe probe, string host)
    {
        Text = $"NetPaw — trace to {host}"; ClientSize = new Size(520, 380); StartPosition = FormStartPosition.CenterScreen; MinimizeBox = false;
        _list.Columns.Add("#", 40); _list.Columns.Add("Hop", 170); _list.Columns.Add("Round-trips", 220); _list.Columns.Add("", 60);
        Controls.Add(_list); Controls.Add(_status);
        Theme.Apply(this);
        _status.Text = "Tracing… three ICMP probes per hop, TTL 1 upwards.";
        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) =>
        {
            var progress = new Progress<Hop>(h => _list.Items.Add(new ListViewItem([h.Ttl.ToString(), h.Address ?? "*", h.MsText, ""]) { ForeColor = h.Silent ? Theme.Muted : Theme.Text }));
            try
            {
                var r = await Trace.Run(probe, host, progress: progress, ct: _cts.Token);
                _status.Text = r.Summary; _status.ForeColor = r.Reached ? Theme.Static : Theme.Temp;
                if (_list.Items.Count > 0 && r.Reached) _list.Items[^1].ForeColor = Theme.Static;
            }
            catch (OperationCanceledException) { }
        };
    }
}
