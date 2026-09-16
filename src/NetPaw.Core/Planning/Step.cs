namespace NetPaw.Planning;

/// <summary>One external command in an apply plan. Shown verbatim in "preview" so admins see exactly what will run.</summary>
/// <param name="ExpectFailure">A failure is normal (e.g. deleting something that may not exist) and is not reported.</param>
public sealed record Step(string Description, string FileName, string Arguments, bool Critical = true, bool ExpectFailure = false)
{
    public string CommandLine => $"{FileName} {Arguments}";

    public static Step Netsh(string description, string args, bool critical = true, bool expectFailure = false) => new(description, "netsh", args, critical, expectFailure);

    public static Step PowerShell(string description, string command, bool critical = true) =>
        new(description, "powershell", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"", critical);
}

public sealed record StepResult(Step Step, int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0 || Step.ExpectFailure;
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
