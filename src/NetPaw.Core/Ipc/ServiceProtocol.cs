using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPaw.Ipc;

/// <summary>
/// Wire format between the tray/CLI and netpaw-svc (docs/PLAN-v0.14.md, "IPC"): one JSON object per
/// line over the named pipe <see cref="PipeName"/>, at most <see cref="MaxLine"/> bytes, versioned.
/// Requests are typed intents; the service never receives a command line.
/// </summary>
public static class ServiceProtocol
{
    public const string PipeName = "NetPaw";
    public const int Version = 1;
    public const int MaxLine = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Kinds by name only: "kind":1 is refused, not read as the enum's second member.
        Converters = { new JsonStringEnumConverter<RequestKind>(allowIntegerValues: false) },
    };

    /// <summary>One message as UTF-8 JSON plus a newline, the wire unit of the pipe.</summary>
    public static byte[] Encode<T>(T message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length + 1 > MaxLine) throw new InvalidDataException($"message is {bytes.Length} bytes, the limit is {MaxLine}");
        return [.. bytes, (byte)'\n'];
    }

    /// <summary>Reads one line (without the newline). Null at end of stream. Throws when the line exceeds
    /// <see cref="MaxLine"/>, so a client cannot make the service buffer without bound.</summary>
    public static async Task<byte[]?> ReadLine(Stream s, CancellationToken ct = default)
    {
        var buf = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return buf.Length == 0 ? null : throw new InvalidDataException("stream ended inside a message");
            if (one[0] == (byte)'\n') return buf.ToArray();
            if (buf.Length + 1 >= MaxLine) throw new InvalidDataException($"message longer than {MaxLine} bytes");
            buf.WriteByte(one[0]);
        }
    }

    /// <summary>Parses a request line. Unknown versions and unknown or missing kinds are refused with a
    /// reason, never guessed.</summary>
    public static (ServiceRequest? Request, string? Error) ParseRequest(byte[] line)
    {
        ServiceRequest? r;
        try { r = JsonSerializer.Deserialize<ServiceRequest>(line, Json); }
        catch (JsonException ex) { return (null, "invalid JSON: " + ex.Message); }
        if (r is null) return (null, "empty request");
        if (r.V != Version) return (null, $"protocol version {r.V} is not supported (service speaks {Version})");
        if (!Enum.IsDefined(r.Kind) || r.Kind == RequestKind.None) return (null, "unknown request kind");
        return (r, null);
    }
}

public enum RequestKind { None, Status, ApplyProfile, Dhcp, Reach, ClearTemp, DeleteRoute, ResetAdapter, Renew, Release, Prefer, SetMtu, RevertLastApply, InstallUpdate }

/// <summary>One request: what the caller wants, never how. The service resolves the adapter against the live
/// list, checks every value (<see cref="ProfileGuard"/>) and builds the commands itself.</summary>
public sealed record ServiceRequest(int V, RequestKind Kind)
{
    /// <summary>Adapter name as Windows shows it; must match a live adapter. Null = the work adapter.</summary>
    public string? Adapter { get; init; }
    /// <summary>ApplyProfile: a managed profile (from %ProgramData%) by id …</summary>
    public string? ProfileId { get; init; }
    /// <summary>… or a user's own profile, sent whole (the service has no access to the user's %APPDATA%).</summary>
    public Model.Profile? Profile { get; init; }
    /// <summary>Reach: "a.b.c.d" or "a.b.c.d/n".</summary>
    public string? Target { get; init; }
    public bool? ReplacePrimary { get; init; }
    /// <summary>SetMtu: bytes, 576..9000.</summary>
    public int? Mtu { get; init; }
    /// <summary>DeleteRoute: "a.b.c.d/n" and the next hop, if the route has one.</summary>
    public string? RoutePrefix { get; init; }
    public string? RouteGateway { get; init; }
    /// <summary>InstallUpdate: the version to install (null = the newest on the channel). The service resolves it itself; no URL or path is accepted.</summary>
    public string? Version { get; init; }
    /// <summary>InstallUpdate: "stable" (default) or "prerelease".</summary>
    public string? Channel { get; init; }
    /// <summary>InstallUpdate: allow a version that is not newer than the running one (CLI flag only).</summary>
    public bool? AllowDowngrade { get; init; }
}

public sealed record ServiceResponse(int V, bool Ok, string? Error = null, ServiceStatus? Status = null)
{
    /// <summary>What a change request did: every step with its result, warnings and the verify diff.</summary>
    public ApplyReport? Apply { get; init; }
    /// <summary>A failed response with the reason and no report.</summary>
    public static ServiceResponse Fail(string error) => new(ServiceProtocol.Version, false, error);
}

public sealed record StepReport(string Description, string Command, int ExitCode, bool Ok, string Output);

