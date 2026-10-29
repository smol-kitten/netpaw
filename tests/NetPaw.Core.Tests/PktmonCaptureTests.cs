using Xunit;
using NetPaw.Discovery;

namespace NetPaw.Tests;

/// <summary>The look-before-touch rules of the shared (service) capture path: pktmon state is machine-wide, and the
/// capture folder must be the service's own, not a junction a user planted.</summary>
public class PktmonCaptureTests
{
    [Fact]
    public void IdleWithNoFiltersIsNotBusy()
    {
        Assert.Null(PktmonCapture.BusyReason("Packet Monitor is not running.\r\n", "No filters.\r\n"));
        Assert.Null(PktmonCapture.BusyReason("Packet Monitor is not running.", "No filters configured."));
    }

    [Fact]
    public void RunningCaptureIsBusyAndNamesIt()
    {
        var why = PktmonCapture.BusyReason("Packet Monitor is running.\r\n\r\nCapture Mode: All\r\n", "No filters.");
        Assert.NotNull(why); Assert.Contains("capture is running", why); Assert.Contains("Packet Monitor is running", why);
    }

    [Fact]
    public void ForeignFiltersAreBusyButOurOwnAreNot()
    {
        const string header = "#   Name          Protocol   Port\r\n-   ----          --------   ----\r\n";
        Assert.Null(PktmonCapture.BusyReason("Packet Monitor is not running.", header + "1   NetPawLLDP    0x88CC\r\n2   NetPawCDP     01-00-0C-CC-CC-CC\r\n"));
        var why = PktmonCapture.BusyReason("Packet Monitor is not running.", header + "1   NetPawLLDP    0x88CC\r\n2   AdminDns      UDP        53\r\n");
        Assert.NotNull(why); Assert.Contains("not set by NetPaw", why); Assert.Contains("AdminDns", why);
    }

    [Fact]
    public void WorkDirMustExistAndMustNotBeAReparsePointNorHaveOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "netpaw-tests", Path.GetRandomFileName());
        var real = Path.Combine(root, "real"); Directory.CreateDirectory(real);
        try
        {
            Assert.Null(PktmonCapture.WorkDirProblem(real));
            Assert.Contains("does not exist", PktmonCapture.WorkDirProblem(Path.Combine(root, "missing")));
            var link = Path.Combine(root, "capture");
            Directory.CreateSymbolicLink(link, real);                       // a user-planted junction stand-in
            Assert.Contains("is a reparse point", PktmonCapture.WorkDirProblem(link));
            var under = Path.Combine(link, "sub"); Directory.CreateDirectory(under);
            Assert.Contains("parent of capture folder", PktmonCapture.WorkDirProblem(under));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
