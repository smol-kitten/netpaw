using NetPaw.Adapters;
using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

public class VlanProviderTests
{
    [Theory]
    [InlineData("VLANID=7\r\n", 0, true, 7, "VlanID")]
    [InlineData("VLANID=0", 0, true, 0, "VlanID")]
    [InlineData("VLANID=none", 0, false, null, null)]
    [InlineData("VLANID=", 0, true, null, "VlanID")]
    [InlineData("", 0, false, null, null)]
    [InlineData("Get-NetAdapterAdvancedProperty : Access denied", 1, false, null, "error: Get-NetAdapterAdvancedProperty : Access denied")]
    public void ParsesPowerShellOutput(string output, int rc, bool capable, int? id, string? source)
    {
        var v = PowerShellVlanProvider.Parse(output, rc);
        Assert.Equal(capable, v.Capable); Assert.Equal(id, v.VlanId); Assert.Equal(source, v.Source);
    }

    [Fact]
    public void QueriesThroughTheRunnerAndCachesPerAdapter()
    {
        var runner = new FakeRunner();
        var p = new PowerShellVlanProvider(new CountingRunner(runner, "VLANID=12"));
        var first = p.Query("Ethernet 2"); var second = p.Query("Ethernet 2"); var other = p.Query("Wi-Fi");
        Assert.True(first.Capable); Assert.Equal(12, first.VlanId); Assert.Same(first, second);
        Assert.Equal(2, runner.Ran.Count);                                                        // one PowerShell start per adapter, not per call
        Assert.Contains("Get-NetAdapterAdvancedProperty -Name 'Ethernet 2' -RegistryKeyword VlanID", runner.Ran[0].Arguments);
        Assert.Equal("powershell", runner.Ran[0].FileName); Assert.False(runner.Ran[0].Critical);
        p.Forget("Ethernet 2"); p.Query("Ethernet 2"); Assert.Equal(3, runner.Ran.Count);
        Assert.Contains("O''Brien", new PowerShellVlanProvider(new CountingRunner(runner, "VLANID=none")).Query("O'Brien").Source is null ? runner.Ran[^1].Arguments : "");   // quote escaped
    }

    /// <summary>Records through the inner FakeRunner but answers with a fixed output.</summary>
    sealed class CountingRunner(FakeRunner inner, string output) : IStepRunner
    {
        public StepResult Run(Step step) { inner.Run(step); return new StepResult(step, 0, output); }
    }
}