public sealed record ApplyReport(string Title, string Adapter, bool Success, bool Aborted,
    IReadOnlyList<string> Warnings, IReadOnlyList<StepReport> Steps, IReadOnlyList<string> VerifyDiffs)
{
    /// <summary>Keeps answers well under <see cref="ServiceProtocol.MaxLine"/>: command output is cut per step.</summary>
    public const int MaxOutput = 1024;

    /// <summary>Flattens a plan and its outcome into the report the client sees (steps, exit codes, verify diffs).</summary>
    public static ApplyReport From(Planning.ApplyPlan plan, Planning.ApplyOutcome? outcome) => new(
        plan.Title, plan.Adapter, outcome?.Success ?? true, outcome?.Aborted ?? false, [.. plan.Warnings],
        outcome is null ? [] : outcome.Results.Select(r => new StepReport(r.Step.Description, r.Step.CommandLine, r.ExitCode, r.Ok,
            r.Output.Length > MaxOutput ? r.Output[..MaxOutput] + " …" : r.Output)).ToList(),
        outcome is null ? [] : [.. outcome.VerifyDiffs]);
}

/// <summary>What <see cref="RequestKind.Status"/> answers: enough for a caller to tell the service is
/// there, which build it is, who the service thinks is calling and at which tier (G12: the effective tier, as the
/// policy gate will see it). <paramref name="Tier"/> is null from a service older than 0.16.</summary>
/// <param name="LastApply">G13: what a RevertLastApply would undo ("DHCP on Ethernet 2 at 00:58 by …"), null when nothing is recorded.</param>
public sealed record ServiceStatus(string Version, int ProtocolVersion, string Caller, int CallerSession, string? Tier = null, string? LastApply = null);

