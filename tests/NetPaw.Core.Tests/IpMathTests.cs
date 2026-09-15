using NetPaw.Model;
using NetPaw.Net;
using Xunit;

namespace NetPaw.Tests;

public class IpMathTests
{
    [Theory]
    [InlineData(24, "255.255.255.0")] [InlineData(16, "255.255.0.0")] [InlineData(8, "255.0.0.0")]
    [InlineData(30, "255.255.255.252")] [InlineData(32, "255.255.255.255")] [InlineData(0, "0.0.0.0")] [InlineData(21, "255.255.248.0")]
    public void PrefixAndMaskRoundTrip(int prefix, string mask)
    {
        Assert.Equal(mask, IpMath.PrefixToMask(prefix));
        Assert.Equal(prefix, IpMath.MaskToPrefix(mask));
    }

    [Fact] public void NonContiguousMaskRejected() => Assert.Throws<FormatException>(() => IpMath.MaskToPrefix("255.0.255.0"));

    [Theory]
    [InlineData("192.168.88.10/24", "192.168.88.10", 24)]
    [InlineData("10.0.0.1 255.0.0.0", "10.0.0.1", 8)]
    [InlineData("10.0.0.1/255.255.0.0", "10.0.0.1", 16)]
    [InlineData("172.16.5.5", "172.16.5.5", 24)]
    [InlineData("  192.168.1.1/30  ", "192.168.1.1", 30)]
    public void ParsesCidrForms(string text, string ip, int prefix)
    {
        Assert.True(IpMath.TryParseCidr(text, out var a));
        Assert.Equal(new IpAddr(ip, prefix), a);
    }

    [Theory] [InlineData("")] [InlineData("nope")] [InlineData("1.2.3")] [InlineData("1.2.3.4/33")] [InlineData("1.2.3.4/x")] [InlineData("::1")] [InlineData("1.2.3.4 5 6")]
    public void RejectsGarbage(string text) => Assert.False(IpMath.TryParseCidr(text, out _));

    [Fact]
    public void ContainsAndNetwork()
    {
        Assert.True(IpMath.Contains("192.168.88.10", 24, "192.168.88.1"));
        Assert.False(IpMath.Contains("192.168.88.10", 24, "192.168.89.1"));
        Assert.True(IpMath.Contains("10.90.90.90", 8, "10.1.2.3"));
        Assert.Equal("192.168.88.0", IpMath.NetworkOf("192.168.88.10", 24));
        Assert.Equal("192.168.88.255", IpMath.BroadcastOf("192.168.88.10", 24));
        Assert.Equal("10.0.0.0", IpMath.NetworkOf("10.90.90.90", 8));
    }

    [Fact]
    public void HostCandidatesCountDownAndSkipReserved()
    {
        var c = IpMath.HostCandidates("192.168.88.1", 24, ["192.168.88.254", "192.168.88.252"]).Take(3).ToList();
        Assert.Equal(["192.168.88.253", "192.168.88.251", "192.168.88.250"], c);
        Assert.DoesNotContain("192.168.88.1", IpMath.HostCandidates("192.168.88.1", 24, []));
        Assert.DoesNotContain("192.168.88.255", IpMath.HostCandidates("192.168.88.1", 24, []));
        Assert.Empty(IpMath.HostCandidates("10.0.0.1", 32, []));
        Assert.Equal("10.0.0.2", IpMath.HostCandidates("10.0.0.1", 30, []).Single());
    }

    [Theory] [InlineData("10.1.1.1", true)] [InlineData("172.16.0.1", true)] [InlineData("172.32.0.1", false)] [InlineData("192.168.0.1", true)] [InlineData("8.8.8.8", false)] [InlineData("169.254.1.1", true)]
    public void PrivateRanges(string ip, bool isPrivate) => Assert.Equal(isPrivate, IpMath.IsPrivate(ip));
}

public class RouteAndHotkeyTests
{
    [Theory]
    [InlineData("10.0.0.0/8 via 192.168.1.1 metric 10", "10.0.0.0", 8, "192.168.1.1", 10)]
    [InlineData("10.5.5.5/24", "10.5.5.0", 24, null, null)]
    [InlineData("172.16.0.0/12 via 10.0.0.1", "172.16.0.0", 12, "10.0.0.1", null)]
    public void ParsesRoutes(string text, string dest, int prefix, string? gw, int? metric)
    {
        Assert.True(StaticRoute.TryParse(text, out var r));
        Assert.Equal(new StaticRoute(dest, prefix, gw, metric), r);
    }

    [Theory] [InlineData("via 1.2.3.4")] [InlineData("10.0.0.0/8 via nope")] [InlineData("10.0.0.0/8 metric -1")] [InlineData("10.0.0.0/8 foo 1")]
    public void RejectsBadRoutes(string text) => Assert.False(StaticRoute.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+1", Hotkeys.HotkeyModifiers.Ctrl | Hotkeys.HotkeyModifiers.Alt, 0x31, "Ctrl+Alt+1")]
    [InlineData("win+shift+f5", Hotkeys.HotkeyModifiers.Win | Hotkeys.HotkeyModifiers.Shift, 0x74, "Shift+Win+F5")]
    [InlineData("Ctrl + Numpad3", Hotkeys.HotkeyModifiers.Ctrl, 0x63, "Ctrl+Numpad3")]
    [InlineData("Alt+n", Hotkeys.HotkeyModifiers.Alt, 0x4E, "Alt+N")]
    [InlineData("Ctrl+Alt+Space", Hotkeys.HotkeyModifiers.Ctrl | Hotkeys.HotkeyModifiers.Alt, 0x20, "Ctrl+Alt+Space")]
    public void ParsesHotkeys(string text, Hotkeys.HotkeyModifiers mods, int vk, string canonical)
    {
        Assert.True(Hotkeys.HotkeyParser.TryParse(text, out var hk));
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
        Assert.Equal(canonical, hk.ToString());
    }

    [Theory] [InlineData("N")] [InlineData("Ctrl+")] [InlineData("Ctrl+Alt")] [InlineData("Ctrl+F25")] [InlineData("Ctrl+A+B")] [InlineData("")]
    public void RejectsBadHotkeys(string text) => Assert.False(Hotkeys.HotkeyParser.TryParse(text, out _));
}
