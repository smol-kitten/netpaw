using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NetPaw.Store;

/// <summary>Raw access to machine policy values so the reader can be unit-tested without a registry.</summary>
public interface IPolicySource
{
    string? GetString(string name);
    int? GetInt(string name);
    string[]? GetMulti(string name);
}

/// <summary>What an organisation may pin or deny. Every Allow* defaults to true; null means "not set by policy".</summary>
public sealed record Policy(
    string? WorkAdapter,
    string? PanelHotkey,
    bool? ConfirmBeforeApply,
    IReadOnlyList<string> RepoUrls,
    bool AllowUserProfiles,
    bool AllowUserRepos,
    bool AllowReach,
    bool AllowTempAddresses,
    bool AllowDhcp,
    bool AllowUnsignedRepos,
    bool AllowTelemetry,
    bool AllowAutoSwitch,
    bool AllowScan,
    bool AllowCapture,
    bool AllowUpdateCheck,
    IReadOnlySet<string> SetKeys)
{
    public static readonly Policy None = new(null, null, null, [], true, true, true, true, true, true, true, true, true, true, true, new HashSet<string>());
    public bool IsSet(string key) => SetKeys.Contains(key);
    public bool Any => SetKeys.Count > 0;

    public const string KeyPath = @"SOFTWARE\Policies\NetPaw";
    public static readonly string[] Keys = ["WorkAdapter", "PanelHotkey", "ConfirmBeforeApply", "RepoUrls", "AllowUserProfiles", "AllowUserRepos", "AllowReach", "AllowTempAddresses", "AllowDhcp", "AllowUnsignedRepos", "AllowTelemetry", "AllowAutoSwitch", "AllowScan", "AllowCapture", "AllowUpdateCheck"];
}

public static class PolicyReader
{
    public static Policy Read(IPolicySource src)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? S(string k) { var v = src.GetString(k); if (v is not null) set.Add(k); return v; }
        bool? B(string k) { var v = src.GetInt(k); if (v is null) return null; set.Add(k); return v != 0; }
        bool Allow(string k) => B(k) ?? true;
        var urls = src.GetMulti("RepoUrls"); if (urls is not null) set.Add("RepoUrls");
        return new Policy(S("WorkAdapter"), S("PanelHotkey"), B("ConfirmBeforeApply"), urls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [],
            Allow("AllowUserProfiles"), Allow("AllowUserRepos"), Allow("AllowReach"), Allow("AllowTempAddresses"), Allow("AllowDhcp"), Allow("AllowUnsignedRepos"), Allow("AllowTelemetry"), Allow("AllowAutoSwitch"), Allow("AllowScan"), Allow("AllowCapture"), Allow("AllowUpdateCheck"), set);
    }

    public static Policy ReadMachine() => OperatingSystem.IsWindows() ? Read(new RegistryPolicySource()) : Policy.None;
}

/// <summary>HKLM\SOFTWARE\Policies\NetPaw — the key both GPO (ADMX) and Intune (ADMX ingestion / OMA-URI) write to.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPolicySource : IPolicySource
{
    object? Get(string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(Policy.KeyPath); return k?.GetValue(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }
    public string? GetString(string name) => Get(name) as string;
    public int? GetInt(string name) => Get(name) switch { int i => i, string s when int.TryParse(s, out var i) => i, _ => null };
    public string[]? GetMulti(string name) => Get(name) switch { string[] a => a, string s => s.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), _ => null };
}

public sealed class DictionaryPolicySource(IDictionary<string, object> values) : IPolicySource
{
    public string? GetString(string name) => values.TryGetValue(name, out var v) ? v as string : null;
    public int? GetInt(string name) => values.TryGetValue(name, out var v) ? v switch { int i => i, bool b => b ? 1 : 0, _ => null } : null;
    public string[]? GetMulti(string name) => values.TryGetValue(name, out var v) ? v as string[] : null;
}
