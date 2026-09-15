using System.Text.Json;
using NetPaw.Model;

namespace NetPaw.Store;

/// <summary>
/// Read-only profiles deployed machine-wide: every <c>*.json</c> in
/// <c>%ProgramData%\NetPaw\profiles.d\</c> holds a list of profiles. Intune (Win32 app / script)
/// and GPO (file preference) can drop files there; users see the profiles but cannot change them.
/// </summary>
public sealed class MachineStore
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string Directory { get; }
    public List<string> Errors { get; } = [];

    public MachineStore(string? directory = null) => Directory = directory ?? DefaultDirectory();

    public static string DefaultDirectory()
    {
        var env = Environment.GetEnvironmentVariable("NETPAW_MACHINE_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(string.IsNullOrEmpty(common) ? "/etc" : common, "NetPaw", "profiles.d");
    }

    /// <summary>Loads every file in name order; a broken file is skipped and reported, never fatal.</summary>
    public List<Profile> Load()
    {
        Errors.Clear();
        var list = new List<Profile>();
        if (!System.IO.Directory.Exists(Directory)) return list;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(file), Json) ?? [];
                foreach (var p in profiles)
                {
                    p.Managed = true; p.Source = "managed";
                    // Deterministic id (file + name) so hotkeys and "current" checks are stable across machines.
                    p.Id = "m-" + Path.GetFileNameWithoutExtension(file) + "-" + p.Name.ToLowerInvariant().Replace(' ', '-');
                    list.Add(p);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Managed first, then the user's own profiles minus any whose name a managed profile already uses.</summary>
    public static List<Profile> Merge(IEnumerable<Profile> managed, IEnumerable<Profile> user)
    {
        var m = managed.ToList();
        var names = m.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        m.AddRange(user.Where(p => !names.Contains(p.Name)));
        return m;
    }
}
