using System.Management;
using System.Runtime.Versioning;

namespace NetPaw.Adapters;

/// <summary>What the NIC driver exposes for 802.1Q. <see cref="Capable"/>=true with a null <see cref="VlanId"/> means "property exists but unreadable".</summary>
public sealed record VlanInfo(bool Capable, int? VlanId, string? Source);

public interface IVlanProvider
{
    VlanInfo Query(string adapterName);
}

/// <summary>
/// Reads the driver's advanced "VlanID" keyword via the NetAdapter CIM class (the same data
/// Get-NetAdapterAdvancedProperty shows) — ~50 ms instead of a PowerShell start-up.
/// Drivers that do not expose VlanID cannot tag from the OS side (only Intel PROSet / Hyper-V
/// style vNICs do real multi-VLAN); we report that honestly instead of pretending.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiVlanProvider : IVlanProvider
{
    public VlanInfo Query(string adapterName)
    {
        try
        {
            var q = $"SELECT Name, RegistryKeyword, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name = '{adapterName.Replace("'", "''")}' AND RegistryKeyword = 'VlanID'";
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", q);
            foreach (ManagementObject o in searcher.Get())
            {
                var raw = o["RegistryValue"] as string[];
                int? id = raw is { Length: > 0 } && int.TryParse(raw[0], out var v) ? v : null;
                return new VlanInfo(true, id, "VlanID");
            }
            return new VlanInfo(false, null, null);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return new VlanInfo(false, null, "error: " + ex.Message);
        }
    }
}

public sealed class NullVlanProvider : IVlanProvider
{
    public VlanInfo Query(string adapterName) => new(false, null, null);
}
