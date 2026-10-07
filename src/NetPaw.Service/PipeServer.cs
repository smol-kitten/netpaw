using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using NetPaw.Ipc;

namespace NetPaw.ServiceHost;

/// <summary>
/// Serves <c>\\.\pipe\NetPaw</c>. One request per connection; the caller's identity is read from the pipe
/// for every request (docs/PLAN-v0.14.md, "IPC"). Status is answered here; change requests go to <see cref="IntentHandler"/>.
/// </summary>
public sealed class PipeServer(Action<string> log, IntentHandler? intents = null)
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
        // ReadWrite|Synchronize is enough for a client to open the pipe (verified from an interactive session on
        // the pawsktop Win11 VM). No CreateNewInstance, so allowed users cannot add server instances. A caller
        // whose token carries the NETWORK SID (a remote SMB open, or an SSH network logon) is denied above.
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid, WellKnownSidType.InteractiveSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return acl;
    }

    /// <summary>At most this many requests are served at once; further clients wait in their connect timeout.</summary>
    const int MaxConcurrent = 8;

    public async Task Run(CancellationToken stop)
    {
        var acl = Acl();
        using var slots = new SemaphoreSlim(MaxConcurrent);
        // FirstPipeInstance on the first create: if any other process already owns \\.\pipe\NetPaw (a squatter
        // while the service was stopped), creating fails and the service stops loudly instead of joining a pipe
        // whose DACL someone else chose.
        var first = true;
        log($"listening on \\\\.\\pipe\\{ServiceProtocol.PipeName}");
        while (!stop.IsCancellationRequested)
        {
            await slots.WaitAsync(stop);
            var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0);
            var pipe = NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, BufferSize, BufferSize, acl);
            first = false;
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); slots.Release(); break; }
            _ = Task.Run(async () => { try { await Serve(pipe, stop); } finally { slots.Release(); } }, CancellationToken.None);
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

    ServiceResponse Handle(ServiceRequest req, Caller caller) => req.Kind switch
    {
        RequestKind.Status => new(ServiceProtocol.Version, true, Status: new(BuildVersion, ServiceProtocol.Version, caller.User, caller.Session)),
        _ when intents is null => ServiceResponse.Fail($"{req.Kind} is not available (service started without its store)"),
        _ => intents.Handle(req, $"{caller.User} (session {caller.Session})", caller.Privileged),
    };
}

/// <summary>The identity behind a pipe connection, read from Windows, never from the request body.</summary>
public sealed record Caller(string User, int Session, int ProcessId, bool Privileged)
{
    public static Caller Of(NamedPipeServerStream pipe)
    {
        // Session straight from the pipe, not PID -> session: the PID can be reused once the client exits.
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session)) throw new UnauthorizedAccessException("cannot read the caller's session");
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid);   // for the log only
        SecurityIdentifier? sid = null;
        var privileged = false;
        // The client connects at Identification level: enough to read its token, not to act as it. WindowsIdentity
        // refuses such a token ("cannot be duplicated"), so the SID and group membership are read from the token.
        pipe.RunAsClient(() => { sid = ThreadTokenUser(); privileged = InAnyGroup(BuiltinAdministratorsSid, NetworkConfigurationOperatorsSid); });
        if (sid is null) throw new UnauthorizedAccessException("cannot read the caller's user");
        string user;
        try { user = sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { user = sid.Value; }
        return new Caller(user, (int)session, (int)pid, privileged);
    }

    static readonly SecurityIdentifier BuiltinAdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier NetworkConfigurationOperatorsSid = new(WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid, null);

    // Does the caller's token carry one of these groups, ENABLED? Read from the token's groups directly:
    // CheckTokenMembership needs an impersonation-level token, but the client connects at Identification level
    // (verified on the pawsktop VM: CheckTokenMembership there reported a real elevated admin as a non-member,
    // which wrongly refused every privileged intent). An admin's filtered (non-elevated) token lists the
    // Administrators SID as deny-only, so requiring SE_GROUP_ENABLED treats a non-elevated admin as unprivileged.
    static bool InAnyGroup(params SecurityIdentifier[] groups)
    {
        const uint TokenQuery = 0x0008; const int TokenGroupsClass = 2;
        const uint SE_GROUP_ENABLED = 0x4;
        if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token)) return false;
        try
        {
            GetTokenInformation(token, TokenGroupsClass, IntPtr.Zero, 0, out var size);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenGroupsClass, buf, size, out _)) return false;
                var count = Marshal.ReadInt32(buf);
                var entry = IntPtr.Add(buf, IntPtr.Size);   // TOKEN_GROUPS: DWORD GroupCount; then SID_AND_ATTRIBUTES[] (aligned)
                for (var i = 0; i < count; i++)
                {
                    var sidPtr = Marshal.ReadIntPtr(entry);
                    var attr = (uint)Marshal.ReadInt32(entry, IntPtr.Size);
                    if ((attr & SE_GROUP_ENABLED) != 0)
                    {
                        var sid = new SecurityIdentifier(sidPtr);
                        if (groups.Any(g => g.Equals(sid))) return true;
                    }
                    entry = IntPtr.Add(entry, IntPtr.Size + 8);   // SID_AND_ATTRIBUTES = PSID (IntPtr) + DWORD, padded to 8
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
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
    static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientSessionId);
}
