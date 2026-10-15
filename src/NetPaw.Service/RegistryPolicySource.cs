using Microsoft.Win32;
using NetPaw.Ipc;

namespace NetPaw.ServiceHost;

/// <summary>
/// Reads PR 4 tier overrides from machine policy: <c>HKLM\Software\Policies\NetPaw</c>, one string value per
/// intent named <c>Tier.&lt;Intent&gt;</c> (e.g. <c>Tier.DeleteRoute = "Admin"</c>). An ABSENT key (no policy
/// configured) is valid with no overrides. A present-but-unreadable key (access denied) or a bad tier value is
/// INVALID, so <see cref="PolicyTiers"/> falls back to the last-known-good cache. Only GPO/Intune (SYSTEM) writes
/// this key; the service reads it.
/// </summary>
public sealed class RegistryPolicySource : ITierPolicySource
{
    const string KeyPath = @"Software\Policies\NetPaw";

    public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read()
    {
        var empty = (IReadOnlyDictionary<PolicyIntent, Tier>)new Dictionary<PolicyIntent, Tier>();
        if (!OperatingSystem.IsWindows()) return (empty, true, null);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key is null) return (empty, true, null);   // no policy configured -> defaults
            // G9-lite: a policy written for a schema this build does not know is invalid as a whole (fail closed).
            var schema = key.GetValue("PolicySchemaVersion")?.ToString();
            if (!PolicyTiers.SchemaSupported(schema)) return (empty, false, $"PolicySchemaVersion {schema} unsupported, this build: {PolicyTiers.SchemaVersion}");
            var overrides = new Dictionary<PolicyIntent, Tier>();
            foreach (var intent in Enum.GetValues<PolicyIntent>())
            {
                var raw = key.GetValue("Tier." + intent);
                if (raw is null) continue;
                if (raw is not string s || !PolicyTiers.TryParseTier(s, out var tier))
                    return (empty, false, $"Tier.{intent} = '{raw}' is not a tier name");   // malformed -> invalid -> last-known-good
                overrides[intent] = tier;
            }
            return (overrides, true, null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (empty, false, "policy key unreadable: " + ex.Message);   // unreadable -> invalid -> last-known-good
        }
    }
}
