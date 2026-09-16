using System.Net;
using NetPaw.Model;
using NetPaw.Repos;
using NetPaw.Reach;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

sealed class FakeFetcher : IPackFetcher
{
    public Dictionary<string, (HttpStatusCode, string?, string?)> Responses { get; } = [];
    public bool Offline { get; set; }
    public int Calls { get; private set; }
    public string? LastETagSent { get; private set; }
    public Task<(HttpStatusCode, string?, string?)> Fetch(string url, string? etag, CancellationToken ct)
    {
        Calls++; LastETagSent = etag;
        if (Offline) throw new HttpRequestException("no route to host");
        if (!Responses.TryGetValue(url, out var r)) return Task.FromResult((HttpStatusCode.NotFound, (string?)null, (string?)null));
        if (etag is not null && etag == r.Item3) return Task.FromResult((HttpStatusCode.NotModified, (string?)null, etag));
        return Task.FromResult(r);
    }
}

public class PackFormatTests
{
    static Pack Sample() => new()
    {
        Name = "test", Entries =
        [
            new PackEntry { Id = "mt", Vendor = "MikroTik", Model = "RouterOS", Kind = "preset", Ip = "192.168.88.1", Prefix = 24, Note = "admin" },
            new PackEntry { Id = "lab", Vendor = "Acme", Model = "Lab", Kind = "profile", Profile = new Profile { Name = "Acme lab", Addresses = [IpAddr.Parse("10.9.0.5/16")], Gateway = "10.9.0.1" } },
        ],
    };

    [Fact]
    public void CanonicalJsonIsSortedCompactAndStable()
    {
        var a = PackJson.Canonical(System.Text.Json.Nodes.JsonNode.Parse("""{ "b": 1, "a": [ {"z": null, "y": "x"} ], "c": {"k": true} }"""));
        Assert.Equal("""{"a":[{"y":"x"}],"b":1,"c":{"k":true}}""", System.Text.Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void EntryHashIgnoresItsOwnFieldAndFormatting()
    {
        var p = Sample();
        var h1 = PackJson.EntryHash(p.Entries[0]);
        p.Entries[0].Sha256 = "whatever";
        Assert.Equal(h1, PackJson.EntryHash(p.Entries[0]));
        var reparsed = PackJson.Parse(PackJson.Serialize(p));
        Assert.Equal(h1, PackJson.EntryHash(reparsed.Entries[0]));
        p.Entries[0].Note = "changed";
        Assert.NotEqual(h1, PackJson.EntryHash(p.Entries[0]));
    }

    [Fact]
    public void SignAndVerifyRoundTripAndTamperDetection()
    {
        var key = PackSigner.Generate();
        Assert.Equal(8, key.KeyId.Length);
        var p = Sample();
        PackSigner.Sign(p, key.PrivateKeyPem);
        Assert.All(p.Entries, e => Assert.NotNull(e.Sha256));
        Assert.Equal(key.KeyId, p.Signature!.KeyId);
        var json = PackJson.Serialize(p);
        var back = PackJson.Parse(json);
        Assert.True(PackSigner.Verify(back, key.PublicKeyBase64));
        Assert.Empty(PackJson.VerifyHashes(back));
        var tampered = PackJson.Parse(json.Replace("192.168.88.1", "10.0.0.1"));   // tamper the published file
        Assert.Single(PackJson.VerifyHashes(tampered));
        Assert.False(PackSigner.Verify(tampered, key.PublicKeyBase64));
        Assert.False(PackSigner.Verify(p, PackSigner.Generate().PublicKeyBase64)); // wrong key
        Assert.False(PackSigner.Verify(p, "not-base64"));
    }

    [Fact]
    public void HashesAndSignatureAreOverThePublishedJsonNotTheModel()
    {
        var key = PackSigner.Generate();
        var p = Sample(); PackSigner.Sign(p, key.PrivateKeyPem);
        var json = PackJson.Serialize(p);
        // A future NetPaw with more Profile fields parses the same file: must still verify.
        var back = PackJson.Parse(json);
        Assert.Empty(PackJson.VerifyHashes(back)); Assert.True(PackSigner.Verify(back, key.PublicKeyBase64));
        // An unknown extra field in the file is part of what was signed: keep it when re-serializing, and it changes the hash if edited.
        var edited = json.Replace("\"note\": \"admin\"", "\"note\": \"admin\", \"extra\": 1");
        var withExtra = PackJson.Parse(edited);
        Assert.Single(PackJson.VerifyHashes(withExtra));
        Assert.Contains("\"extra\"", PackJson.Serialize(withExtra));
    }

    [Fact]
    public void BuildOnAnExistingIndexWritesTheNewEntries()
    {
        var key = PackSigner.Generate();
        var p = Sample(); PackSigner.Sign(p, key.PrivateKeyPem);
        var parsed = PackJson.Parse(PackJson.Serialize(p));
        parsed.Entries.RemoveAll(e => e.Id == "lab");
        parsed.Entries.Add(new PackEntry { Id = "new", Vendor = "New", Model = "Box", Kind = "preset", Ip = "10.5.0.1" });
        parsed.RawEntries = null;                       // what `pack build` does: the model is the source of truth again
        PackJson.StampHashes(parsed); parsed.Signature = null;
        var written = PackJson.Serialize(parsed);
        Assert.Contains("\"new\"", written); Assert.DoesNotContain("\"lab\"", written);
        var back = PackJson.Parse(written);
        Assert.Empty(PackJson.VerifyHashes(back));
        Assert.DoesNotContain("autoSwitch", written);   // default-valued auto-switch fields are never emitted (0.5.x clients hash the model)
    }

    [Fact]
    public void ShippedCommunityPackStillVerifies()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent) { var c = Path.Combine(d.FullName, "packs", "community", "index.json"); if (File.Exists(c)) { path = c; break; } }
        if (path is null) return; // not in the repo tree (packaged test run)
        var pack = PackJson.Parse(File.ReadAllText(path));
        Assert.Empty(PackJson.VerifyHashes(pack));
        Assert.True(PackSigner.Verify(pack, File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "public.txt")).Trim()));
    }

    [Fact]
    public void RepoRefParsing()
    {
        var plain = RepoRef.Parse("https://x/index.json");
        Assert.False(plain.Pinned); Assert.Equal("https://x/index.json", plain.ToSpec());
        var key = PackSigner.Generate();
        var pinned = RepoRef.Parse($"https://x/index.json|{key.KeyId}:{key.PublicKeyBase64}", fromPolicy: true);
        Assert.True(pinned.Pinned); Assert.Equal(key.KeyId, pinned.KeyId); Assert.True(pinned.FromPolicy);
        var derived = RepoRef.Parse($"https://x/index.json|{key.PublicKeyBase64}");
        Assert.Equal(key.KeyId, derived.KeyId);
    }
}

