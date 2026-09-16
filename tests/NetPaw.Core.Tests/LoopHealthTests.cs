using NetPaw.Connectivity;
using NetPaw.Diagnostics;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class LoopHealthTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CadenceBacksOffWhenStable()
    {
        var s = new CheckSettings { IntervalSeconds = 30, MaxIntervalSeconds = 120 };
        var quiet = T0.AddMinutes(-10);                                                       // last change long ago
        Assert.Equal(30, Cadence.Next(false, false, quiet, T0, 0, s));                       // first round: fast
        Assert.Equal(60, Cadence.Next(false, false, quiet, T0, 30, s));
        Assert.Equal(120, Cadence.Next(false, false, quiet, T0, 60, s));
        Assert.Equal(120, Cadence.Next(false, false, quiet, T0, 120, s));                    // ceiling
        Assert.Equal(30, Cadence.Next(false, false, T0.AddMinutes(-2), T0, 60, s));           // inside the 5 min settle window
        Assert.Equal(30, Cadence.Next(false, false, quiet, T0, 60, new CheckSettings { IntervalSeconds = 30, MaxIntervalSeconds = 120, MonitorMode = true }));
        Assert.Equal(45, Cadence.Next(false, false, quiet, T0, 45, new CheckSettings { IntervalSeconds = 45, MaxIntervalSeconds = 10 }));   // max below fast: fast wins
    }

    [Fact]
    public void CadenceReturnsToFastOnProblemOrChange()
    {
        var s = new CheckSettings { IntervalSeconds = 30, MaxIntervalSeconds = 120 };
        var quiet = T0.AddMinutes(-10);
        Assert.Equal(30, Cadence.Next(true, false, quiet, T0, 120, s));
        Assert.Equal(30, Cadence.Next(false, true, quiet, T0, 120, s));
        Assert.Equal(TimeSpan.FromSeconds(60), Cadence.Deadline(30)); Assert.Equal(TimeSpan.FromSeconds(20), Cadence.Deadline(5));
        Assert.False(Cadence.Stale(T0, T0.AddSeconds(89), 30)); Assert.True(Cadence.Stale(T0, T0.AddSeconds(91), 30));
    }

    [Fact]
    public void LogRingKeepsLast40()
    {
        var dir = Directory.CreateTempSubdirectory("netpaw-ring");
        try
        {
            var store = new JsonStore(dir.FullName);
            for (var i = 1; i <= 50; i++) store.Log($"line {i}");
            var recent = store.Recent;
            Assert.Equal(40, recent.Count); Assert.EndsWith("line 11", recent[0]); Assert.EndsWith("line 50", recent[^1]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} line 50$", recent[^1]);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task GuardedNeverThrowsAndReportsWithContext()
    {
        var log = new List<string>(); (Exception Ex, string Op)? reported = null;
        await Guard.Run("boom", () => throw new InvalidOperationException("nope"), log.Add, (ex, op) => reported = (ex, op));
        Assert.Equal("boom", reported!.Value.Op); Assert.Equal("nope", reported.Value.Ex.Message); Assert.Contains("boom: InvalidOperationException: nope", log);
        await Guard.Run("cancelled", () => throw new OperationCanceledException(), log.Add, (_, _) => throw new Exception("must not be called"));
        Assert.Contains("cancelled: cancelled", log);
        await Guard.Run("reporter-dies", () => throw new Exception("x"), log.Add, (_, _) => throw new Exception("reporter"));
        Assert.Contains(log, l => l.Contains("report failed: reporter"));
        Assert.Equal(7, await Guard.Run("value", () => Task.FromResult(7), log.Add));
        Assert.Equal(-1, await Guard.Run("value-fails", () => Task.FromException<int>(new Exception("x")), log.Add, fallback: -1));
        var ctx = Guard.Context("check", "Online", "Ethernet", "0.10.0", TimeSpan.FromMinutes(90), ["a", "b"]);
        Assert.Equal("check", ctx["where"]); Assert.Equal("Online", ctx["state"]); Assert.Equal(5400L, ctx["uptime_s"]); Assert.Equal("a\nb", ctx["log"]);
    }
}
