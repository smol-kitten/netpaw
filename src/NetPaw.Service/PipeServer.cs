using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using NetPaw.Ipc;

namespace NetPaw.ServiceHost;

/// <summary>
/// Serves <c>\\.\pipe\NetPaw</c>. One request per connection; the caller's identity is read from the pipe
/// for every request (docs/PLAN-v0.14.md, "IPC"). This step answers <see cref="RequestKind.Status"/> only.
/// </summary>
public sealed class PipeServer(Action<string> log)
{
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    // Non-zero buffers: with 0 a write blocks until the other side reads, so two writers deadlock.
    const int BufferSize = 4096;

    /// <summary>Who may open the pipe. Everyone not listed is refused by Windows before the service reads a
    /// byte. Remote (SMB) callers are denied explicitly, even if they belong to a listed group.</summary>
    public static PipeSecurity Acl()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid, WellKnownSidType.InteractiveSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return acl;
    }

    public async Task Run(CancellationToken stop)
    {
        var acl = Acl();
        log($"listening on \\\\.\\pipe\\{ServiceProtocol.PipeName}");
        while (!stop.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferSize, BufferSize, acl);
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            _ = Task.Run(() => Serve(pipe, stop), stop);
        }
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using var _ = pipe;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(ReadTimeout);
        ServiceResponse reply;
        try
        {
            // Read first: Windows lets a pipe server identify its client only after it has read from the pipe.
            var line = await ServiceProtocol.ReadLine(pipe, cts.Token);
            if (line is null) return;
            var caller = Caller.Of(pipe);
            var (req, err) = ServiceProtocol.ParseRequest(line);
            reply = req is null ? ServiceResponse.Fail(err!) : Handle(req, caller);
            log($"{req?.Kind.ToString() ?? "invalid"} from {caller.User} (session {caller.Session}): {(reply.Ok ? "ok" : reply.Error)}");
        }
        catch (Exception ex)
        {
            // Every failure gets an answer that says what happened; the client never sees a bare refusal.
            var why = ex is OperationCanceledException ? "no complete request within 10 s" : $"{ex.GetType().Name}: {ex.Message}";
            log("request refused: " + why);
            reply = ServiceResponse.Fail(why);
        }
        try { await pipe.WriteAsync(ServiceProtocol.Encode(reply), stop); await pipe.FlushAsync(stop); }
        catch (IOException) { }   // the client went away; nothing to tell it
    }

    static readonly string BuildVersion = (typeof(PipeServer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];

    static ServiceResponse Handle(ServiceRequest req, Caller caller) => req.Kind switch
    {
        RequestKind.Status => new(ServiceProtocol.Version, true, Status: new(BuildVersion, ServiceProtocol.Version, caller.User, caller.Session)),
        _ => ServiceResponse.Fail($"{req.Kind} is not implemented by this service"),
    };
}

/// <summary>The identity behind a pipe connection, read from Windows, never from the request body.</summary>
public sealed record Caller(string User, int Session, int ProcessId)
{
    public static Caller Of(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new UnauthorizedAccessException("cannot read the caller's process id");
        if (!ProcessIdToSessionId(pid, out var session)) throw new UnauthorizedAccessException("cannot read the caller's session");
        SecurityIdentifier? sid = null;
        // The client connects at Identification level: enough to read its token, not to act as it. WindowsIdentity
        // refuses such a token ("cannot be duplicated"), so the SID is read from the token directly.
        pipe.RunAsClient(() => sid = ThreadTokenUser());
        if (sid is null) throw new UnauthorizedAccessException("cannot read the caller's user");
        string user;
        try { user = sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { user = sid.Value; }
        return new Caller(user, (int)session, (int)pid);
    }

    static SecurityIdentifier ThreadTokenUser()
    {
        const uint TokenQuery = 0x0008; const int TokenUserClass = 1;
        if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token)) throw new System.ComponentModel.Win32Exception();
        try
        {
            GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out var size);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, buf, size, out _)) throw new System.ComponentModel.Win32Exception();
                return new SecurityIdentifier(Marshal.ReadIntPtr(buf));   // TOKEN_USER.User.Sid
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenThreadToken(IntPtr thread, uint desiredAccess, bool openAsSelf, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