public class RepoClientTests
{
    const string Url = "https://packs.example/index.json";
    static readonly PackSigner.KeyPair Key = PackSigner.Generate();

    static string SignedJson(string note = "n")
    {
        var p = new Pack { Name = "example", Entries = [new PackEntry { Id = "a", Vendor = "V", Model = "M", Ip = "10.5.5.1", Note = note }] };
        PackSigner.Sign(p, Key.PrivateKeyPem);
        return PackJson.Serialize(p);
    }

    static (RepoClient, FakeFetcher) Make() { var f = new FakeFetcher(); return (new RepoClient(Directory.CreateTempSubdirectory().FullName, f), f); }

    [Fact]
    public async Task SyncCachesAndUsesETag()
    {
        var (c, f) = Make();
        f.Responses[Url] = (HttpStatusCode.OK, SignedJson(), "\"v1\"");
        var r = RepoRef.Parse($"{Url}|{Key.KeyId}:{Key.PublicKeyBase64}");
        var s1 = await c.Sync(r);
        Assert.Equal(TrustState.Verified, s1.Trust); Assert.False(s1.FromCache); Assert.Equal(1, s1.Count);
        var s2 = await c.Sync(r);
        Assert.Equal("\"v1\"", f.LastETagSent); Assert.Equal(TrustState.Verified, s2.Trust); Assert.Equal(1, s2.Count);
        var cached = c.LoadCached(r);
        Assert.True(cached.FromCache); Assert.Equal(1, cached.Count); Assert.NotNull(cached.Fetched);
    }

    [Fact]
    public async Task OfflineFallsBackToCache()
    {
        var (c, f) = Make();
        f.Responses[Url] = (HttpStatusCode.OK, SignedJson(), null);
        var r = RepoRef.Parse(Url);
        await c.Sync(r);
        f.Offline = true;
        var s = await c.Sync(r);
        Assert.Equal(1, s.Count); Assert.True(s.FromCache); Assert.Contains("cached copy", s.Error);
        var never = await c.Sync(RepoRef.Parse("https://other/index.json"));
        Assert.Null(never.Pack); Assert.NotNull(never.Error);
    }

    [Fact]
    public async Task TrustStates()
    {
        var (c, f) = Make();
        f.Responses[Url] = (HttpStatusCode.OK, SignedJson(), null);
        Assert.Equal(TrustState.Unsigned, (await c.Sync(RepoRef.Parse(Url))).Trust);                                   // no pin → cannot vouch
        var other = PackSigner.Generate();
        var bad = await c.Sync(RepoRef.Parse($"{Url}|{other.KeyId}:{other.PublicKeyBase64}"));
        Assert.Equal(TrustState.BadSignature, bad.Trust); Assert.Null(bad.Pack);
        var unsigned = new Pack { Name = "u", Entries = [new PackEntry { Id = "a", Vendor = "V", Ip = "10.1.1.1" }] };
        f.Responses[Url] = (HttpStatusCode.OK, PackJson.Serialize(unsigned), null);
        var pinnedButUnsigned = await c.Sync(RepoRef.Parse($"{Url}|{Key.KeyId}:{Key.PublicKeyBase64}"));
        Assert.Equal(TrustState.BadSignature, pinnedButUnsigned.Trust); Assert.Contains("not signed", pinnedButUnsigned.Error);
        // tampered entry with stale hash
        var tampered = SignedJson().Replace("\"note\": \"n\"", "\"note\": \"evil\"");
        f.Responses[Url] = (HttpStatusCode.OK, tampered, null);
        var t = await c.Sync(RepoRef.Parse(Url));
        Assert.Equal(TrustState.BadSignature, t.Trust); Assert.Contains("hash mismatch", t.Error);
        Assert.Contains("https://", (await c.Sync(RepoRef.Parse("http://insecure/index.json"))).Error);
    }
}

