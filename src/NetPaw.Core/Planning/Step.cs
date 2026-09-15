namespace NetPaw.Planning;

/// <summary>One external command in an apply plan. Shown verbatim in "preview" so admins see exactly what will run.</summary>
public sealed record Step(string Description, string FileName, string Arguments, bool Critical = true)
{
    public string CommandLine => $"{FileName} {Arguments}";

    public static Step Netsh(string description, string args, bool critical = true) => new(description, "netsh", args, critical);

    public static Step PowerShell(string description, string command, bool critical = true) =>
        new(description, "powershell", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"", critical);
}

public sealed record StepResult(Step Step, int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

public sealed class ApplyPlan
{
    public string Title { get; init; } = "";
    public string Adapter { get; init; } = "";
    public List<Step> Steps { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsEmpty => Steps.Count == 0;
    public override string ToString() => string.Join(Environment.NewLine, Steps.Select(s => "  " + s.CommandLine));
}
