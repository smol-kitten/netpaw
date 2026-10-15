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

public class SystemToolStrictTests
{
    const string Sys = @"C:\Windows\System32";

    [Fact]
    public void Strict_refuses_an_unknown_bare_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SystemTool.Resolve("some-other-tool", isWindows: true, Sys, strict: true));
        Assert.Contains("never searches PATH", ex.Message);
    }

    [Fact]
    public void Strict_refuses_a_rooted_path_outside_system32()
    {
        var outside = Path.Combine(Path.GetTempPath(), "netsh.exe");
        Assert.Throws<InvalidOperationException>(() => SystemTool.Resolve(outside, isWindows: true, Sys, strict: true));
    }

    [Fact]
    public void Strict_allows_known_tools_and_rooted_paths_inside_system32()
    {
        Assert.Equal(Path.Combine(Sys, "netsh.exe"), SystemTool.Resolve("netsh", isWindows: true, Sys, strict: true));
        var inside = Path.Combine(Sys, "netsh.exe");
        Assert.Equal(inside, SystemTool.Resolve(inside, isWindows: true, Sys, strict: true));
    }

    [Fact]
    public void Non_strict_keeps_the_pass_through_for_user_context_tools()
    {
        Assert.Equal("some-other-tool", SystemTool.Resolve("some-other-tool", isWindows: true, Sys, strict: false));
    }
}
