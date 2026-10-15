using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

/// <summary>G1: a LocalSystem process must never resolve a tool through PATH. The plan keeps the short name;
/// the launcher maps known tools to System32 on Windows and leaves everything else alone.</summary>
public class SystemToolTests
{
    const string Sys = @"C:\Windows\System32";

    [Fact]
    public void Off_windows_names_pass_through()
    {
        Assert.Equal("netsh", SystemTool.Resolve("netsh", isWindows: false, Sys));
    }

    [Theory]
    [InlineData("netsh", "netsh.exe")]
    [InlineData("ipconfig", "ipconfig.exe")]
    [InlineData("NETSH.exe", "netsh.exe")]     // case and an explicit .exe do not matter
    [InlineData("pktmon", "pktmon.exe")]
    [InlineData("arp", "arp.exe")]
    public void Known_tools_resolve_under_system32(string name, string exe)
    {
        Assert.Equal(Path.Combine(Sys, exe), SystemTool.Resolve(name, isWindows: true, Sys));
    }

    [Fact]
    public void Powershell_resolves_to_its_own_folder()
    {
        Assert.Equal(Path.Combine(Sys, "WindowsPowerShell", "v1.0", "powershell.exe"), SystemTool.Resolve("powershell", isWindows: true, Sys));
    }

    [Fact]
    public void Unknown_and_rooted_names_pass_through()
    {
        Assert.Equal("some-other-tool", SystemTool.Resolve("some-other-tool", isWindows: true, Sys));
        var rooted = Path.Combine(Path.GetTempPath(), "netsh.exe");   // rooted on every OS
        Assert.Equal(rooted, SystemTool.Resolve(rooted, isWindows: true, Sys));
    }
}
