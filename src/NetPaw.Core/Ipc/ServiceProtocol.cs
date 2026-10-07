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

public enum RequestKind { None, Status }

/// <summary>One request. Later steps add the intent fields (profile, adapter, target …) here.</summary>
public sealed record ServiceRequest(int V, RequestKind Kind);

public sealed record ServiceResponse(int V, bool Ok, string? Error = null, ServiceStatus? Status = null)
{
    public static ServiceResponse Fail(string error) => new(ServiceProtocol.Version, false, error);
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