/// <summary>Client side of the pipe, used by the tray and the CLI.</summary>
public static class ServiceClient
{
    /// <summary>Sends one request and reads one response. Null when no service answers within the timeout
    /// (not installed, stopped, or the pipe ACL refused this user: the message says which).</summary>
    public static async Task<(ServiceResponse? Response, string? Error)> Send(ServiceRequest request, int timeoutMs = 2000, CancellationToken ct = default)
    {
        // Bound the whole exchange, not only the connect: a service that accepts but never answers must not hang the caller.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs + AnswerMs);
        try { return await Exchange(request, timeoutMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, $"the NetPaw service did not answer within {(timeoutMs + AnswerMs) / 1000} s"); }
        catch (IOException ex) { return (null, "pipe error talking to the NetPaw service: " + ex.Message); }
        catch (InvalidDataException ex) { return (null, "unusable answer from the NetPaw service: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return (null, "access to the NetPaw service denied: " + ex.Message); }
    }

    const int AnswerMs = 15_000;

    /// <summary>One round trip: connect, verify the server is the NetPaw service, send, read one line; errors come back as text.</summary>
    static async Task<(ServiceResponse? Response, string? Error)> Exchange(ServiceRequest request, int timeoutMs, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);   // lets the service read who is calling; it cannot act as us
        try { await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return (null, "NetPaw service not running (no answer on the pipe)"); }
        catch (UnauthorizedAccessException) { return (null, "access to the NetPaw service denied for this user"); }
        // Anyone can create a pipe with this name while the service is stopped. Talk only to the process the
        // Service Control Manager runs as the NetPaw service; anything else could read requests or fake answers.
        if (OperatingSystem.IsWindows() && ServerCheck.Verify(pipe) is { } impostor) return (null, impostor);
        await pipe.WriteAsync(ServiceProtocol.Encode(request), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var line = await ServiceProtocol.ReadLine(pipe, ct).ConfigureAwait(false);
        if (line is null) return (null, "the service closed the pipe without an answer");
        try { return (JsonSerializer.Deserialize<ServiceResponse>(line, ServiceProtocol.Json), null); }
        catch (JsonException ex) { return (null, "unreadable answer from the service: " + ex.Message); }
    }
}

/// <summary>What a change request did, flattened for a caller (tray/CLI): success, an error to show, and the
/// apply report when there is one.</summary>
public sealed record ChangeResult(bool Ok, string? Error, ApplyReport? Report)
{
    /// <summary>The result when no path can run the change (no service, not elevated).</summary>
    public static ChangeResult Unreachable(string why) => new(false, why, null);
}

public static class ServiceChange
{
    /// <summary>Send one change request and flatten the answer. Synchronous: callers are UI handlers and the
    /// existing CLI path, both of which block. A null response (no service / refused / squatter) becomes Ok=false
    /// with the reason, never an exception.</summary>
    public static ChangeResult Send(ServiceRequest request, int timeoutMs = 4000)
    {
        var (resp, err) = ServiceClient.Send(request, timeoutMs).GetAwaiter().GetResult();
        if (resp is null) return ChangeResult.Unreachable(err ?? "the NetPaw service did not answer");
        return new ChangeResult(resp.Ok, resp.Error, resp.Apply);
    }

    /// <summary>Does a NetPaw service answer the pipe right now? One short Status round-trip; false when not
    /// installed, stopped, refused, or a squatter holds the pipe. The change channel uses this to pick its mode.</summary>
    public static bool ServiceAnswers(int timeoutMs = 800)
    {
        var (resp, _) = ServiceClient.Send(new ServiceRequest(ServiceProtocol.Version, RequestKind.Status), timeoutMs).GetAwaiter().GetResult();
        LastStatus = resp is { Ok: true } ? resp.Status : null;
        return resp is { Ok: true };
    }

    /// <summary>The last Status answer (caller + effective tier as the service sees this process); null when the service did not answer.</summary>
    public static ServiceStatus? LastStatus { get; private set; }

    /// <summary>ApplyProfile request with an inline user profile.</summary>
    public static ServiceRequest Profile(Model.Profile p, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ApplyProfile) { Profile = p, Adapter = adapter };
    /// <summary>ApplyProfile request by managed profile id.</summary>
    public static ServiceRequest Managed(string profileId, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ApplyProfile) { ProfileId = profileId, Adapter = adapter };
    /// <summary>Dhcp request for the adapter (null = the work adapter).</summary>
    public static ServiceRequest Dhcp(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = adapter };
    /// <summary>Reach request for a target address or a.b.c.d/n.</summary>
    public static ServiceRequest Reach(string target, string? adapter = null, bool? replacePrimary = null) => new(ServiceProtocol.Version, RequestKind.Reach) { Target = target, Adapter = adapter, ReplacePrimary = replacePrimary };
    /// <summary>ClearTemp request: remove every temporary address.</summary>
    public static ServiceRequest ClearTemp() => new(ServiceProtocol.Version, RequestKind.ClearTemp);
    /// <summary>DeleteRoute request for a prefix and optional next hop.</summary>
    public static ServiceRequest DeleteRoute(string prefix, string? gateway, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.DeleteRoute) { RoutePrefix = prefix, RouteGateway = gateway, Adapter = adapter };
    /// <summary>ResetAdapter request (disable, enable).</summary>
    public static ServiceRequest ResetAdapter(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ResetAdapter) { Adapter = adapter };
    /// <summary>Renew request (ipconfig /renew).</summary>
    public static ServiceRequest Renew(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Renew) { Adapter = adapter };
    /// <summary>Release request (ipconfig /release).</summary>
    public static ServiceRequest Release(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Release) { Adapter = adapter };
    /// <summary>Prefer request (lowest interface metric for the adapter).</summary>
    public static ServiceRequest Prefer(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Prefer) { Adapter = adapter };
    /// <summary>SetMtu request, 576..9000 bytes.</summary>
    public static ServiceRequest SetMtu(int mtu, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.SetMtu) { Mtu = mtu, Adapter = adapter };
    /// <summary>G13: put the adapter of the last apply back into the state the service snapshotted just before it.</summary>
    public static ServiceRequest RevertLastApply() => new(ServiceProtocol.Version, RequestKind.RevertLastApply);
    /// <summary>Update from CatWinget: the service resolves, downloads, verifies twice and installs. Never a URL or a path.</summary>
    public static ServiceRequest InstallUpdate(string? version = null, string? channel = null, bool allowDowngrade = false) => new(ServiceProtocol.Version, RequestKind.InstallUpdate) { Version = version, Channel = channel, AllowDowngrade = allowDowngrade ? true : null };
}

/// <summary>Is the process on the other end of the pipe the NetPaw service? (review of #75: pipe squatting)</summary>
static class ServerCheck
{
    /// <returns>Null when the pipe server is the running NetPaw service, else why not.</returns>
    public static string? Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)) return "cannot read the pipe server's process id";
        var servicePid = ServiceProcessId("NetPaw");
        if (servicePid is null) return "the NetPaw service is not running, but something else answers on its pipe";
        return servicePid == serverPid ? null : $"the pipe is served by process {serverPid}, not by the NetPaw service (process {servicePid}); refusing to talk to it";
    }

    /// <summary>The PID the SCM reports for a running service. SERVICE_QUERY_STATUS is open to every local user,
    /// and no other process can make the SCM report itself as this service.</summary>
    static uint? ServiceProcessId(string name)
    {
        const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ScStatusProcessInfo = 0, ServiceRunning = 4;
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try
            {
                var buf = new byte[36];   // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, ScStatusProcessInfo, buf, buf.Length, out _)) return null;
                var state = BitConverter.ToUInt32(buf, 4);
                var pid = BitConverter.ToUInt32(buf, 28);
                return state == ServiceRunning && pid != 0 ? pid : null;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>kernel32: the process id of the pipe server behind the handle.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>advapi32: opens the Service Control Manager with the given access.</summary>
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    /// <summary>advapi32: opens one service by name.</summary>
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    /// <summary>advapi32: SERVICE_STATUS_PROCESS for the service (holds its process id).</summary>
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel, byte[] buffer, int size, out int needed);

    /// <summary>advapi32: closes an SCM or service handle.</summary>
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
