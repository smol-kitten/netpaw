using NetPaw.Connectivity;
using Xunit;

namespace NetPaw.Tests;

public class DnsTraceTests
{
    [Fact]
    public void DnsQueryBuildsAndParsesPacket()
    {
        var q = DnsQuery.BuildQuery(0x1234, "www.example.com.");
        Assert.Equal([0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'w', (byte)'w', (byte)'w', 7], q.Take(17).ToArray());
        Assert.Equal([0, 0, 1, 0, 1], q.TakeLast(5).ToArray());
        // Reply: same id, QR=1, NOERROR, one question (compressed name pointer in the answer), one A record 93.184.216.34.
        var reply = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 };
        reply.AddRange(q.Skip(12));                                   // question section copied
        reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4, 93, 184, 216, 34]);
        Assert.True(DnsQuery.TryParse([.. reply], 0x1234, out var rcode, out var a)); Assert.Equal(0, rcode); Assert.Equal("93.184.216.34", a);
        var nx = reply.ToArray(); nx[3] = 0x83; nx[7] = 0;
        Assert.True(DnsQuery.TryParse(nx, 0x1234, out rcode, out a)); Assert.Equal(3, rcode); Assert.Equal("NXDOMAIN", DnsQuery.RcodeName(rcode)); Assert.Null(a);
        Assert.False(DnsQuery.TryParse([.. reply], 0x9999, out _, out _));          // foreign id
        Assert.False(DnsQuery.TryParse(q, 0x1234, out _, out _));                    // a query is not a reply (QR=0)
        Assert.False(DnsQuery.TryParse([1, 2, 3], 0x1234, out _, out _));
    }

    [Fact]
    public async Task CheckerQueriesEachDnsServerAndAdvisorNamesTheDeadOne()
    {
        var probe = new FakeProbe(); probe.Reachable.UnionWith(["10.0.0.1", "1.1.1.1"]); probe.Resolvable.Add("www.msftconnecttest.com"); probe.DnsAnswering.Add("10.0.0.54");
        var checker = new ConnectivityChecker(probe);
        var nic = Fx.Nic(dhcp: true, addrs: ["10.0.0.20/24"], gw: "10.0.0.1", dns: ["10.0.0.53", "10.0.0.54"]);
        var s = await checker.Check(nic, new CheckSettings { Enabled = true, InternetTargets = ["1.1.1.1"] });
        Assert.Equal(["10.0.0.53", "10.0.0.54"], probe.DnsAsked);
        Assert.False(s.DnsServers[0].Ok); Assert.True(s.DnsServers[1].Ok); Assert.True(s.AnyDnsServerAnswers);
        Assert.Equal(NetState.Online, s.State);                                        // a dead secondary does not change the state
        var adv = Advisor.Analyze(s, null);
        var finding = Assert.Single(adv, x => x.Title.StartsWith("DNS server"));
        Assert.Equal("DNS server 10.0.0.53 does not answer", finding.Title);
        Assert.Contains("10.0.0.54 answers", finding.Text); Assert.Contains("on the DHCP server", finding.Text); Assert.False(finding.CanRenew);

        // Nobody answers and no resolver check ran (no internet): "No DNS server answers", renewable on DHCP.
        probe.DnsAnswering.Clear(); probe.Reachable.Remove("1.1.1.1");
        s = await checker.Check(nic, new CheckSettings { Enabled = true, InternetTargets = ["1.1.1.1"] });
        var none = Assert.Single(Advisor.Analyze(s, null), x => x.Title == "No DNS server answers");
        Assert.True(none.CanRenew); Assert.Contains("although the gateway answers", none.Text);
        // Checks off: nothing asked, nothing said.
        probe.DnsAsked.Clear();
        s = await checker.Check(nic, new CheckSettings());
        Assert.Empty(probe.DnsAsked); Assert.Empty(s.DnsServers); Assert.DoesNotContain(Advisor.Analyze(s, null), x => x.Title.Contains("DNS server"));
    }

    [Fact]
    public async Task TraceStopsAtTargetAndAfterSilence()
    {
        var probe = new FakeProbe();
        probe.Paths["1.1.1.1"] = ["10.0.0.1", "100.64.0.1", null, "1.1.1.1"];
        var hops = new List<Hop>();
        var r = await Trace.Run(probe, "1.1.1.1", progress: new Progress<Hop>(hops.Add));
        Assert.True(r.Reached); Assert.Equal(4, r.Hops.Count); Assert.Equal("1.1.1.1", r.Hops[3].Address);
        Assert.True(r.Hops[2].Silent); Assert.Equal("*  *  *", r.Hops[2].MsText); Assert.Equal("2 ms  2 ms  2 ms", r.Hops[0].MsText);
        Assert.StartsWith("1.1.1.1 reached in 4 hops", r.Summary);
        // Path that dies after the ISP hop: three silent hops end the walk, summary names the last answering hop.
        probe.Paths["8.8.8.8"] = ["10.0.0.1", "100.64.0.1"];
        r = await Trace.Run(probe, "8.8.8.8");
        Assert.False(r.Reached); Assert.Equal(5, r.Hops.Count); Assert.Equal("100.64.0.1", r.LastAnswering!.Address);
        Assert.Contains("path stops after hop 2 (100.64.0.1)", r.Summary);
        // Nothing ever answers: five silent hops, then give up with the ICMP hint.
        r = await Trace.Run(probe, "9.9.9.9");
        Assert.Equal(5, r.Hops.Count); Assert.Null(r.LastAnswering); Assert.Contains("ICMP blocked", r.Summary);
    }
}
