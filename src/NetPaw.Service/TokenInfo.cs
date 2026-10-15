using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NetPaw.ServiceHost;

/// <summary>What the service's own process token holds — the `whoami /priv` equivalent, logged at start so the
/// evidence is the token Windows built, not the SCM configuration `sc qprivs` / `sc qsidtype` report (G2).</summary>
public static class TokenInfo
{
    public static string Describe()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var privs = Privileges(id.Token);
            var serviceSid = NetPaw.Ipc.ServiceSid.For("NetPaw");
            var hasServiceSid = id.Groups?.Any(g => g.Value == serviceSid) == true;
            var restricted = IsTokenRestricted(id.Token);
            return $"user={id.Name} privileges={string.Join(",", privs)} serviceSid={(hasServiceSid ? "present" : "absent")} restricted={restricted}";
        }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }

    static List<string> Privileges(IntPtr token)
    {
        const int TokenPrivilegesClass = 3;
        GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out var size);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, buf, size, out _)) throw new System.ComponentModel.Win32Exception();
            var count = Marshal.ReadInt32(buf);
            var list = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                // TOKEN_PRIVILEGES: DWORD PrivilegeCount; LUID_AND_ATTRIBUTES[] (LUID 8 bytes + DWORD attributes = 12 bytes each)
                var entry = IntPtr.Add(buf, 4 + i * 12);
                var luid = Marshal.ReadInt64(entry);
                var attr = (uint)Marshal.ReadInt32(entry, 8);
                var name = new System.Text.StringBuilder(64); var len = name.Capacity;
                var text = LookupPrivilegeName(null, ref luid, name, ref len) ? name.ToString() : $"luid:{luid}";
                list.Add(text + ((attr & 0x2) != 0 ? "" : "(disabled)"));   // SE_PRIVILEGE_ENABLED = 0x2
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeName(string? systemName, ref long luid, System.Text.StringBuilder name, ref int nameLength);

    [DllImport("advapi32.dll")]
    static extern bool IsTokenRestricted(IntPtr token);
}
