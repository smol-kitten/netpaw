using NetPaw.Arp;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Net;
using NetPaw.Presets;
using NetPaw.Reach;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class IntentWatcherTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
    static IntentWatcher W() => new(ownPid: 999, pid => pid == 4242 ? "msedge" : null);
    static int _lp = 50000;
    static TcpEndpoint E(string remote, int port = 80, int pid = 4242, int localPort = 0) => new(remote, port, pid, "10.10.20.50", localPort == 0 ? ++_lp : localPort);

    [Fact]
    public void HandshakeNoiseIsNotStuckButTwoSamplesAre()
    {
        var w = W();
        var cf = E("1.1.1.1", 443);
        Assert.Empty(w.Sample([E("192.168.88.1")], T0));                                   // first sight: pending
        Assert.Empty(w.Sample([cf], T0.AddSeconds(2)));                                     // the first one completed, a new one appeared
        var stuck = w.Sample([cf], T0.AddSeconds(4));                                       // same socket seen twice: stuck
        var (e, since) = Assert.Single(stuck);
        Assert.Equal("1.1.1.1", e.Remote); Assert.Equal(T0.AddSeconds(2), since);
    }

    [Fact]
    public void ParallelConnectsToOneHostAreNotStuckOnFirstSight()
    {
        // Edge opens 2-6 sockets to the same host:port at once; on the first sample none of them is stuck yet.
        var w = W();
        var burst = new[] { E("192.168.88.1", localPort: 61001), E("192.168.88.1", localPort: 61002), E("192.168.88.1", localPort: 61003) };
        Assert.Empty(w.Sample(burst, T0));
        var stuck = w.Sample(burst, T0.AddSeconds(2));
        var (e, since) = Assert.Single(stuck);                                              // one report per remote (cooldown), not three
        Assert.Equal(T0, since); Assert.Equal(2, new StuckTarget(e.Remote, e.Port, e.Pid, "msedge", StuckKind.OffLinkNoReply, null!, since).SecondsWaiting(T0.AddSeconds(2)));
    }

    [Fact]
    public void IgnoresLoopbackLinkLocalMulticastAndOwnPid()
    {
        var w = W();
        var noise = new[] { E("127.0.0.1"), E("169.254.9.9"), E("224.0.0.251"), E("255.255.255.255"), E("10.0.0.5", 443, pid: 999) };
        Assert.Empty(w.Sample(noise, T0)); Assert.Empty(w.Sample(noise, T0.AddSeconds(2))); Assert.Empty(w.Sample(noise, T0.AddSeconds(4)));
    }

    [Fact]
    public void CooldownSuppressesRepeatsPerRemoteAndCapsAtThree()
    {
        var w = W();
        var rows = new[] { E("10.1.1.1"), E("10.1.1.2"), E("10.1.1.3"), E("10.1.1.4") };
        w.Sample(rows, T0);
        Assert.Equal(3, w.Sample(rows, T0.AddSeconds(2)).Count);                            // cap
        var again = E("10.1.1.1", 8080);
        Assert.Empty(w.Sample([again], T0.AddSeconds(4)));                                  // same remote, other socket: first sight
        Assert.Empty(w.Sample([again], T0.AddSeconds(6)));                                  // stuck, but the remote is in cooldown
        var later = E("10.1.1.1", 8080);
        Assert.Empty(w.Sample([later], T0.AddMinutes(11)));                                 // cooldown over: first sight again ...
        Assert.Single(w.Sample([later], T0.AddMinutes(11).AddSeconds(2)));                  // ... reported once more
        Assert.Empty(w.Sample([later], T0.AddMinutes(11).AddSeconds(4)));                   // ... and not twice
    }

    [Fact]
    public void ClassifiesOnLinkSilentViaArpAndOffLinkNoReply()
    {
        var nic = Fx.Nic(dhcp: true, addrs: ["10.10.20.50/24"], gw: "10.10.20.1");
        Assert.Equal(StuckKind.OnLinkSilent, IntentWatcher.Classify("10.10.20.77", nic, []));
        Assert.Equal(StuckKind.OnLinkSilent, IntentWatcher.Classify("10.10.20.77", nic, [new ArpEntry("10.10.20.77", "00-00-00-00-00-00", "invalid", null)]));
        Assert.Equal(StuckKind.OffLinkNoReply, IntentWatcher.Classify("10.10.20.77", nic, [new ArpEntry("10.10.20.77", "bc-24-11-00-00-77", "dynamic", null)]));   // ARP answered: the host is there, the port is silent
        Assert.Equal(StuckKind.OffLinkNoReply, IntentWatcher.Classify("192.168.88.1", nic, []));
        Assert.Equal(StuckKind.OffLinkNoReply, IntentWatcher.Classify("192.168.88.1", null, []));
    }

    [Fact]
    public void RecommendsProfilePresetOrTempAndAdvisoryNamesThem()
    {
        var nic = Fx.Nic(dhcp: true, addrs: ["10.10.20.50/24"], gw: "10.10.20.1");
        var lab = Fx.Static("MikroTik lab", "192.168.88.1", "192.168.88.5/24");
        var presets = new[] { new Preset { Vendor = "Ubiquiti", Model = "EdgeRouter", Ip = "192.168.1.1", Prefix = 24 } };
        var w = W();
        var t = w.Describe(E("192.168.88.1"), T0, nic, [], [lab], presets);
        Assert.Equal("msedge", t.Process); Assert.Equal(ReachKind.UseProfile, t.Decision.Kind);
        var adv = Advisor.FromStuck(t, T0.AddSeconds(9));
        Assert.Equal("msedge cannot reach 192.168.88.1:80", adv.Title);
        Assert.Equal("No reply for 9 s (no reply from beyond the gateway). Profile 'MikroTik lab' covers it.", adv.Text);
        Assert.Equal(AdvisorySeverity.Warning, adv.Severity); Assert.Equal(RepairKind.Reach, adv.Repair); Assert.Equal("192.168.88.1", adv.Target);

        var p = w.Describe(E("192.168.1.1", 443, pid: 7), T0, nic, [], [], presets);
        Assert.Equal("pid 7", p.Process); Assert.Equal(ReachKind.UsePreset, p.Decision.Kind);
        Assert.Contains("Preset Ubiquiti EdgeRouter covers it", Advisor.FromStuck(p, T0).Text);

        var u = w.Describe(E("172.31.9.9"), T0, nic, [], [], []);
        Assert.Equal(ReachKind.TempAddress, u.Decision.Kind);
        var uAdv = Advisor.FromStuck(u, T0);
        Assert.Equal(AdvisorySeverity.Info, uAdv.Severity); Assert.Contains("'reach 172.31.9.9' adds a temporary address", uAdv.Text); Assert.Equal(RepairKind.Reach, uAdv.Repair);

        var here = w.Describe(E("10.10.20.77"), T0, nic, [], [], []);
        Assert.Equal(StuckKind.OnLinkSilent, here.Kind); Assert.Equal(ReachKind.AlreadyReachable, here.Decision.Kind);
        var hAdv = Advisor.FromStuck(here, T0);
        Assert.Contains("down, or on another VLAN", hAdv.Text); Assert.Equal(RepairKind.None, hAdv.Repair);
    }

    [Fact]
    public void PolicyDeniesIntentWatchAndSettingDefaultsOn()
    {
        var pol = PolicyReader.Read(new DictionaryPolicySource(new Dictionary<string, object> { ["AllowIntentWatch"] = 0 }));
        Assert.False(pol.AllowIntentWatch); Assert.True(pol.IsSet("AllowIntentWatch"));
        Assert.True(Policy.None.AllowIntentWatch); Assert.Contains("AllowIntentWatch", Policy.Keys);
        Assert.True(new RepairSettings().IntentWatch);
    }
}
