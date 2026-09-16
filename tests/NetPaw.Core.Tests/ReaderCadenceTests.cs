using NetPaw.Adapters;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class ReaderCadenceTests
{
    [Fact]
    public async Task ReadersRunOnlyWhenDue()
    {
        var probe = new FakeProbe(); var wlanCalls = 0; var dot1xCalls = 0;
        var checker = new ConnectivityChecker(probe)
        {
            WlanReader = _ => { wlanCalls++; return new WlanInfo("Wi-Fi", "Corp", "aa:bb:cc:00:00:01", 70); },
            Dot1xReader = _ => { dot1xCalls++; return new Dot1xInfo("Ethernet", "Authenticating"); },
        };
        var wifi = Fx.Nic("Wi-Fi", wireless: true, dhcp: true, addrs: ["10.0.0.5/24"], gw: "10.0.0.1");
        var s = new CheckSettings { Enabled = true };
        Assert.NotNull((await checker.Check(wifi, s, readers: true)).Wlan); Assert.Equal(1, wlanCalls);
        Assert.Null((await checker.Check(wifi, s, readers: false)).Wlan); Assert.Equal(1, wlanCalls);        // skipped: caller copies the previous value
        var wired = Fx.Nic(dhcp: true, addrs: ["169.254.1.1/16"], gw: null);
        Assert.NotNull((await checker.Check(wired, s, readers: true)).Dot1x); Assert.Equal(1, dot1xCalls);
        Assert.Null((await checker.Check(wired, s, readers: false)).Dot1x); Assert.Equal(1, dot1xCalls);
    }

    [Fact]
    public void LogLevelGatesVerbose()
    {
        var dir = Directory.CreateTempSubdirectory("netpaw-loglevel");
        try
        {
            var store = new JsonStore(dir.FullName) { MinLevel = LogLevel.Normal };
            store.Verbose("check", "round 1"); store.Log("apply", "profile x"); store.Error("update", "boom");
            var lines = File.ReadAllLines(store.LogFile);
            Assert.Equal(2, lines.Length); Assert.EndsWith("apply: profile x", lines[0]); Assert.EndsWith("update: boom", lines[1]);
            store.MinLevel = LogLevel.Errors; store.Log("apply", "hidden"); store.Error("apply", "shown");
            Assert.EndsWith("apply: shown", File.ReadAllLines(store.LogFile)[^1]); Assert.Equal(3, File.ReadAllLines(store.LogFile).Length);
            store.MinLevel = LogLevel.Verbose; store.Verbose("check", "round 2");
            Assert.EndsWith("check: round 2", File.ReadAllLines(store.LogFile)[^1]);
            var json = System.Text.Json.JsonSerializer.Serialize(new Settings { LogLevel = LogLevel.Verbose }, JsonStore.Json);
            Assert.Contains("\"Verbose\"", json);
        }
        finally { dir.Delete(true); }
    }
}
