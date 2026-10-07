using System.Security.AccessControl;
using System.Security.Principal;

namespace NetPaw.ServiceHost;

/// <summary>
/// %ProgramData%\NetPaw\service: the service's own store (settings, temporary-address state, log, apply
/// journal). Only SYSTEM and Administrators may touch it. ProgramData lets every user create folders, so a
/// folder that already exists and is not owned by SYSTEM or Administrators is moved aside, never trusted.
/// </summary>
public static class ServiceFolder
{
    static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static DirectorySecurity Acl()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    public static string Ensure(string root, Action<string> log)
    {
        var dir = new DirectoryInfo(Path.Combine(root, "service"));
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A user could pre-create service\ as a junction to an admin-owned folder; the owner check would then
            // pass and we would re-ACL and write into the target. Move any reparse point aside unconditionally.
            var junction = dir.FullName + ".reparse-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            Directory.Move(dir.FullName, junction);
            log($"{dir.FullName} was a reparse point; moved to {junction} and recreated");
            dir = new DirectoryInfo(dir.FullName);
        }
        if (dir.Exists)
        {
            var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == System || owner == Admins) { dir.SetAccessControl(Acl()); return dir.FullName; }
            var aside = dir.FullName + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            Directory.Move(dir.FullName, aside);
            log($"{dir.FullName} was owned by {owner?.Value ?? "?"}, not SYSTEM/Administrators; moved to {aside} and recreated");
        }
        dir.Create(Acl());
        return dir.FullName;
    }
}
