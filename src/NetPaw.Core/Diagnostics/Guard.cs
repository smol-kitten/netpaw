namespace NetPaw.Diagnostics;

/// <summary>
/// Every background task runs through here: a failure is logged with its operation name and reported (telemetry
/// build) with context, and it never propagates into the UI loop. Cancellation is silent by design.
/// </summary>
public static class Guard
{
    public static async Task Run(string op, Func<Task> work, Action<string> log, Action<Exception, string>? report = null)
    {
        try { await work(); }
        catch (OperationCanceledException) { log($"{op}: cancelled"); }
        catch (Exception ex)
        {
            log($"{op}: {ex.GetType().Name}: {ex.Message}");
            try { report?.Invoke(ex, op); } catch (Exception rex) { log($"{op}: report failed: {rex.Message}"); }
        }
    }

    public static async Task<T?> Run<T>(string op, Func<Task<T>> work, Action<string> log, Action<Exception, string>? report = null, T? fallback = default)
    {
        try { return await work(); }
        catch (OperationCanceledException) { log($"{op}: cancelled"); return fallback; }
        catch (Exception ex)
        {
            log($"{op}: {ex.GetType().Name}: {ex.Message}");
            try { report?.Invoke(ex, op); } catch (Exception rex) { log($"{op}: report failed: {rex.Message}"); }
            return fallback;
        }
    }

    /// <summary>What travels with an error report besides the exception: enough to see what the app was doing.</summary>
    public static Dictionary<string, object?> Context(string op, string? state, string? adapter, string version, TimeSpan uptime, IReadOnlyList<string> recentLog) => new()
    {
        ["where"] = op, ["state"] = state, ["adapter"] = adapter, ["version"] = version,
        ["uptime_s"] = (long)uptime.TotalSeconds, ["log"] = string.Join("\n", recentLog),
    };
}
