using System.Runtime.Versioning;

namespace NetPaw.Adapters;

/// <summary>What the NIC driver exposes for 802.1Q. <see cref="Capable"/>=true with a null <see cref="VlanId"/> means "property exists but unreadable".</summary>
public sealed record VlanInfo(bool Capable, int? VlanId, string? Source);

public interface IVlanProvider
{
    VlanInfo Query(string adapterName);
}

/// <summary>
/// Reads the driver's advanced "VlanID" keyword with Get-NetAdapterAdvancedProperty through the step runner.
/// One PowerShell start (~1 s) per adapter, cached for a minute — and no System.Management dependency, which
/// failed to load for some MSI installs (v0.11.0). Drivers that do not expose VlanID cannot tag from the OS
/// side (only Intel PROSet / Hyper-V style vNICs do real multi-VLAN); we report that honestly instead of pretending.
/// </summary>
public sealed class PowerShellVlanProvider(Planning.IStepRunner runner) : IVlanProvider
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    readonly Dictionary<string, (VlanInfo Info, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public VlanInfo Query(string adapterName)
    {
        lock (_cache)
            if (_cache.TryGetValue(adapterName, out var c) && DateTimeOffset.Now - c.At < CacheFor) return c.Info;
        var cmd = $"$p = Get-NetAdapterAdvancedProperty -Name '{adapterName.Replace("'", "''")}' -RegistryKeyword VlanID -ErrorAction SilentlyContinue; if ($p) {{ 'VLANID=' + ($p.RegistryValue -join ',') }} else {{ 'VLANID=none' }}";
        var r = runner.Run(Planning.Step.PowerShell($"Read VlanID of {adapterName}", cmd, critical: false));
        var info = Parse(r.Output, r.ExitCode);
        lock (_cache) _cache[adapterName] = (info, DateTimeOffset.Now);
        return info;
    }

    /// <summary>"VLANID=none" → no property; "VLANID=7" → capable, id 7; anything else → capable but unreadable / error.</summary>
    public static VlanInfo Parse(string output, int exitCode)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("VLANID=", StringComparison.OrdinalIgnoreCase));
        if (line is null) return new VlanInfo(false, null, exitCode == 0 ? null : "error: " + output.Trim().Split('\n').FirstOrDefault());
        var value = line[7..].Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return new VlanInfo(false, null, null);
        return new VlanInfo(true, int.TryParse(value.Split(',')[0], out var id) ? id : null, "VlanID");
    }

    public void Forget(string adapterName) { lock (_cache) _cache.Remove(adapterName); }
}

/// <summary>
/// Instant VLAN query from the driver's registry keys: the network class key holds one subkey per driver
/// instance (<c>NetCfgInstanceId</c> = adapter GUID); <c>Ndi\Params\VlanID</c> under it means the driver
/// exposes the property, the <c>VlanID</c> value is the current id. Falls back to PowerShell when the
/// registry does not answer (odd drivers, denied keys). Setting still goes through Set-NetAdapterAdvancedProperty.
/// </summary>
public sealed class RegistryVlanProvider(Func<string, string?> adapterIdByName, IVlanProvider fallback, Func<string, VlanInfo?>? reader = null) : IVlanProvider
{
    public VlanInfo Query(string adapterName)
    {
        var id = adapterIdByName(adapterName);
        var info = id is null ? null : (reader ?? ReadRegistry)(id);
        return info ?? fallback.Query(adapterName);
    }

    const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static VlanInfo? ReadRegistry(string adapterGuid)
    {
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ClassKey);
            if (cls is null) return null;
            foreach (var name in cls.GetSubKeyNames())
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("NetCfgInstanceId") is not string guid || !guid.Equals(adapterGuid, StringComparison.OrdinalIgnoreCase)) continue;
                using var param = k.OpenSubKey(@"Ndi\Params\VlanID");
                if (param is null) return new VlanInfo(false, null, null);
                var raw = k.GetValue("VlanID")?.ToString();
                return new VlanInfo(true, int.TryParse(raw, out var v) ? v : null, "VlanID");
            }
            return null;                                                 // adapter not found under the class key: let PowerShell decide
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

public sealed class NullVlanProvider : IVlanProvider
{
    public VlanInfo Query(string adapterName) => new(false, null, null);
}
