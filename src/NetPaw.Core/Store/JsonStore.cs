using System.Text.Json;
using System.Text.Json.Serialization;
using NetPaw.Model;

namespace NetPaw.Store;

/// <summary>Profiles, settings and temp-address state as plain JSON files (atomic writes) so they are greppable and backup-friendly.</summary>
public sealed class JsonStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public string Directory { get; }
    public string ProfilesFile => Path.Combine(Directory, "profiles.json");
    public string SettingsFile => Path.Combine(Directory, "settings.json");
    public string StateFile => Path.Combine(Directory, "state.json");
    public string PresetsFile => Path.Combine(Directory, "presets.json");
    public string LogFile => Path.Combine(Directory, "netpaw.log");

    public JsonStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static string DefaultDirectory()
    {
        var env = Environment.GetEnvironmentVariable("NETPAW_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(string.IsNullOrEmpty(appData) ? Environment.CurrentDirectory : appData, "NetPaw");
    }

    public List<Profile> LoadProfiles() => Load<List<Profile>>(ProfilesFile) ?? [];
    public void SaveProfiles(IEnumerable<Profile> profiles) => Save(ProfilesFile, profiles.ToList());
    public Settings LoadSettings() => Load<Settings>(SettingsFile) ?? new Settings();
    public void SaveSettings(Settings s) => Save(SettingsFile, s);
    public List<TempAddress> LoadTemp() => Load<List<TempAddress>>(StateFile) ?? [];
    public void SaveTemp(IEnumerable<TempAddress> temp) => Save(StateFile, temp.ToList());

    public void Log(string line)
    {
        try { File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    T? Load<T>(string file) where T : class
    {
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json); }
        catch (JsonException ex) { Log($"corrupt {Path.GetFileName(file)}: {ex.Message}"); return null; }
    }

    void Save<T>(string file, T value)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, file, overwrite: true);
    }
}
