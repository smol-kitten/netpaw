using System.Diagnostics;

namespace NetPaw.Planning;

public interface IStepRunner
{
    StepResult Run(Step step);
}

/// <summary>Runs a step as a hidden child process and captures its output for the log.</summary>
public sealed class ProcessStepRunner : IStepRunner
{
    public StepResult Run(Step step)
    {
        var psi = new ProcessStartInfo(step.FileName, step.Arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return new StepResult(step, -1, "timeout after 30 s"); }
            var text = (stdout.Result + stderr.Result).Trim();
            return new StepResult(step, proc.ExitCode, text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new StepResult(step, -1, ex.Message);
        }
    }
}

public sealed class ApplyOutcome
{
    public List<StepResult> Results { get; } = [];
    public bool Success => Results.All(r => r.Ok || !r.Step.Critical);
    public IEnumerable<StepResult> Failures => Results.Where(r => !r.Ok);
    public bool Aborted { get; set; }
}

public static class PlanExecutor
{
    /// <summary>Runs steps in order; a failed critical step aborts (later steps would only pile on).</summary>
    public static ApplyOutcome Execute(ApplyPlan plan, IStepRunner runner, Action<string>? log = null)
    {
        var outcome = new ApplyOutcome();
        log?.Invoke($"== apply '{plan.Title}' on '{plan.Adapter}' ({plan.Steps.Count} steps)");
        foreach (var w in plan.Warnings) log?.Invoke("   warn: " + w);
        foreach (var step in plan.Steps)
        {
            var r = runner.Run(step);
            outcome.Results.Add(r);
            log?.Invoke($"   [{(r.Ok ? "ok" : "FAIL " + r.ExitCode)}] {step.CommandLine}{(string.IsNullOrEmpty(r.Output) ? "" : " -> " + r.Output.ReplaceLineEndings(" | "))}");
            if (!r.Ok && step.Critical) { outcome.Aborted = true; log?.Invoke("   aborted: critical step failed"); break; }
        }
        return outcome;
    }
}
