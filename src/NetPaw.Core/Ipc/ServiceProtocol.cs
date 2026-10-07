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

public enum RequestKind { None, Status, ApplyProfile, Dhcp, Reach, ClearTemp, DeleteRoute, ResetAdapter, Renew, Release, Prefer, SetMtu }

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
}

public sealed record ServiceResponse(int V, bool Ok, string? Error = null, ServiceStatus? Status = null)
{
    /// <summary>What a change request did: every step with its result, warnings and the verify diff.</summary>
    public ApplyReport? Apply { get; init; }
    public static ServiceResponse Fail(string error) => new(ServiceProtocol.Version, false, error);
}

public sealed record StepReport(string Description, string Command, int ExitCode, bool Ok, string Output);

public sealed record ApplyReport(string Title, string Adapter, bool Success, bool Aborted,
    IReadOnlyList<string> Warnings, IReadOnlyList<StepReport> Steps, IReadOnlyList<string> VerifyDiffs)
{
    /// <summary>Keeps answers well under <see cref="ServiceProtocol.MaxLine"/>: command output is cut per step.</summary>
    public const int MaxOutput = 1024;

    public static ApplyReport From(Planning.ApplyPlan plan, Planning.ApplyOutcome? outcome) => new(
        plan.Title, plan.Adapter, outcome?.Success ?? true, outcome?.Aborted ?? false, [.. plan.Warnings],
        outcome is null ? [] : outcome.Results.Select(r => new StepReport(r.Step.Description, r.Step.CommandLine, r.ExitCode, r.Ok,
            r.Output.Length > MaxOutput ? r.Output[..MaxOutput] + " …" : r.Output)).ToList(),
        outcome is null ? [] : [.. outcome.VerifyDiffs]);
}

/// <summary>What <see cref="RequestKind.Status"/> answers: enough for a caller to tell the service is
/// there, which build it is, and who the service thinks is calling.</summary>
public sealed record ServiceStatus(string Version, int ProtocolVersion, string Caller, int CallerSession);

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

    public static ServiceRequest Profile(Model.Profile p, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ApplyProfile) { Profile = p, Adapter = adapter };
    public static ServiceRequest Managed(string profileId, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ApplyProfile) { ProfileId = profileId, Adapter = adapter };
    public static ServiceRequest Dhcp(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = adapter };
    public static ServiceRequest Reach(string target, string? adapter = null, bool? replacePrimary = null) => new(ServiceProtocol.Version, RequestKind.Reach) { Target = target, Adapter = adapter, ReplacePrimary = replacePrimary };
    public static ServiceRequest ClearTemp() => new(ServiceProtocol.Version, RequestKind.ClearTemp);
    public static ServiceRequest DeleteRoute(string prefix, string? gateway, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.DeleteRoute) { RoutePrefix = prefix, RouteGateway = gateway, Adapter = adapter };
    public static ServiceRequest ResetAdapter(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.ResetAdapter) { Adapter = adapter };
    public static ServiceRequest Renew(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Renew) { Adapter = adapter };
    public static ServiceRequest Release(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Release) { Adapter = adapter };
    public static ServiceRequest Prefer(string? adapter = null) => new(ServiceProtocol.Version, RequestKind.Prefer) { Adapter = adapter };
    public static ServiceRequest SetMtu(int mtu, string? adapter = null) => new(ServiceProtocol.Version, RequestKind.SetMtu) { Mtu = mtu, Adapter = adapter };
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel, byte[] buffer, int size, out int needed);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
