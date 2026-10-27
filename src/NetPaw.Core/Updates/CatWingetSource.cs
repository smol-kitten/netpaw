using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace NetPaw.Updates;

/// <summary>One installable version resolved from the CatWinget source (docs/PLAN-catwinget-update.md).</summary>
public sealed record CatWingetUpdate(Version Version, string InstallerUrl, string Sha256, string? ProductCode, string ReleaseNotesUrl, string Channel);

/// <summary>Outcome of a resolve: an update, or the reason there is none.</summary>
public sealed record CatWingetResolve(CatWingetUpdate? Update, string Reason)
{
    public bool Ok => Update is not null;
}

/// <summary>A fetched document: the final URL after redirects and the bytes.</summary>
public sealed record Fetched(string Url, byte[] Body);

/// <summary>What one HTTP round trip returned: a status, an optional Location, the body.</summary>
public sealed record HttpAnswer(int Status, string? Location, byte[] Body, long? ContentLength = null);

/// <summary>
/// Resolves NetPaw updates from the CatWinget REST source (winget REST API). Pure: the manifest JSON and the
/// HTTP round trips are injected, so every rule is unit-tested. The base URL, the package id and the download
/// hosts are constants of the build; nothing here reads a URL from settings or from a request.
/// </summary>
public static class CatWingetSource
{
    public const string BaseUrl = "https://winget.goes.moe";
    public const string PackageId = "smol-kitten.NetPaw";
    /// <summary>A prerelease published under its own identifier, if catwinget chooses that; null = same identifier, Channel field.</summary>
    public const string? PrereleasePackageId = null;
    public const string StableChannel = "stable";
    public const string PrereleaseChannel = "prerelease";
    public const int MaxRedirects = 3;
    public static string ManifestUrl(string packageId) => $"{BaseUrl}/packageManifests/{packageId}";

