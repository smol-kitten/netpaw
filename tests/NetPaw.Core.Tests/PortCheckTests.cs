using NetPaw.Connectivity;
using NetPaw.Reach;
using Xunit;

namespace NetPaw.Tests;

public class PortCheckTests
{
    [Theory]
    [InlineData("10.0.0.5:443", true, "10.0.0.5", 443)]
    [InlineData(" nas.corp.local:22 ", true, "nas.corp.local", 22)]
    [InlineData("printer:9100", true, "printer", 9100)]
    [InlineData("10.0.0.5", false, "", 0)]
    [InlineData("10.0.0.5/24", false, "", 0)]
    [InlineData("10.0.0.5:0", false, "", 0)]
    [InlineData("10.0.0.5:70000", false, "", 0)]
    [InlineData("10.0.0.5:", false, "", 0)]
    [InlineData(":443", false, "", 0)]
    [InlineData("fe80::1:443", false, "", 0)]
    [InlineData("10.0.0.0/24 443", false, "", 0)]
    public void PortCheckParsesHostPort(string text, bool ok, string host, int port)
    {
        Assert.Equal(ok, PortCheck.TryParse(text, out var h, out var p));
        if (ok) { Assert.Equal(host, h); Assert.Equal(port, p); }
    }

    [Fact]
    public void ReachDecidesPortCheckBeforeAddresses()
    {
        var d = ReachResolver.Resolve("10.0.0.5:443", Fx.Adapter(addrs: ["10.0.0.9/24"]), [], []);
        Assert.Equal(ReachKind.PortCheck, d.Kind); Assert.Equal("10.0.0.5:443", d.Target); Assert.Null(d.Address);
        Assert.Equal(ReachKind.AlreadyReachable, ReachResolver.Resolve("10.0.0.5", Fx.Adapter(addrs: ["10.0.0.9/24"]), [], []).Kind);   // unchanged without a port
    }

    [Fact]
    public async Task PortCheckReportsOpenRefusedTimeout()
    {
        var probe = new FakeProbe(); probe.OpenPorts.Add("10.0.0.5:443"); probe.RefusedPorts.Add("10.0.0.5:23"); probe.UnreachableHosts.Add("10.9.9.9");
        var open = await PortCheck.Run(probe, "10.0.0.5", 443);
        Assert.Equal(PortVerdict.Open, open.Verdict); Assert.True(open.Open); Assert.Equal(0, open.ExitCode); Assert.Contains("is open", open.Text);
        var refused = await PortCheck.Run(probe, "10.0.0.5", 23);
        Assert.Equal(PortVerdict.Refused, refused.Verdict); Assert.Equal(1, refused.ExitCode); Assert.Contains("nothing listens on 23", refused.Text);
        var timeout = await PortCheck.Run(probe, "10.0.0.5", 8080, timeoutMs: 1500);
        Assert.Equal(PortVerdict.Timeout, timeout.Verdict); Assert.Equal(2, timeout.ExitCode); Assert.Equal(1500, timeout.Ms);
        var unreachable = await PortCheck.Run(probe, "10.9.9.9", 80);
        Assert.Equal(PortVerdict.Unreachable, unreachable.Verdict); Assert.Equal(1, unreachable.ExitCode); Assert.Contains("reach 10.9.9.9", unreachable.Text);
        var unresolved = await PortCheck.Run(probe, "nas.invalid", 22);
        Assert.Equal(PortVerdict.Unresolved, unresolved.Verdict); Assert.Equal(3, unresolved.ExitCode);
    }
}
