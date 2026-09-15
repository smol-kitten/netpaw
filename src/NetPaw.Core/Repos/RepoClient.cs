using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetPaw.Repos;

/// <summary>Where a pack's bytes come from: HTTP in production, a dictionary in tests.</summary>
public interface IPackFetcher
{
    /// <summary>Returns (status, body, etag). 304 = not modified. Throws HttpRequestException/TaskCanceledException when offline.</summary>
    Task<(HttpStatusCode Status, string? Body, string? ETag)> Fetch(string url, string? etag, CancellationToken ct);
}

public sealed class HttpPackFetcher : IPackFetcher
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
    static HttpPackFetcher() => Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetPaw", "0.2.0"));

    public async Task<(HttpStatusCode, string?, string?)> Fetch(string url, string? etag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotModified) return (res.StatusCode, null, etag);
        var body = res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct) : null;
        return (res.StatusCode, body, res.Headers.ETag?.Tag);
    }
}

/// <summary>
/// Fetches, verifies and caches packs. Network only on <see cref="Sync"/>; <see cref="LoadCached"/>
/// never touches the network, so start-up is offline-safe. Cache: one .json + .meta per URL hash.
/// </summary>
public sealed class RepoClient(string cacheDir, IPackFetcher? fetcher = null)
{
    readonly IPackFetcher _fetcher = fetcher ?? new HttpPackFetcher();
    public string CacheDir { get; } = cacheDir;

    sealed record Meta(string? ETag, DateTimeOffset Fetched);

    string BaseName(string url) => Path.Combine(CacheDir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant())))[..16]);

    public RepoState LoadCached(RepoRef repo)
    {
        var json = BaseName(repo.Url) + ".json";
        if (!File.Exists(json)) return new RepoState(repo, null, TrustState.Unsigned, true, null, "not synced yet");
        try
        {
            var meta = ReadMeta(repo.Url);
            return Evaluate(repo, PackJson.Parse(File.ReadAllText(json)), true, meta?.Fetched);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new RepoState(repo, null, TrustState.Unsigned, true, null, "cache unreadable: " + ex.Message); }
    }

    public async Task<RepoState> Sync(RepoRef repo, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var meta = ReadMeta(repo.Url);
        try
        {
            if (!repo.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !repo.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return new RepoState(repo, null, TrustState.Untrusted, false, null, "only https:// (or file:// for testing) URLs are accepted");
            var (status, body, etag) = await _fetcher.Fetch(repo.Url, meta?.ETag, ct);
            if (status == HttpStatusCode.NotModified) { var cached = LoadCached(repo); Touch(repo.Url, etag); return cached with { FromCache = false, Fetched = DateTimeOffset.Now }; }
            if (body is null) return LoadCached(repo) with { Error = $"HTTP {(int)status}" };
            var pack = PackJson.Parse(body);
            var state = Evaluate(repo, pack, false, DateTimeOffset.Now);
            if (state.Trust == TrustState.BadSignature) return state; // never cache a forgery over a good copy
            File.WriteAllText(BaseName(repo.Url) + ".json", body);
            Touch(repo.Url, etag);
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            var cached = LoadCached(repo);
            return cached with { Error = (cached.Pack is null ? "" : "using cached copy — ") + ex.Message };
        }
    }

    RepoState Evaluate(RepoRef repo, Pack pack, bool fromCache, DateTimeOffset? fetched)
    {
        var badHashes = PackJson.VerifyHashes(pack);
        if (badHashes.Count > 0) return new RepoState(repo, null, TrustState.BadSignature, fromCache, fetched, $"entry hash mismatch: {string.Join(", ", badHashes.Take(3))}");
        TrustState trust;
        if (repo.Pinned) trust = PackSigner.Verify(pack, repo.PublicKey!) ? TrustState.Verified : TrustState.BadSignature;
        else trust = TrustState.Unsigned; // signed or not, without a pinned key we cannot vouch for it
        if (trust == TrustState.BadSignature) return new RepoState(repo, null, trust, fromCache, fetched, pack.Signature is null ? "index is not signed but a key is pinned" : "signature does not match the pinned key");
        return new RepoState(repo, pack, trust, fromCache, fetched, null);
    }

    Meta? ReadMeta(string url)
    {
        var f = BaseName(url) + ".meta";
        try { return File.Exists(f) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(f)) : null; } catch (JsonException) { return null; }
    }

    void Touch(string url, string? etag) => File.WriteAllText(BaseName(url) + ".meta", JsonSerializer.Serialize(new Meta(etag, DateTimeOffset.Now)));

    public void Forget(string url)
    {
        foreach (var f in new[] { BaseName(url) + ".json", BaseName(url) + ".meta" }) if (File.Exists(f)) File.Delete(f);
    }
}
