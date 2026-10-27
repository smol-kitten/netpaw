using NetPaw.Ipc;
using System.Security.Cryptography.X509Certificates;
using NetPaw.Planning;

namespace NetPaw.Updates;

/// <summary>
/// What the installer needs from the host: the download folder (created with the service DACL, reparse and owner
/// checked), one HTTP round trip, and the msiexec start. Injected so the orchestration is unit-tested on Linux.
/// </summary>
public interface IUpdateEnvironment
{
    /// <summary>The verified download folder, or a reason it is not usable.</summary>
    (string? Dir, string Reason) EnsureDownloadDir();
    /// <summary>One round trip without following redirects (the installer follows them itself).</summary>
    Task<HttpAnswer> Get(string url, CancellationToken ct);
    /// <summary>Starts msiexec detached on an already-open, read-only handle's path; returns the process id or a reason.</summary>
    (int? Pid, string Reason) StartInstaller(string msiPath, string logPath);
    /// <summary>Roots the signature must chain to.</summary>
    IReadOnlyCollection<X509Certificate2> PinnedRoots { get; }
    IReadOnlyCollection<X509Certificate2>? Intermediates { get; }
}

/// <summary>
/// The CatWinget install path on the service side (docs/PLAN-catwinget-update.md): resolve from the pinned source,
/// download with checked redirects, hash the file on disk, verify Authenticode on the same bytes, hold a read-only
/// handle, start msiexec detached, answer the client. One install at a time. Every step is an audit line.
/// </summary>
public sealed class UpdateInstaller(IUpdateEnvironment env, Version current, Action<string> audit)
{
    static readonly SemaphoreSlim One = new(1, 1);
    public const string MutexName = @"Global\NetPaw.InstallUpdate";
    /// <summary>How long the verified file stays open read-only after msiexec was started (the host delays msiexec by 2 s).</summary>
    public static TimeSpan HoldAfterStart { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<ApplyReport> Install(string? version, string? channel, bool allowDowngrade, CancellationToken ct = default)
    {
        var plan = new ApplyPlan { Title = "update from CatWinget", Adapter = "" };
        if (!One.Wait(0)) return Refuse(plan, "an update is already running");
        try
        {
            var ch = string.Equals(channel, CatWingetSource.PrereleaseChannel, StringComparison.OrdinalIgnoreCase) ? CatWingetSource.PrereleaseChannel : CatWingetSource.StableChannel;
            // resolve
            var manifest = await CatWingetSource.Download(CatWingetSource.ManifestUrl(CatWingetSource.PackageId), env.Get, 4 * 1024 * 1024, ct);
            if (manifest.Result is null) return Refuse(plan, "resolve: " + manifest.Reason);
            var resolved = CatWingetSource.Resolve(System.Text.Encoding.UTF8.GetString(manifest.Result.Body), current, ch, allowDowngrade);
            if (!resolved.Ok) return Refuse(plan, "resolve: " + resolved.Reason);
            var upd = resolved.Update!;
            if (version is not null && (!CatWingetSource.TryVersion(version, out var want) || want != upd.Version))
                return Refuse(plan, $"resolve: the newest {ch} version is {upd.Version.ToString(3)}, not {version}");
            audit($"update: resolve {upd.Version.ToString(3)} ({ch}) from {CatWingetSource.BaseUrl}");
            // download
            var (dir, why) = env.EnsureDownloadDir();
            if (dir is null) return Refuse(plan, "folder: " + why);
            var dl = await CatWingetSource.Download(upd.InstallerUrl, env.Get, UpdateTrust.MaxDownload, ct);
            if (dl.Result is null) return Refuse(plan, dl.Reason);
            audit($"update: download {dl.Result.Body.LongLength} bytes from {new Uri(dl.Result.Url).Host}");
            var msiPath = Path.Combine(dir, $"NetPaw-{upd.Version.ToString(3)}.msi");
            var logPath = Path.Combine(dir, $"NetPaw-{upd.Version.ToString(3)}.install.log");
            await File.WriteAllBytesAsync(msiPath, dl.Result.Body, ct);
            // Verify the bytes read back from disk through a handle opened with FileShare.Read only (no write, no
            // delete share): nothing can swap or truncate the file between the check and msiexec's own open. The handle
            // stays open until msiexec has opened the package (the host starts it after a 2 s delay), then is released
            // on a background task so the client gets its answer at once.
            byte[] onDisk;
            FileStream hold;
            try { hold = new FileStream(msiPath, FileMode.Open, FileAccess.Read, FileShare.Read); onDisk = new byte[hold.Length]; await hold.ReadExactlyAsync(onDisk, ct); }
            catch (IOException ex) { return Refuse(plan, "folder: cannot read back the download: " + ex.Message); }
            var check = CatWingetSource.VerifyManifest(onDisk, upd.Sha256, env.PinnedRoots, env.Intermediates);
            if (!check.Ok) { hold.Dispose(); TryDelete(msiPath); return Refuse(plan, "verify: " + check.Reason); }
            audit("update: verify hash ok");
            audit($"update: verify authenticode ok {check.Signer}");
            var (pid, reason) = env.StartInstaller(msiPath, logPath);
            if (pid is null) { hold.Dispose(); return Refuse(plan, "install: " + reason); }
            audit($"update: install start pid {pid} {upd.Version.ToString(3)} log {logPath}");
            _ = Task.Run(async () => { await Task.Delay(HoldAfterStart); hold.Dispose(); audit($"update: read-only handle released {HoldAfterStart.TotalSeconds:0} s after the install start"); });
            plan.Steps.Add(new Step($"install NetPaw {upd.Version.ToString(3)}", "msiexec", $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\""));
            var outcome = new ApplyOutcome();
            outcome.Results.Add(new StepResult(plan.Steps[0], 0, "started"));
            return ApplyReport.From(plan, outcome);
        }
        finally { One.Release(); }
    }

    ApplyReport Refuse(ApplyPlan plan, string why)
    {
        audit("update: refused " + why);
        plan.Warnings.Add(why);
        // A refused update is a failed critical step, so Success is false and the client sees the reason as the step's output.
        var step = new Step("update refused", "netpaw-svc", "update", Critical: true);
        plan.Steps.Add(step);
        var outcome = new ApplyOutcome { Aborted = true };
        outcome.Results.Add(new StepResult(step, 1, why));
        return ApplyReport.From(plan, outcome);
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
