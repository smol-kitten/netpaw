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

    public (IReadOnlyDictionary<PolicyIntent, Tier>, bool) Read()
    {
        var empty = (IReadOnlyDictionary<PolicyIntent, Tier>)new Dictionary<PolicyIntent, Tier>();
        if (!OperatingSystem.IsWindows()) return (empty, true);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key is null) return (empty, true);   // no policy configured -> defaults
            var overrides = new Dictionary<PolicyIntent, Tier>();
            foreach (var intent in Enum.GetValues<PolicyIntent>())
            {
                var raw = key.GetValue("Tier." + intent);
                if (raw is null) continue;
                if (raw is not string s || !Enum.TryParse<Tier>(s, ignoreCase: true, out var tier))
                    return (empty, false);           // present but malformed -> invalid -> last-known-good
                overrides[intent] = tier;
            }
            return (overrides, true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (empty, false);                   // unreadable -> invalid -> last-known-good
        }
    }
}
