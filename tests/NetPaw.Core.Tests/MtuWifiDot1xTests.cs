using NetPaw.Adapters;
using NetPaw.Connectivity;
using Xunit;

namespace NetPaw.Tests;

public class MtuWifiDot1xTests
{
    [Theory]
    [InlineData(1500, 1500)]
    [InlineData(1492, 1492)]
    [InlineData(1452, 1452)]
    [InlineData(1280, 1280)]
    public async Task MtuBinarySearchFindsThePathMtu(int real, int expected)
    {
        var probe = new FakeProbe(); probe.PathMtu["1.1.1.1"] = real;
        var r = await Mtu.Discover(probe, "1.1.1.1");
        Assert.Equal(expected, r!.Mtu); Assert.True(probe.DfProbes <= 12);
        Assert.Equal(expected < 1500, r.Reduced);
    }

    [Fact]
    public async Task MtuIsInconclusiveWhenIcmpIsBlocked()
    {
        var probe = new FakeProbe();
        Assert.Null(await Mtu.Discover(probe, "9.9.9.9")); Assert.Equal(1, probe.DfProbes);
        probe.PathMtu["x"] = 1000;                                           // below the floor: also inconclusive rather than a wrong number
        Assert.Null(await Mtu.Discover(probe, "x"));
    }

    [Fact]
    public void AdvisorWarnsOnLowMtuOnlyWiredAndWithoutVpn()
    {
        var wired = Fx.Nic(dhcp: true, addrs: ["10.0.0.20/24"], gw: "10.0.0.1", dns: ["10.0.0.53"]);
        Snapshot Snap(NetPaw.Adapters.AdapterInfo a, int mtu) => new(DateTimeOffset.Now, a, true, true, true, [new("10.0.0.1", true, 1)], [new("1.1.1.1", true, 9)], null, true) { PathMtu = new MtuResult("1.1.1.1", mtu, 9) };
        var adv = Assert.Single(Advisor.Analyze(Snap(wired, 1452), [wired]), x => x.Title.StartsWith("Path MTU"));
        Assert.Equal("Path MTU 1452 on Ethernet", adv.Title); Assert.Equal(RepairKind.SetMtu, adv.Repair); Assert.Equal(1452, adv.Value); Assert.Equal("Ethernet", adv.RepairAdapter);
        Assert.Contains("mtu=1452", Advisor.PlanSetMtu(wired, 1452).Steps[0].Arguments);
        Assert.DoesNotContain(Advisor.Analyze(Snap(wired, 1500), [wired]), x => x.Title.StartsWith("Path MTU"));
        var wifi = Fx.Nic("Wi-Fi", wireless: true, dhcp: true, addrs: ["10.0.0.21/24"], gw: "10.0.0.1", dns: ["10.0.0.53"]);
        Assert.DoesNotContain(Advisor.Analyze(Snap(wifi, 1452), [wifi]), x => x.Title.StartsWith("Path MTU"));
        var vpn = Fx.Nic("WireGuard Tunnel", "WireGuard Tunnel", physical: false, addrs: ["10.99.0.2/32"], gw: "10.99.0.1");
        Assert.DoesNotContain(Advisor.Analyze(Snap(wired, 1420), [wired, vpn]), x => x.Title.StartsWith("Path MTU"));   // a tunnel MTU is expected
    }

