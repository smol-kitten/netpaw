using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetPaw.Ipc;
using NetPaw.Updates;
using Xunit;

namespace NetPaw.Tests;

public class UpdateInstallerTests
{
    static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "update");
    static readonly byte[] Valid = File.ReadAllBytes(Path.Combine(Dir, "valid.msi"));
    static readonly byte[] Unsigned = File.ReadAllBytes(Path.Combine(Dir, "unsigned.msi"));
    static readonly X509Certificate2 TestRoot = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(Dir, "test-root.crt"));

    /// <summary>A fake host: a manifest that points at /msi, a byte array served there, a temp download dir, msiexec recorded.</summary>
    sealed class Env(byte[] served, string sha, bool dirOk = true) : IUpdateEnvironment
    {
        public readonly string Tmp = Path.Combine(Path.GetTempPath(), "np-upd-" + Guid.NewGuid().ToString("N"));
        public (string, string)? Started; public int Gets;
        public IReadOnlyCollection<X509Certificate2> PinnedRoots => [TestRoot];
        public IReadOnlyCollection<X509Certificate2>? Intermediates => null;
        public (string? Dir, string Reason) EnsureDownloadDir() { if (!dirOk) return (null, "folder owned by someone else"); Directory.CreateDirectory(Tmp); return (Tmp, "ok"); }
        public Task<HttpAnswer> Get(string url, CancellationToken ct)
        {
            Gets++;
            if (url == CatWingetSource.ManifestUrl(CatWingetSource.PackageId))
                return Task.FromResult(new HttpAnswer(200, null, System.Text.Encoding.UTF8.GetBytes(Manifest(sha))));
            if (url == "https://github.com/smol-kitten/netpaw/releases/download/v9.9.9/NetPaw-9.9.9.msi")
                return Task.FromResult(new HttpAnswer(302, "https://objects.githubusercontent.com/blob", []));
            if (url == "https://objects.githubusercontent.com/blob") return Task.FromResult(new HttpAnswer(200, null, served, served.LongLength));
            return Task.FromResult(new HttpAnswer(404, null, []));
        }
        public (int? Pid, string Reason) StartInstaller(string msiPath, string logPath) { Started = (msiPath, logPath); return (4242, "ok"); }
        static string Manifest(string sha) => """
            {"Data":{"PackageIdentifier":"smol-kitten.NetPaw","Versions":[{"PackageVersion":"9.9.9","Channel":null,"Installers":[{"InstallerType":"msi","Scope":"machine","Architecture":"x64",
            "InstallerUrl":"https://github.com/smol-kitten/netpaw/releases/download/v9.9.9/NetPaw-9.9.9.msi","InstallerSha256":"SHA","ProductCode":"{P}"}]}]}}
            """.Replace("SHA", sha);
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public async Task Happy_path_resolves_downloads_verifies_twice_and_starts_msiexec_with_audit_lines()
    {
        var env = new Env(Valid, Sha(Valid)); var audit = new List<string>();
        var report = await new UpdateInstaller(env, new Version(0, 16, 0), audit.Add).Install(null, null, false);
        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.NotNull(env.Started);
        Assert.Equal(Path.Combine(env.Tmp, "NetPaw-9.9.9.msi"), env.Started!.Value.Item1);
        Assert.Equal(Valid, await File.ReadAllBytesAsync(env.Started.Value.Item1));
        Assert.Collection(audit,
            l => Assert.StartsWith("update: resolve 9.9.9 (stable)", l),
            l => Assert.StartsWith("update: download " + Valid.Length + " bytes from objects.githubusercontent.com", l),
            l => Assert.Equal("update: verify hash ok", l),
            l => Assert.StartsWith("update: verify authenticode ok", l),
            l => Assert.StartsWith("update: install start pid 4242 9.9.9", l));
        Assert.Contains(report.Steps, s => s.Command.Contains("/qn /norestart /l*v"));
    }

    [Fact]
    public async Task Hash_mismatch_refuses_deletes_the_file_and_never_starts_msiexec()
    {
        var env = new Env(Valid, new string('0', 64)); var audit = new List<string>();
        var report = await new UpdateInstaller(env, new Version(0, 16, 0), audit.Add).Install(null, null, false);
        Assert.False(report.Success); Assert.Null(env.Started);
        Assert.Contains(audit, l => l.StartsWith("update: refused verify: sha256"));
        Assert.False(File.Exists(Path.Combine(env.Tmp, "NetPaw-9.9.9.msi")));
    }

    [Fact]
    public async Task Right_hash_but_unsigned_is_refused()
    {
        var env = new Env(Unsigned, Sha(Unsigned)); var audit = new List<string>();
        var report = await new UpdateInstaller(env, new Version(0, 16, 0), audit.Add).Install(null, null, false);
        Assert.False(report.Success); Assert.Null(env.Started);
        Assert.Contains(audit, l => l.Contains("hash matched but the signature did not"));
    }

    [Fact]
    public async Task Not_newer_untrusted_folder_and_wrong_pinned_version_are_refused_before_any_download()
    {
        var env = new Env(Valid, Sha(Valid)); var audit = new List<string>();
        var inst = new UpdateInstaller(env, new Version(9, 9, 9), audit.Add);
        var same = await inst.Install(null, null, false);
        Assert.False(same.Success); Assert.Contains(audit, l => l.Contains("refused resolve: 9.9.9 is the newest"));
        Assert.Equal(1, env.Gets);   // only the manifest was fetched
        var badDir = new Env(Valid, Sha(Valid), dirOk: false); var a2 = new List<string>();
        Assert.False((await new UpdateInstaller(badDir, new Version(0, 1, 0), a2.Add).Install(null, null, false)).Success);
        Assert.Contains(a2, l => l.Contains("refused folder: folder owned by someone else")); Assert.Null(badDir.Started);
        var a3 = new List<string>();
        Assert.False((await new UpdateInstaller(new Env(Valid, Sha(Valid)), new Version(0, 1, 0), a3.Add).Install("8.0.0", null, false)).Success);
        Assert.Contains(a3, l => l.Contains("not 8.0.0"));
    }

    [Fact]
    public void The_intent_is_admin_by_default_with_an_nco_floor_and_needs_the_capability()
    {
        Assert.Equal(Tier.Admin, PolicyTiers.DefaultFor(PolicyIntent.InstallUpdate));
        Assert.Equal(Tier.NetworkConfigOperators, PolicyTiers.FloorFor(PolicyIntent.InstallUpdate));
        var r = IntentHandlerTests.MakeRigFor(new() { ["AllowCatWingetUpdate"] = 0 });
        var resp = r.Handler.Handle(ServiceChange.InstallUpdate(), "PC\\admin (session 1)", Tier.Admin);
        Assert.False(resp.Ok); Assert.Contains("denied by policy", resp.Error);
        var noInstaller = IntentHandlerTests.MakeRigFor(null);
        var resp2 = noInstaller.Handler.Handle(ServiceChange.InstallUpdate(), "PC\\admin (session 1)", Tier.Admin);
        Assert.False(resp2.Ok); Assert.Contains("not available in this service build", resp2.Error);
        var user = noInstaller.Handler.Handle(ServiceChange.InstallUpdate(), "PC\\anna (session 2)", Tier.StandardUser);
        Assert.False(user.Ok); Assert.Contains("needs tier Admin", user.Error);
    }
}
