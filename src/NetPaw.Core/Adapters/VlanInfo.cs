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

public sealed class NullVlanProvider : IVlanProvider
{
    public VlanInfo Query(string adapterName) => new(false, null, null);
}
