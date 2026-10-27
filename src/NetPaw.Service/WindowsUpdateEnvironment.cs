using System.Diagnostics;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using NetPaw.Updates;

namespace NetPaw.ServiceHost;

/// <summary>The Windows side of <see cref="UpdateInstaller"/>: a DACL'd download folder under the service store,
/// one HTTP client that never follows redirects, and a detached msiexec start.</summary>
public sealed class WindowsUpdateEnvironment(string serviceDir, Action<string> log) : IUpdateEnvironment
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromSeconds(120) };

    public IReadOnlyCollection<X509Certificate2> PinnedRoots => UpdateTrust.PinnedRoots;
    public IReadOnlyCollection<X509Certificate2>? Intermediates => UpdateTrust.Intermediates;

    /// <summary>`service\updates`: same reparse-point and owner rules as the service folder, SYSTEM + Administrators only.</summary>
    public (string? Dir, string Reason) EnsureDownloadDir()
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(serviceDir, "updates"));
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { var aside = dir.FullName + ".reparse-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"); Directory.Move(dir.FullName, aside); log($"{dir.FullName} was a reparse point; moved to {aside}"); dir.Refresh(); }
            if (dir.Exists)
            {
                var owner = dir.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
                if (owner is null || !(owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)))
                    return (null, "the updates folder is owned by someone else; not trusted");
                dir.SetAccessControl(ServiceFolder.Acl());
                foreach (var f in dir.GetFiles()) { try { f.Delete(); } catch (IOException) { } }   // a previous run's leftovers
            }
            else dir.Create(ServiceFolder.Acl());
            return (dir.FullName, "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ex.Message); }
    }

    public async Task<HttpAnswer> Get(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("NetPaw-service");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)resp.StatusCode;
        if (status is >= 300 and < 400) return new HttpAnswer(status, resp.Headers.Location?.ToString(), []);
        var len = resp.Content.Headers.ContentLength;
        if (len > UpdateTrust.MaxDownload) return new HttpAnswer(status, null, [], len);
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n; long total = 0;
        while ((n = await s.ReadAsync(buf, ct)) > 0) { total += n; if (total > UpdateTrust.MaxDownload) return new HttpAnswer(status, null, [], total); ms.Write(buf, 0, n); }
        return new HttpAnswer(status, null, ms.ToArray(), len);
    }

    /// <summary>msiexec starts detached after 2 s so the service answers the client before the MSI stops it.</summary>
    public (int? Pid, string Reason) StartInstaller(string msiPath, string logPath)
    {
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            var p = Process.Start(new ProcessStartInfo(cmd, $"/d /c timeout /t 2 /nobreak >nul & \"{msiexec}\" /i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"")
                { UseShellExecute = false, CreateNoWindow = true });
            return p is null ? (null, "msiexec did not start") : (p.Id, "ok");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, ex.Message); }
    }
}
