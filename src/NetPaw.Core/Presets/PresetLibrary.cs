using System.Reflection;
using System.Text.Json;

namespace NetPaw.Presets;

/// <summary>Bundled presets (embedded presets.json) merged with the user's own file; user entries with the same Vendor/Model win.</summary>
public static class PresetLibrary
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<Preset> LoadBundled()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("presets.json") ?? throw new InvalidOperationException("presets.json missing from assembly");
        return JsonSerializer.Deserialize<List<Preset>>(s, Json) ?? [];
    }

    public static IReadOnlyList<Preset> Load(string? userFile)
    {
        var map = LoadBundled().ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        if (userFile is not null && File.Exists(userFile))
            foreach (var p in JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(userFile), Json) ?? [])
                map[p.Key] = p;
        return map.Values.OrderBy(p => p.Vendor).ThenBy(p => p.Model).ToList();
    }

    public static IEnumerable<Preset> Search(IEnumerable<Preset> presets, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return presets;
        return presets.Where(p => p.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || p.Model.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || p.Ip.StartsWith(q, StringComparison.Ordinal));
    }
}
