using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPaw.Updates;
using Xunit;

namespace NetPaw.Tests;

public class CatWingetSourceTests
{
    static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "update");
    static string Manifest => File.ReadAllText(Path.Combine(Dir, "catwinget-manifest.json"));
    static readonly Version V0130 = new(0, 13, 0), V0140 = new(0, 14, 0), V0150 = new(0, 15, 0);

    /// <summary>The live manifest with one edit applied to every installer of the newest version.</summary>
    static string Edited(Action<JsonObject> editInstaller, Action<JsonObject>? editVersion = null)
    {
        var root = JsonNode.Parse(Manifest)!.AsObject();
        var v = root["Data"]!["Versions"]![0]!.AsObject();
        editVersion?.Invoke(v);
        foreach (var inst in v["Installers"]!.AsArray()) editInstaller(inst!.AsObject());
        return root.ToJsonString();
    }

    [Fact]
    public void Live_manifest_resolves_the_newest_stable_x64_machine_msi()
    {
        var r = CatWingetSource.Resolve(Manifest, V0130);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(V0140, r.Update!.Version);
        Assert.StartsWith("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/", r.Update.InstallerUrl);
        Assert.Equal(64, r.Update.Sha256.Length);
        Assert.Equal("{2A863C75-F2C4-4F28-A93B-FB08D27C0EF2}", r.Update.ProductCode);
        Assert.Equal("https://github.com/smol-kitten/netpaw/releases/tag/v0.14.0", r.Update.ReleaseNotesUrl);   // no ReleaseNotesUrl in the locale yet
        Assert.Equal("stable", r.Update.Channel);
    }

    [Fact]
    public void Same_or_older_is_not_an_update_unless_downgrade_is_allowed()
    {
        Assert.False(CatWingetSource.Resolve(Manifest, V0140).Ok);
        Assert.Contains("newest", CatWingetSource.Resolve(Manifest, V0140).Reason);
        var r = CatWingetSource.Resolve(Manifest, V0150);
        Assert.False(r.Ok); Assert.Contains("downgrade not allowed", r.Reason);
        Assert.True(CatWingetSource.Resolve(Manifest, V0150, allowDowngrade: true).Ok);
    }

    [Fact]
    public void Prerelease_channel_is_opt_in_and_stable_never_sees_it()
    {
        var withPre = Edited(_ => { }, v => { v["PackageVersion"] = "0.15.0-rc.1"; v["Channel"] = "prerelease"; });
        var stable = CatWingetSource.Resolve(withPre, V0130);
        Assert.True(stable.Ok); Assert.Equal(new Version(0, 13, 3), stable.Update!.Version);   // the next stable entry, not the rc
        var pre = CatWingetSource.Resolve(withPre, V0130, channel: CatWingetSource.PrereleaseChannel);
        Assert.True(pre.Ok); Assert.Equal(V0150, pre.Update!.Version); Assert.Equal("prerelease", pre.Update.Channel);
        Assert.Contains("no version on the prerelease channel", CatWingetSource.Resolve(Manifest, V0130, channel: CatWingetSource.PrereleaseChannel).Reason);
    }

    [Theory]
    [InlineData("Architecture", "arm64", "no x64 installer")]
    [InlineData("Scope", "user", "no machine-scope installer")]
    [InlineData("InstallerType", "exe", "not msi/wix")]
    [InlineData("InstallerUrl", "https://evil.example/NetPaw.msi", "not allowed: evil.example")]
    [InlineData("InstallerUrl", "http://github.com/smol-kitten/netpaw/releases/download/v0.14.0/NetPaw-0.14.0.msi", "not allowed: github.com")]
    [InlineData("InstallerSha256", "", "no sha256")]
    public void An_installer_that_breaks_a_rule_is_refused_with_the_rule(string field, string value, string reason)
    {
        var r = CatWingetSource.Resolve(Edited(i => i[field] = value), V0130);
        Assert.False(r.Ok); Assert.Contains(reason, r.Reason);
    }

    [Fact]
    public void Wix_counts_as_msi_and_garbage_is_a_reason_not_an_exception()
    {
        Assert.True(CatWingetSource.Resolve(Edited(i => i["InstallerType"] = "wix"), V0130).Ok);
        Assert.Contains("not JSON", CatWingetSource.Resolve("{", V0130).Reason);
        Assert.Contains("no Data.Versions", CatWingetSource.Resolve("{\"Data\":{}}", V0130).Reason);
    }

    [Fact]
    public async Task Download_follows_a_github_302_chain_and_refuses_bad_hops()
    {
        var body = new byte[] { 1, 2, 3 };
        Task<HttpAnswer> Chain(string url, CancellationToken _) => Task.FromResult(url switch
        {
            "https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/NetPaw-0.14.0.msi" => new HttpAnswer(302, "https://release-assets.githubusercontent.com/x/NetPaw-0.14.0.msi", []),
            "https://release-assets.githubusercontent.com/x/NetPaw-0.14.0.msi" => new HttpAnswer(302, "https://objects.githubusercontent.com/y", []),
            "https://objects.githubusercontent.com/y" => new HttpAnswer(200, null, body, 3),
            _ => new HttpAnswer(404, null, []),
        });
        var ok = await CatWingetSource.Download("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/NetPaw-0.14.0.msi", Chain, 1024);
        Assert.NotNull(ok.Result); Assert.Equal("https://objects.githubusercontent.com/y", ok.Result!.Url); Assert.Equal(body, ok.Result.Body);

        Task<HttpAnswer> Evil(string url, CancellationToken _) => Task.FromResult(url.StartsWith("https://github.com/") ? new HttpAnswer(302, "https://evil.example/NetPaw.msi", []) : new HttpAnswer(200, null, body));
        var evil = await CatWingetSource.Download("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/a.msi", Evil, 1024);
        Assert.Null(evil.Result); Assert.Contains("evil.example", evil.Reason);

        Task<HttpAnswer> Http(string url, CancellationToken _) => Task.FromResult(url.StartsWith("https://") ? new HttpAnswer(302, "http://github.com/smol-kitten/netpaw/releases/download/v0.14.0/a.msi", []) : new HttpAnswer(200, null, body));
        var http = await CatWingetSource.Download("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/a.msi", Http, 1024);
        Assert.Null(http.Result); Assert.Contains("not an allowed https host", http.Reason);

        var hops = 0;
        Task<HttpAnswer> Loop(string url, CancellationToken _) { hops++; return Task.FromResult(new HttpAnswer(302, "https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/" + hops + ".msi", [])); }
        var loop = await CatWingetSource.Download("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/0.msi", Loop, 1024);
        Assert.Null(loop.Result); Assert.Contains("more than 3 redirects", loop.Reason); Assert.Equal(4, hops);

        Task<HttpAnswer> Big(string url, CancellationToken _) => Task.FromResult(new HttpAnswer(200, null, new byte[2048], 2048));
        var big = await CatWingetSource.Download("https://github.com/smol-kitten/netpaw/releases/download/v0.14.0/a.msi", Big, 1024);
        Assert.Null(big.Result); Assert.Contains("larger than the cap", big.Reason);
    }

    [Fact]
    public void Verify_needs_the_hash_and_the_signature()
    {
        var signed = File.ReadAllBytes(Path.Combine(Dir, "valid.msi"));
        var root = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(Dir, "test-root.crt"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(signed));
        var ok = CatWingetSource.VerifyManifest(signed, sha.ToUpperInvariant(), [root]);
        Assert.True(ok.Ok, ok.Reason);
        var wrongHash = CatWingetSource.VerifyManifest(signed, new string('0', 64), [root]);
        Assert.False(wrongHash.Ok); Assert.Contains("does not match the manifest", wrongHash.Reason);
        var unsigned = File.ReadAllBytes(Path.Combine(Dir, "unsigned.msi"));
        var hashOkSigBad = CatWingetSource.VerifyManifest(unsigned, Convert.ToHexStringLower(SHA256.HashData(unsigned)), [root]);
        Assert.False(hashOkSigBad.Ok); Assert.Contains("hash matched but the signature did not", hashOkSigBad.Reason);
        var wrongRoot = CatWingetSource.VerifyManifest(signed, sha, UpdateTrust.PinnedRoots);
        Assert.False(wrongRoot.Ok);
    }

    [Fact]
    public void Constants_are_the_pinned_source()
    {
        Assert.Equal("https://winget.goes.moe/packageManifests/smol-kitten.NetPaw", CatWingetSource.ManifestUrl(CatWingetSource.PackageId));
        Assert.True(CatWingetSource.IsAllowedUrl("https://winget.goes.moe/x"));
        Assert.False(CatWingetSource.IsAllowedUrl("https://winget.goes.moe.evil.example/x"));
        Assert.False(CatWingetSource.IsAllowedUrl("ftp://github.com/x"));
    }
}
