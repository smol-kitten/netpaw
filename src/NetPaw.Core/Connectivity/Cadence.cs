namespace NetPaw.Connectivity;

/// <summary>
/// How long until the next check round. Fast (the configured interval) while something is wrong, right after a
/// change, in monitor mode, and for five minutes after the last change; otherwise doubles up to the maximum
/// (30 → 60 → 120 s). Link and address events bypass this entirely — they trigger a round at once.
/// </summary>
public static class Cadence
{
    public static readonly TimeSpan SettleWindow = TimeSpan.FromMinutes(5);

    /// <param name="last">Seconds the previous round waited (0 = first round).</param>
    public static int Next(bool problem, bool changed, DateTimeOffset lastChange, DateTimeOffset now, int last, CheckSettings s)
    {
        var fast = Math.Clamp(s.IntervalSeconds, 5, 3600);
        var max = Math.Max(fast, s.MaxIntervalSeconds);
        if (problem || changed || s.MonitorMode || now - lastChange < SettleWindow || last <= 0) return fast;
        return Math.Min(max, last * 2);
    }

    /// <summary>Watchdog bound: a round older than this is stuck (a probe that ignores its timeout, a hung netsh).</summary>
    public static TimeSpan Deadline(int currentIntervalSeconds) => TimeSpan.FromSeconds(Math.Max(20, currentIntervalSeconds * 2));

    /// <summary>The card says "last check N min ago" once a snapshot is older than three intervals.</summary>
    public static bool Stale(DateTimeOffset snapshotAt, DateTimeOffset now, int currentIntervalSeconds) => now - snapshotAt > TimeSpan.FromSeconds(currentIntervalSeconds * 3);
}
