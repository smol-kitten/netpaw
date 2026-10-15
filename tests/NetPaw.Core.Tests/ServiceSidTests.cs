using NetPaw.Ipc;
using Xunit;

namespace NetPaw.Tests;

public class ServiceSidTests
{
    [Fact]
    public void Matches_the_well_known_TrustedInstaller_sid() =>
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", ServiceSid.For("TrustedInstaller"));

    [Fact]
    public void Is_case_insensitive_and_stable() => Assert.Equal(ServiceSid.For("netpaw"), ServiceSid.For("NetPaw"));
}