public class RepoServiceTests
{
    const string Url = "https://packs.example/index.json";

    static (NetPawService, FakeFetcher) Make(Dictionary<string, object>? policy = null, params string[] userRepos)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new JsonStore(dir);
        store.SaveSettings(new Settings { RepoUrls = [.. userRepos] });
        var f = new FakeFetcher();
        var key = PackSigner.Generate();
        var pack = new Pack
        {
            Name = "community", Entries =
            [
                new PackEntry { Id = "sw", Vendor = "Acme", Model = "Switch", Kind = "preset", Ip = "10.77.0.1", Prefix = 24 },
                new PackEntry { Id = "lab", Vendor = "Acme", Model = "Lab", Kind = "profile", Profile = new Profile { Name = "Acme lab", Addresses = [IpAddr.Parse("10.9.0.5/16")], Gateway = "10.9.0.1" } },
                new PackEntry { Id = "broken", Vendor = "Acme", Model = "Bad", Kind = "profile", Profile = new Profile { Name = "bad", Gateway = "1.1.1.1" } },
            ],
        };
        PackSigner.Sign(pack, key.PrivateKeyPem);
        f.Responses[Url] = (HttpStatusCode.OK, PackJson.Serialize(pack), null);
        var svc = new NetPawService(store, new FakeAdapters(Fx.Adapter(gw: "192.168.1.1")), new FakeVlan(false), new FakeRunner(),
            new MachineStore(Path.Combine(dir, "profiles.d")), () => PolicyReader.Read(new DictionaryPolicySource(policy ?? new())), new RepoClient(Path.Combine(dir, "cache"), f));
        return (svc, f);
    }

    [Fact]
    public async Task StartupIsOfflineAndSyncAddsEntries()
    {
        var (svc, f) = Make(null, Url);
        Assert.Equal(0, f.Calls);                                  // no network at construction
        var bundled = svc.Presets.Count;
        Assert.Single(svc.RepoStates); Assert.Contains("not synced", svc.RepoStates[0].Error);
        await svc.SyncRepos();
        Assert.Equal(1, f.Calls);
        Assert.Equal(bundled + 1, svc.Presets.Count);
        Assert.Equal("community", svc.Presets.Last().Source);
        var rp = Assert.Single(svc.RepoProfiles);                  // the invalid one was dropped
        Assert.Equal("Acme lab", rp.Name); Assert.Equal("community", rp.Source); Assert.StartsWith("r-community-", rp.Id);
        Assert.Equal(ReachKind.UsePreset, svc.ResolveReach("10.77.0.1").Kind);
        // a fresh service sees the cache without network
        svc.Reload();
        Assert.Equal(bundled + 1, svc.Presets.Count); Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task UnsignedRepoHiddenWhenPolicySaysSo()
    {
        var (svc, _) = Make(new() { ["AllowUnsignedRepos"] = 0 }, Url);
        var bundled = svc.Presets.Count;
        await svc.SyncRepos();
        Assert.Equal(TrustState.Unsigned, svc.RepoStates[0].Trust);
        Assert.Equal(bundled, svc.Presets.Count); Assert.Empty(svc.RepoProfiles);
    }

    [Fact]
    public void PolicyReposCannotBeRemovedAndUserReposCanBeDenied()
    {
        var (svc, _) = Make(new() { ["RepoUrls"] = new[] { Url } });
        Assert.Single(svc.RepoRefs()); Assert.True(svc.RepoRefs()[0].FromPolicy);
        Assert.Throws<InvalidOperationException>(() => svc.RemoveRepo(Url));
        Assert.Throws<InvalidOperationException>(() => svc.AddRepo(Url));
        Assert.Throws<InvalidOperationException>(() => svc.AddRepo("http://plain/index.json"));
        svc.AddRepo("https://mine/index.json");
        Assert.Equal(2, svc.RepoRefs().Count);
        svc.RemoveRepo("https://mine/index.json");
        Assert.Single(svc.RepoRefs());
        var (locked, _) = Make(new() { ["AllowUserRepos"] = 0 }, "https://user/index.json");
        Assert.Empty(locked.RepoRefs());
        Assert.Throws<DeniedByPolicyException>(() => locked.AddRepo("https://x/index.json"));
    }
}
