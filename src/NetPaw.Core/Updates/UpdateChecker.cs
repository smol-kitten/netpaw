using System.Text.Json;
using NetPaw.Model;

namespace NetPaw.Updates;

public sealed record UpdateInfo(string Version, string Url, string? Headline);

/// <summary>
/// Opt-in, once a day, never downloads: asks the GitHub releases API for the latest tag and says so once per
/// version. Honours the AllowUpdateCheck policy and the user setting; failures are silent (logged by the caller).
/// The fetch is injected so the rules are unit-tested without the network.
/// </summary>
public sealed class UpdateChecker(Func<string, CancellationToken, Task<string>> fetch, string currentVersion)
{
    public const string ReleasesUrl = "https://api.github.com/repos/smol-kitten/netpaw/releases/latest";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Newer release described by the API JSON, or null (same/older, prerelease, draft, unparsable).</summary>
    public static UpdateInfo? Parse(string json, string currentVersion)
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
            return new UpdateInfo(latest.ToString(3), url ?? "https://github.com/smol-kitten/netpaw/releases", headline);
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
        var info = Parse(await fetch(ReleasesUrl, ct), currentVersion);
        if (info is null || info.Version == s.UpdateLastVersionSeen) return null;
        s.UpdateLastVersionSeen = info.Version;
        return info;
    }
}
