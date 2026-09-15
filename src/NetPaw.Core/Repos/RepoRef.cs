namespace NetPaw.Repos;

/// <summary>A configured repository: the index URL and, optionally, the pinned signing key ("url|keyId:base64").</summary>
public sealed record RepoRef(string Url, string? KeyId, string? PublicKey, bool FromPolicy)
{
    public bool Pinned => PublicKey is not null;

    public static RepoRef Parse(string spec, bool fromPolicy = false)
    {
        var parts = spec.Trim().Split('|', 2);
        var url = parts[0].Trim();
        if (parts.Length == 1) return new RepoRef(url, null, null, fromPolicy);
        var key = parts[1].Trim();
        var colon = key.IndexOf(':');
        return colon < 0 ? new RepoRef(url, PackSigner.KeyIdOf(key), key, fromPolicy) : new RepoRef(url, key[..colon], key[(colon + 1)..], fromPolicy);
    }

    public string ToSpec() => PublicKey is null ? Url : $"{Url}|{KeyId}:{PublicKey}";
    public override string ToString() => Url;
}

public enum TrustState { Verified, Unsigned, BadSignature, Untrusted }

/// <summary>A fetched (or cached) pack plus how far it can be trusted.</summary>
public sealed record RepoState(RepoRef Repo, Pack? Pack, TrustState Trust, bool FromCache, DateTimeOffset? Fetched, string? Error)
{
    public int Count => Pack?.Entries.Count ?? 0;
    public string Name => Pack?.Name is { Length: > 0 } n ? n : new Uri(Repo.Url).Host;
    public bool Usable(bool allowUnsigned) => Pack is not null && (Trust == TrustState.Verified || (allowUnsigned && Trust == TrustState.Unsigned));
}