    /// <summary>Hosts an installer may be downloaded from, including GitHub's redirect targets for release assets.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "winget.goes.moe", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
    };

    static readonly HashSet<string> InstallerTypes = new(StringComparer.OrdinalIgnoreCase) { "msi", "wix" };

    /// <summary>True when the URL is https and its host is on <see cref="AllowedHosts"/>.</summary>
    public static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host);

    /// <summary>
    /// Picks the newest installable version from a packageManifests document: channel match, x64, machine scope,
    /// msi/wix, an allow-listed https installer URL, and newer than <paramref name="current"/> unless
    /// <paramref name="allowDowngrade"/>. The reason names the first rule that left nothing.
    /// </summary>
    public static CatWingetResolve Resolve(string manifestJson, Version current, string channel = StableChannel, bool allowDowngrade = false,
        string architecture = "x64", string scope = "machine")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(manifestJson); }
        catch (JsonException ex) { return new(null, "manifest is not JSON: " + ex.Message); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Data", out var data) || !data.TryGetProperty("Versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                return new(null, "manifest has no Data.Versions");
            var wantPre = string.Equals(channel, PrereleaseChannel, StringComparison.OrdinalIgnoreCase);
            var candidates = new List<(Version V, JsonElement El)>();
            var seen = 0;
            foreach (var v in versions.EnumerateArray())
            {
                seen++;
                var ch = Str(v, "Channel");
                var isPre = string.Equals(ch, PrereleaseChannel, StringComparison.OrdinalIgnoreCase);
                if (isPre != wantPre) continue;
                if (!TryVersion(Str(v, "PackageVersion"), out var ver)) continue;
                candidates.Add((ver, v));
            }
            if (seen == 0) return new(null, "manifest lists no versions");
            if (candidates.Count == 0) return new(null, $"no version on the {channel} channel");
            var best = candidates.MaxBy(c => c.V);
            if (best.V <= current && !allowDowngrade)
                return new(null, best.V == current ? $"{current.ToString(3)} is the newest on the {channel} channel" : $"newest {best.V.ToString(3)} is older than {current.ToString(3)}; downgrade not allowed");
            if (!best.El.TryGetProperty("Installers", out var installers) || installers.ValueKind != JsonValueKind.Array)
                return new(null, $"{best.V.ToString(3)} has no installers");
            string? why = null;
            foreach (var inst in installers.EnumerateArray())
            {
                if (!string.Equals(Str(inst, "Architecture"), architecture, StringComparison.OrdinalIgnoreCase)) { why ??= $"no {architecture} installer"; continue; }
                if (!string.Equals(Str(inst, "Scope"), scope, StringComparison.OrdinalIgnoreCase)) { why ??= $"no {scope}-scope installer"; continue; }
                var type = Str(inst, "InstallerType") ?? "";
                if (!InstallerTypes.Contains(type)) { why = $"installer type '{type}' is not msi/wix"; continue; }
                var url = Str(inst, "InstallerUrl") ?? "";
                if (!IsAllowedUrl(url)) { why = $"installer URL host is not allowed: {HostOf(url)}"; continue; }
                var sha = (Str(inst, "InstallerSha256") ?? "").ToLowerInvariant();
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) { why = "installer has no sha256"; continue; }
                var notes = Str(best.El.TryGetProperty("DefaultLocale", out var loc) ? loc : default, "ReleaseNotesUrl")
                            ?? $"https://github.com/smol-kitten/netpaw/releases/tag/v{best.V.ToString(3)}";
                return new(new CatWingetUpdate(best.V, url, sha, Str(inst, "ProductCode"), notes, wantPre ? PrereleaseChannel : StableChannel), "ok");
            }
            return new(null, $"{best.V.ToString(3)}: {why ?? "no usable installer"}");
        }
    }

    /// <summary>
    /// GETs a URL following redirects by hand: at most <see cref="MaxRedirects"/> hops, https only, every hop's host
    /// on the allow-list, Content-Length and the body capped at <paramref name="maxBytes"/>. <paramref name="get"/>
    /// performs one round trip without following redirects.
    /// </summary>
    public static async Task<(Fetched? Result, string Reason)> Download(string url, Func<string, CancellationToken, Task<HttpAnswer>> get, long maxBytes, CancellationToken ct = default)
    {
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowedUrl(url)) return (null, $"download refused: {HostOf(url)} is not an allowed https host");
            var a = await get(url, ct);
            if (a.Status is >= 300 and < 400)
            {
                if (hop >= MaxRedirects) return (null, $"download refused: more than {MaxRedirects} redirects");
                if (string.IsNullOrEmpty(a.Location)) return (null, "download refused: redirect without a Location");
                url = Uri.TryCreate(new Uri(url), a.Location, out var next) ? next.ToString() : a.Location;
                continue;
            }
            if (a.Status != 200) return (null, $"download failed: HTTP {a.Status}");
            if (a.ContentLength is { } len && len > maxBytes) return (null, "download refused: larger than the cap");
            if (a.Body.LongLength > maxBytes) return (null, "download refused: larger than the cap");
            return (new Fetched(url, a.Body), "ok");
        }
    }

    /// <summary>
    /// Both checks the plan requires before an installer may run: the manifest's sha256 must match the bytes, AND the
    /// Authenticode signature must chain to a pinned root. The hash alone is never enough.
    /// </summary>
    public static SignatureCheck VerifyManifest(byte[] msi, string manifestSha256, IReadOnlyCollection<X509Certificate2> roots, IReadOnlyCollection<X509Certificate2>? intermediates = null)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(msi));
        if (!actual.Equals(manifestSha256, StringComparison.OrdinalIgnoreCase)) return new(false, $"sha256 {actual[..12]}… does not match the manifest ({manifestSha256[..Math.Min(12, manifestSha256.Length)]}…)");
        var sig = MsiAuthenticode.Verify(msi, roots, intermediates ?? UpdateTrust.Intermediates);
        return sig.Ok ? sig : new(false, "hash matched but the signature did not: " + sig.Reason);
    }

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(no host)";

    /// <summary>"0.14.0", "v0.14.0" or "0.15.0-rc.1" → 0.14.0 / 0.15.0 (three parts, prerelease suffix dropped).</summary>
    public static bool TryVersion(string? s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim(); if (s[0] is 'v' or 'V') s = s[1..];
        var dash = s.IndexOf('-'); if (dash > 0) s = s[..dash];
        if (!Version.TryParse(s, out var parsed)) return false;
        v = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build)); return true;
    }
}
