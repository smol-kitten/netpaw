using System.Text.Json;
using NetPaw.Model;

namespace NetPaw.Updates;

public sealed record UpdateInfo(string Version, string Url, string? Headline, string? MsiName = null, string? MsiUrl = null, string? SignaturesUrl = null);

/// <summary>Outcome of <see cref="UpdateChecker.DownloadAndVerify"/>: a path only when the file verified.</summary>
public sealed record VerifiedUpdate(bool Verified, string Reason, string? Path = null);

/// <summary>
/// Opt-in, once a day: asks the GitHub releases API for the latest tag and says so once per version. Honours the
/// AllowUpdateCheck policy and the user setting; failures are silent (logged by the caller). The installer is only
/// offered after <see cref="DownloadAndVerify"/> proved it (SIGNATURES.md hash + Authenticode to a pinned root).
/// The fetches are injected so the rules are unit-tested without the network.
/// </summary>
public sealed class UpdateChecker(Func<string, CancellationToken, Task<string>> fetch, string currentVersion, bool telemetryBuild = false)
{
    public const string ReleasesUrl = "https://api.github.com/repos/smol-kitten/netpaw/releases/latest";
    /// <summary>Downloads come from this repository's releases only, whatever the JSON says.</summary>
    public const string AssetPrefix = "https://github.com/smol-kitten/netpaw/releases/download/";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Newer release described by the API JSON, or null (same/older, prerelease, draft, unparsable).</summary>
    public static UpdateInfo? Parse(string json, string currentVersion, bool telemetryBuild = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || !TryVersion(tag, out var latest) || !TryVersion(currentVersion, out var current) || latest <= current) return null;
            var url = r.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            var body = r.TryGetProperty("body", out var b) ? b.GetString() : null;
            var headline = body?.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(l => l.Length > 0);
            // the installer for THIS variant (a telemetry build updates to the telemetry msi) + the release's SIGNATURES.md
            var msiName = $"NetPaw-{tag.TrimStart('v', 'V')}{(telemetryBuild ? "-telemetry" : "")}.msi";
            string? Asset(string name) => r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) && n.GetString() == name)
                    .Select(x => x.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null).FirstOrDefault(d => d?.StartsWith(AssetPrefix, StringComparison.Ordinal) == true)
                : null;
            var msiUrl = Asset(msiName);
            return new UpdateInfo(latest.ToString(3), url ?? "https://github.com/smol-kitten/netpaw/releases", headline,
                msiUrl is null ? null : msiName, msiUrl, Asset("SIGNATURES.md"));
        }
        catch (JsonException) { return null; }
    }

    static bool TryVersion(string s, out Version v)
    {
        s = s.Trim(); if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var dash = s.IndexOf('-'); if (dash > 0) s = s[..dash];
        if (!Version.TryParse(s, out var parsed)) { v = new Version(0, 0, 0); return false; }
        v = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build)); return true;
    }

    /// <summary>true when a request is due: allowed by policy, enabled by the user, and the last check is older than <see cref="Interval"/>.</summary>
    public static bool Due(Settings s, bool allowedByPolicy, DateTimeOffset now) =>
        allowedByPolicy && s.UpdateCheck && (s.UpdateLastCheck is null || now - s.UpdateLastCheck >= Interval);

    /// <summary>
    /// Runs the check when due. Returns the newer release the first time it is seen (once per version), else null.
    /// Updates <see cref="Settings.UpdateLastCheck"/> / <see cref="Settings.UpdateLastVersionSeen"/>; the caller saves.
    /// </summary>
    public async Task<UpdateInfo?> Run(Settings s, bool allowedByPolicy, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Due(s, allowedByPolicy, now)) return null;
        s.UpdateLastCheck = now;
        var info = Parse(await fetch(ReleasesUrl, ct), currentVersion, telemetryBuild);
        if (info is null || info.Version == s.UpdateLastVersionSeen) return null;
        s.UpdateLastVersionSeen = info.Version;
        return info;
    }

    /// <summary>
    /// Downloads the release's installer and SIGNATURES.md and verifies them (<see cref="UpdateTrust.Verify"/>). The
    /// installer is written to <paramref name="dir"/> ONLY when it verified; an unverified download never touches
    /// the disk, so nothing can launch it. Network or size failures come back as not verified, with the reason.
    /// </summary>
    public static async Task<VerifiedUpdate> DownloadAndVerify(UpdateInfo info, Func<string, CancellationToken, Task<byte[]>> fetchBytes,
        string dir, IReadOnlyCollection<System.Security.Cryptography.X509Certificates.X509Certificate2> roots, CancellationToken ct = default)
    {
        if (info.MsiUrl is null || info.MsiName is null) return new(false, "the release has no installer for this build");
        if (info.SignaturesUrl is null) return new(false, "the release has no SIGNATURES.md (unsigned release)");
        byte[] msi, sigs;
        try
        {
            sigs = await fetchBytes(info.SignaturesUrl, ct);
            msi = await fetchBytes(info.MsiUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return new(false, "download failed: " + ex.Message); }
        if (msi.LongLength > UpdateTrust.MaxDownload || sigs.Length > 1024 * 1024) return new(false, "download too large");
        var check = UpdateTrust.Verify(msi, info.MsiName, System.Text.Encoding.UTF8.GetString(sigs), roots);
        if (!check.Ok) return new(false, check.Reason);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.MsiName);
        await File.WriteAllBytesAsync(path, msi, ct);
        return new(true, $"signed by {check.Signer}, timestamped {check.Timestamp:yyyy-MM-dd HH:mm} UTC", path);
    }
}