    [Fact]
    public void WlanParsesSignalChannelRateAndAdvisorWarnsWhenWeak()
    {
        const string text = """
                Name                   : Wi-Fi
                State                  : connected
                SSID                   : Corp WLAN
                BSSID                  : 10:20:30:40:50:60
                Radio type             : 802.11ax
                Band                   : 5 GHz
                Channel                : 36
                Receive rate (Mbps)    : 866.7
                Transmit rate (Mbps)   : 866.7
                Signal                 : 18%
            """;
        var w = Wlan.Parse(text)[0];
        Assert.Equal(18, w.Signal); Assert.Equal(36, w.Channel); Assert.Equal("802.11ax", w.Radio); Assert.Equal(866, w.ReceiveMbps); Assert.True(w.Weak);
        Assert.Equal("Corp WLAN · ch 36 · 18 % · 866 Mbit/s · 802.11ax", w.Summary);
        var de = Wlan.Parse(text.Replace("Channel ", "Kanal   ").Replace("Radio type ", "Funktyp    ").Replace("Receive rate (Mbps)", "Empfangsrate (MBit/s)"))[0];
        Assert.Equal(36, de.Channel); Assert.Equal("802.11ax", de.Radio); Assert.Equal(866, de.ReceiveMbps);
        var wifi = Fx.Nic("Wi-Fi", wireless: true, dhcp: true, addrs: ["10.0.0.21/24"], gw: "10.0.0.1", dns: ["10.0.0.53"]);
        var snap = new Snapshot(DateTimeOffset.Now, wifi, true, true, true, [], [], null, false) { Wlan = w };
        var adv = Assert.Single(Advisor.Analyze(snap, null), x => x.Title.StartsWith("Weak Wi-Fi"));
        Assert.Equal("Weak Wi-Fi signal (18 %)", adv.Title); Assert.Contains("channel 36", adv.Text);
        Assert.DoesNotContain(Advisor.Analyze(snap with { Wlan = w with { Signal = 70 } }, null), x => x.Title.StartsWith("Weak"));
    }

    [Fact]
    public async Task Dot1xParsesAuthFailureAndAdvisorPrefersItOverNoLease()
    {
        const string text = """

            There is 1 interface on the system:

                Name             : Ethernet
                Description      : Intel(R) Ethernet Connection I219-LM
                GUID             : 1234
                Physical Address : AA-BB-CC-DD-EE-FF
                State            : Authentication failed. Connected.
            """;
        var d = Assert.Single(Dot1x.Parse(text));
        Assert.Equal("Ethernet", d.Name); Assert.True(d.Failed); Assert.False(d.InProgress);
        Assert.True(Dot1x.Parse(text.Replace("Authentication failed. Connected.", "Connected. Network does not support authentication."))[0].NotSupported);
        Assert.True(Dot1x.Parse(text.Replace("Authentication failed. Connected.", "Authenticating"))[0].InProgress);
        Assert.True(Dot1x.Parse(text.Replace("State            :", "Status           :").Replace("Authentication failed. Connected.", "Authentifizierung fehlgeschlagen"))[0].Failed);

        var probe = new FakeProbe();
        var checker = new ConnectivityChecker(probe) { Dot1xReader = n => n == "Ethernet" ? d : null };
        var nic = Fx.Nic(dhcp: true, addrs: ["169.254.7.7/16"], gw: null);
        var s = await checker.Check(nic, new CheckSettings { Enabled = true });
        Assert.NotNull(s.Dot1x); Assert.Equal(NetState.NoAddress, s.State);
        var adv = Advisor.Analyze(s, null);
        var only = Assert.Single(adv);
        Assert.Equal("802.1X authentication failed on Ethernet", only.Title); Assert.Equal(AdvisorySeverity.Error, only.Severity); Assert.False(only.CanRenew);
        // Reader not consulted for adapters that have an address, or for wireless.
        var asked = new List<string>();
        checker.Dot1xReader = n => { asked.Add(n); return null; };
        await checker.Check(Fx.Nic(dhcp: true, addrs: ["10.0.0.5/24"], gw: "10.0.0.1"), new CheckSettings { Enabled = true });
        await checker.Check(Fx.Nic("Wi-Fi", wireless: true, dhcp: true, addrs: ["169.254.1.1/16"], gw: null), new CheckSettings { Enabled = true });
        Assert.Empty(asked);
        // Without the reader the usual "No DHCP lease" finding stays.
        checker.Dot1xReader = null;
        s = await checker.Check(nic, new CheckSettings { Enabled = true });
        Assert.Contains(Advisor.Analyze(s, null), x => x.Title == "No DHCP lease");
    }
}
