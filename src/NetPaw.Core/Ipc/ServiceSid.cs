using System.Security.Cryptography;
using System.Text;

namespace NetPaw.Ipc;

/// <summary>
/// The per-service SID Windows derives from a service name (<c>NT SERVICE\name</c>): S-1-5-80 followed by the
/// SHA-1 of the upper-cased UTF-16LE name as five little-endian DWORDs. With <c>sc sidtype restricted</c> the
/// service token is write-restricted, and a write succeeds only where the DACL also grants this SID (or
/// WRITE RESTRICTED / Everyone). Pure, so it is tested here against the well-known TrustedInstaller SID.
/// </summary>
public static class ServiceSid
{
    public static string For(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4).ToString());
        return "S-1-5-80-" + string.Join("-", parts);
    }
}
