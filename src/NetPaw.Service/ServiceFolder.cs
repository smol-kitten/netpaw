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
    // NT SERVICE\NetPaw: with sc sidtype restricted the service token is write-restricted, and the store must grant
    // this SID too, or the journal/cache/log writes fail (audit G2).
    static readonly SecurityIdentifier Service = new(NetPaw.Ipc.ServiceSid.For("NetPaw"));

    public static DirectorySecurity Acl()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins, Service })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>The data root's ACL (what the MSI sets on %ProgramData%\NetPaw): SYSTEM, Administrators and the
    /// service SID full control, Users read + execute. Users must not be able to rename or replace the subfolders
    /// (service\, capture\, profiles.d\) between <see cref="Ensure"/> and use.</summary>
    public static DirectorySecurity RootAcl()
    {
        var sec = Acl();
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Makes the data root itself trustworthy before any subfolder is used. A reparse point is refused
    /// (fail loud: the whole store would live where a user chose). A missing root is created with
    /// <see cref="RootAcl"/>. A root owned by someone other than SYSTEM/Administrators (pre-created by a user
    /// before install, or a build-path install that inherited ProgramData's open ACL) is taken over by SYSTEM
    /// and re-ACLed — an owner can rewrite the DACL, so a protected DACL alone is not enough.</summary>
    public static string EnsureRoot(string root, Action<string> log)
    {
        var dir = new DirectoryInfo(root);
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{root} is a reparse point (junction/symlink); the service refuses to use it. Remove it and reinstall.");
        if (!dir.Exists) { dir.Create(RootAcl()); log($"{root} created with the service ACL"); return dir.FullName; }
        var sec = dir.GetAccessControl();
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner != System && owner != Admins)
        {
            var fixedSec = RootAcl(); fixedSec.SetOwner(System);
            dir.SetAccessControl(fixedSec);     // LocalSystem holds SeTakeOwnershipPrivilege; .NET enables it for the owner write
            log($"{root} was owned by {owner?.Value ?? "?"}, not SYSTEM/Administrators; ownership taken by SYSTEM and ACL reset");
        }
        else if (!sec.AreAccessRulesProtected)
        {
            dir.SetAccessControl(RootAcl());
            log($"{root} inherited ProgramData's open ACL; reset to the service ACL");
        }
        return dir.FullName;
    }

    /// <summary>Ensures <c>root\name</c> exists with the service ACL. <c>service</c> is the store; <c>capture</c> holds pktmon ETL/pcapng files.</summary>
    public static string Ensure(string root, Action<string> log, string name = "service")
    {
        var dir = new DirectoryInfo(Path.Combine(root, name));
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
