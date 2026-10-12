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

    /// <summary>Returns why an opened file must not be trusted, or null. Windows: the owner read FROM THE OPEN
    /// HANDLE must be SYSTEM or Administrators — checking the handle, not re-opening the path, so a file cannot be
    /// swapped between the owner check and the read (TOCTOU). Tests replace it; null skips the check.</summary>
    public Func<FileStream, string?>? OwnerCheck { get; init; } = OperatingSystem.IsWindows() ? AdminOwnedHandle : null;

    /// <summary>Returns why the profiles.d folder itself must not be trusted, or null. ProgramData lets any user
    /// create folders, so a pre-created profiles.d that is a reparse point (a junction to an admin-owned folder)
    /// or is not owned by SYSTEM/Administrators is not trusted and nothing in it is loaded — mirrors
    /// <c>ServiceFolder</c>. Tests replace it; null skips the check.</summary>
    public Func<string, string?>? DirCheck { get; init; } = OperatingSystem.IsWindows() ? AdminOwnedDir : null;

    static string? AdminOwnedHandle(FileStream fs)
    {
        var owner = fs.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
        return IsAdminOrSystem(owner) ? null : $"skipped: owned by {owner?.Value ?? "unknown"}, not SYSTEM or Administrators";
    }

    static string? AdminOwnedDir(string dir)
    {
        var di = new DirectoryInfo(dir);
        // A reparse point here means the real folder (and its ACL and owner) is somewhere else: never trust it.
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "profiles.d is a reparse point; not trusted";
        var owner = di.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
        return IsAdminOrSystem(owner) ? null : $"profiles.d owned by {owner?.Value ?? "unknown"}, not SYSTEM or Administrators";
    }

    static bool IsAdminOrSystem(System.Security.Principal.SecurityIdentifier? owner) =>
        owner is not null && (owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid));

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
        // Gate the folder before reading anything in it: an untrusted profiles.d taints every file inside.
        if (DirCheck?.Invoke(Directory) is { } badDir) { Errors.Add($"profiles.d not trusted, no managed profiles loaded: {badDir}"); return list; }
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Open once; check the owner on THIS handle and read from THE SAME handle — no second open to race.
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (OwnerCheck?.Invoke(fs) is { } notAdmin) { Errors.Add($"{Path.GetFileName(file)}: {notAdmin}"); continue; }
                var profiles = JsonSerializer.Deserialize<List<Profile>>(new StreamReader(fs).ReadToEnd(), Json) ?? [];
                foreach (var p in profiles)
                {
                    var problems = Ipc.ProfileGuard.Check(p);
                    if (problems.Count > 0) { Errors.Add($"{Path.GetFileName(file)}: profile '{p.Name}' skipped: {string.Join(" ", problems)}"); continue; }
                    p.AutoSwitchConfirmed = false;
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
