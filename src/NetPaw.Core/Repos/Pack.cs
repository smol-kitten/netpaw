using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetPaw.Model;

namespace NetPaw.Repos;

/// <summary>
/// An online profile pack: <c>index.json</c> at any HTTPS URL. Entries are presets (device default
/// address) or full profiles. Each entry may carry its own sha256; the pack may carry a signature
/// over the canonical entries array. See docs/PACK-FORMAT.md.
/// </summary>
public sealed class Pack
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1";
    public string? Updated { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public List<PackEntry> Entries { get; set; } = [];
    public PackSignature? Signature { get; set; }
}

public sealed class PackEntry
{
    public string Id { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>"preset" (device address; host picked at use) or "profile" (full profile).</summary>
    public string Kind { get; set; } = "preset";
    public string? Ip { get; set; }
    public int? Prefix { get; set; }
    public string? HostIp { get; set; }
    public Profile? Profile { get; set; }
    public string? Note { get; set; }
    public string? Url { get; set; }
    public string? Sha256 { get; set; }

    public string Title => string.IsNullOrEmpty(Model) ? Vendor : $"{Vendor} {Model}";

    public Presets.Preset? ToPreset() => Kind == "preset" && Ip is not null
        ? new Presets.Preset { Vendor = Vendor, Model = Model, Ip = Ip, Prefix = Prefix ?? 24, HostIp = HostIp, Note = Note, Url = Url }
        : null;
}

public sealed class PackSignature
{
    public string Alg { get; set; } = PackSigner.Alg;
    public string KeyId { get; set; } = "";
    public string Value { get; set; } = "";
}

public static class PackJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static Pack Parse(string json) => JsonSerializer.Deserialize<Pack>(json, Options) ?? throw new JsonException("empty pack");
    public static string Serialize(Pack pack) => JsonSerializer.Serialize(pack, Options);

    /// <summary>Canonical form: object keys sorted ordinally, no whitespace, UTF-8. Stable across serializers, so signatures survive re-formatting.</summary>
    public static byte[] Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{'); var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Value is null) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':'); Write(kv.Value, sb);
                }
                sb.Append('}'); break;
            case JsonArray a:
                sb.Append('['); for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i], sb); } sb.Append(']'); break;
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    public static JsonNode? EntryNode(PackEntry e)
    {
        var node = JsonSerializer.SerializeToNode(e, Options)!.AsObject();
        node.Remove("sha256");
        return node;
    }

    public static string EntryHash(PackEntry e) => Convert.ToHexStringLower(SHA256.HashData(Canonical(EntryNode(e))));

    public static byte[] EntriesBytes(Pack p)
    {
        var arr = new JsonArray();
        foreach (var e in p.Entries) arr.Add(JsonSerializer.SerializeToNode(e, Options));
        return Canonical(arr);
    }

    /// <summary>Fills in every entry's sha256 and returns the ids whose stored hash did not match.</summary>
    public static List<string> VerifyHashes(Pack p)
    {
        var bad = new List<string>();
        foreach (var e in p.Entries)
            if (e.Sha256 is not null && !string.Equals(e.Sha256, EntryHash(e), StringComparison.OrdinalIgnoreCase)) bad.Add(e.Id);
        return bad;
    }

    public static void StampHashes(Pack p) { foreach (var e in p.Entries) e.Sha256 = EntryHash(e); }
}
