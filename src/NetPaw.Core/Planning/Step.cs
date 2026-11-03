namespace NetPaw.Planning;

/// <summary>One external command in an apply plan. Shown verbatim in "preview" so admins see exactly what will run.
/// <see cref="FileName"/> is set only by planner code (<see cref="Netsh"/>, <see cref="PowerShell"/> and the explicit
/// ipconfig steps); no policy, profile or IPC field ever reaches it. The launcher maps it to a System32 path
/// (<see cref="SystemTool"/>).</summary>
/// <param name="ExpectFailure">A failure is normal (e.g. deleting something that may not exist) and is not reported.</param>
/// <param name="NoopOutput">Output fragments (case-insensitive) that mean "already in the requested state": a non-zero exit
/// whose output contains one counts as success. netsh exits 1 for that case exactly like for a real error, so the text is
/// the only signal; the list carries the locales measured so far and a miss falls back to the old (failure) behaviour.</param>
public sealed record Step(string Description, string FileName, string Arguments, bool Critical = true, bool ExpectFailure = false, string[]? NoopOutput = null)
{
    public string CommandLine => $"{FileName} {Arguments}";

    public static Step Netsh(string description, string args, bool critical = true, bool expectFailure = false, string[]? noopOutput = null) => new(description, "netsh", args, critical, expectFailure, noopOutput);

    public static Step PowerShell(string description, string command, bool critical = true) =>
        new(description, "powershell", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"", critical);
}

public sealed record StepResult(Step Step, int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0 || Step.ExpectFailure || AlreadyDone;
    /// <summary>The command failed only because the adapter was already in the requested state (<see cref="Step.NoopOutput"/>).</summary>
    public bool AlreadyDone => ExitCode != 0 && Step.NoopOutput is { } m && m.Any(f => Output.Contains(f, StringComparison.OrdinalIgnoreCase));
}

public sealed class ApplyPlan
{
    public string Title { get; init; } = "";
    public string Adapter { get; init; } = "";
    public List<Step> Steps { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>The profile this plan applies, when it applies one — lets the service verify afterwards.</summary>
    public Model.Profile? Profile { get; set; }
    public bool IsEmpty => Steps.Count == 0;
    /// <summary>True when a step changes DNS servers or (re)negotiates a lease — the cases where a stale resolver cache bites.</summary>
    public bool ChangesDns => Steps.Any(s => s.Arguments.Contains("dnsservers") || s.Arguments.Contains("source=dhcp") || (s.FileName == "ipconfig" && (s.Arguments.StartsWith("/renew") || s.Arguments.StartsWith("/release"))) || s.Arguments.Contains("admin=enable"));
    public override string ToString() => string.Join(Environment.NewLine, Steps.Select(s => "  " + s.CommandLine));
}
