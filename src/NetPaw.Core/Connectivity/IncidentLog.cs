using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPaw.Connectivity;

/// <summary>One timestamped line in incidents.jsonl: a state change or a configuration finding on an adapter.</summary>
public sealed record Incident(DateTimeOffset At, string Adapter, string Kind, string From, string To, string Text, IReadOnlyList<string> Advisories)
{
    public override string ToString() =>
        $"{At:yyyy-MM-dd HH:mm:ss} {Adapter,-14} {Kind,-9} {From} -> {To}: {Text}{(Advisories.Count == 0 ? "" : "  [" + string.Join("; ", Advisories) + "]")}";
}

/// <summary>
/// Optional append-only record of "when was the network not right" — state transitions and
/// configuration findings with timestamps, one JSON object per line so it survives any crash and
/// can be grepped or fed to a ticket. Off by default; nothing is written until enabled.
/// </summary>
public sealed class IncidentLog(string file)
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string File { get; } = file;
    readonly object _lock = new();

    public void Append(Incident i)
    {
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.AppendAllText(File, JsonSerializer.Serialize(i, Json) + Environment.NewLine);
        }
    }

    public IReadOnlyList<Incident> Read(int last = 200)
    {
        if (!System.IO.File.Exists(File)) return [];
        var lines = System.IO.File.ReadAllLines(File);
        var list = new List<Incident>();
        foreach (var l in lines.Skip(Math.Max(0, lines.Length - last)))
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            try { if (JsonSerializer.Deserialize<Incident>(l, Json) is { } i) list.Add(i); } catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Decides what is worth a line: a state change, or the set of findings changing. Returns null when nothing changed.</summary>
    public static Incident? FromChange(Snapshot snap, Transitions.Change? change, IReadOnlyList<Advisory> advisories, IReadOnlyList<string> previousAdvisories)
    {
        var titles = advisories.Select(a => a.Title).ToList();
        var name = snap.Adapter?.Name ?? "adapter";
        if (change is not null)
            return new Incident(snap.At, name, change.IsProblem ? "problem" : "recovery", change.From.ToString(), change.To.ToString(), change.Text, titles);
        if (!titles.SequenceEqual(previousAdvisories) && (titles.Count > 0 || previousAdvisories.Count > 0))
            return new Incident(snap.At, name, titles.Count == 0 ? "config-ok" : "config", snap.State.ToString(), snap.State.ToString(),
                titles.Count == 0 ? "configuration findings cleared" : string.Join("; ", advisories.Select(a => a.Title + " — " + a.Text)), titles);
        return null;
    }
}
