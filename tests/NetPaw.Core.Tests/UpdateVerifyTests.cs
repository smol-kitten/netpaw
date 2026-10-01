using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetPaw.Updates;
using Xunit;

namespace NetPaw.Tests;

/// <summary>
/// The update gate, on real osslsigncode-signed MSIs from tools/make-update-fixtures.sh (test PKI shaped like the
/// catboy one, timestamped at a fixed time, so the files never expire). No network anywhere.
/// </summary>
public class UpdateVerifyTests
{
    static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "update");
    static byte[] File_(string name) => File.ReadAllBytes(Path.Combine(Dir, name));
    static X509Certificate2 Root(string name) => X509CertificateLoader.LoadCertificateFromFile(Path.Combine(Dir, name));
    static readonly X509Certificate2[] TestRoot = [Root("test-root.crt")];
    static string Sigs => File.ReadAllText(Path.Combine(Dir, "SIGNATURES.md"));

    [Fact]
    public void ValidSignedMsiVerifies()
    {
        var r = MsiAuthenticode.Verify(File_("valid.msi"), TestRoot);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("smol-kitten/netpaw", r.Signer);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 1, 0, TimeSpan.Zero), r.Timestamp);   // the leaf expired at 12:30; the timestamp keeps it valid
        Assert.True(UpdateTrust.Verify(File_("valid.msi"), "valid.msi", Sigs, TestRoot).Ok);
    }

    [Fact]
    public void UnsignedMsiIsRejected() => Assert.Equal("not signed", MsiAuthenticode.Verify(File_("unsigned.msi"), TestRoot).Reason);

    [Fact]
    public void MsiSignedUnderAnotherRootIsRejected()
    {
        var r = MsiAuthenticode.Verify(File_("otherroot.msi"), TestRoot);
        Assert.False(r.Ok); Assert.StartsWith("signer not trusted", r.Reason);
        Assert.True(MsiAuthenticode.Verify(File_("otherroot.msi"), [Root("other-root.crt"), TestRoot[0]]).Ok);   // the second pin slot works
    }

    [Fact]
    public void TamperedMsiIsRejected()
    {
        var r = MsiAuthenticode.Verify(File_("tampered.msi"), TestRoot);
        Assert.False(r.Ok); Assert.Contains("digest mismatch", r.Reason);
    }

    [Fact]
    public void SignatureWithoutTimestampIsRejected() => Assert.Equal("no timestamp", MsiAuthenticode.Verify(File_("notimestamp.msi"), TestRoot).Reason);

    [Fact]
    public void ProductionPinsDoNotTrustTheTestRoot()
    {
        Assert.Single(UpdateTrust.PinnedRoots);
        Assert.Equal(UpdateTrust.R0Sha256, Convert.ToHexStringLower(SHA256.HashData(UpdateTrust.PinnedRoots[0].RawData)));
        var r = MsiAuthenticode.Verify(File_("valid.msi"), UpdateTrust.PinnedRoots);
        Assert.False(r.Ok); Assert.Contains("not trusted", r.Reason);   // the test TSA is refused first, then the signer would be
    }

    [Fact]
    public void SignaturesMdMustListTheExactBytes()
    {
        var valid = File_("valid.msi");
        Assert.Contains("not listed", UpdateTrust.Verify(valid, "NetPaw-9.9.9.msi", Sigs, TestRoot).Reason);
        Assert.Contains("does not match SIGNATURES.md", UpdateTrust.Verify(valid, "otherroot.msi", Sigs, TestRoot).Reason);
        var map = UpdateTrust.ParseSignatures("| `netpaw-win-x64/NetPaw.exe` | " + new string('a', 64) + " | Authenticode |\n| x | nothex | y |");
        Assert.Equal(new string('a', 64), map["NetPaw.exe"]); Assert.Single(map);
    }

    [Fact]
    public void GarbageIsNotAnMsi()
    {
        Assert.StartsWith("not a valid MSI", MsiAuthenticode.Verify(new byte[4096], TestRoot).Reason);
        var cut = File_("valid.msi")[..1024];
        Assert.False(MsiAuthenticode.Verify(cut, TestRoot).Ok);
    }

    const string Release = """
      {"tag_name":"v0.13.0","html_url":"https://github.com/smol-kitten/netpaw/releases/tag/v0.13.0","prerelease":false,"draft":false,"body":"x","assets":[
      {"name":"NetPaw-0.13.0.msi","browser_download_url":"https://github.com/smol-kitten/netpaw/releases/download/v0.13.0/NetPaw-0.13.0.msi"},
      {"name":"NetPaw-0.13.0-telemetry.msi","browser_download_url":"https://github.com/smol-kitten/netpaw/releases/download/v0.13.0/NetPaw-0.13.0-telemetry.msi"},
      {"name":"SIGNATURES.md","browser_download_url":"https://github.com/smol-kitten/netpaw/releases/download/v0.13.0/SIGNATURES.md"}]}
      """;

    [Fact]
    public void ParsePicksTheInstallerForThisVariantFromThisRepoOnly()
    {
        var i = UpdateChecker.Parse(Release, "0.12.2")!;
        Assert.Equal("NetPaw-0.13.0.msi", i.MsiName); Assert.EndsWith("/v0.13.0/SIGNATURES.md", i.SignaturesUrl);
        Assert.Equal("NetPaw-0.13.0-telemetry.msi", UpdateChecker.Parse(Release, "0.12.2", telemetryBuild: true)!.MsiName);
        var foreign = UpdateChecker.Parse(Release.Replace("https://github.com/smol-kitten/netpaw/releases/download/v0.13.0/NetPaw-0.13.0.msi", "https://evil.example/NetPaw-0.13.0.msi"), "0.12.2")!;
        Assert.Null(foreign.MsiUrl);
    }

    [Fact]
    public async Task DownloadWritesOnlyAVerifiedInstaller()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpaw-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = new UpdateInfo("0.13.0", "u", null, "valid.msi", "https://x/valid.msi", "https://x/SIGNATURES.md");
            Task<byte[]> Fetch(string file, string url) => Task.FromResult(url.EndsWith("SIGNATURES.md") ? File_("SIGNATURES.md") : File_(file));

            var ok = await UpdateChecker.DownloadAndVerify(info, (u, _) => Fetch("valid.msi", u), dir, TestRoot);
            Assert.True(ok.Verified, ok.Reason); Assert.True(File.Exists(ok.Path));

            var bad = await UpdateChecker.DownloadAndVerify(info with { MsiName = "tampered.msi" }, (u, _) => Fetch("tampered.msi", u), dir, TestRoot);
            Assert.False(bad.Verified); Assert.Null(bad.Path); Assert.False(File.Exists(Path.Combine(dir, "tampered.msi")));

            var unsignedRelease = await UpdateChecker.DownloadAndVerify(info with { SignaturesUrl = null }, (u, _) => Fetch("valid.msi", u), dir, TestRoot);
            Assert.Contains("unsigned release", unsignedRelease.Reason);

            var offline = await UpdateChecker.DownloadAndVerify(info, (_, _) => throw new HttpRequestException("offline"), dir, TestRoot);
            Assert.Contains("download failed", offline.Reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
