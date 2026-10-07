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

[JsonConverter(typeof(JsonStringEnumConverter<RequestKind>))]
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
        cts.CancelAfter(timeoutMs + 15_000);
        try { return await Exchange(request, timeoutMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "the NetPaw service did not answer within 15 s"); }
        catch (IOException ex) { return (null, "pipe error talking to the NetPaw service: " + ex.Message); }
    }

    static async Task<(ServiceResponse? Response, string? Error)> Exchange(ServiceRequest request, int timeoutMs, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);   // lets the service read who is calling; it cannot act as us
        try { await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return (null, "NetPaw service not running (no answer on the pipe)"); }
        catch (UnauthorizedAccessException) { return (null, "access to the NetPaw service denied for this user"); }
        await pipe.WriteAsync(ServiceProtocol.Encode(request), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var line = await ServiceProtocol.ReadLine(pipe, ct).ConfigureAwait(false);
        if (line is null) return (null, "the service closed the pipe without an answer");
        try { return (JsonSerializer.Deserialize<ServiceResponse>(line, ServiceProtocol.Json), null); }
        catch (JsonException ex) { return (null, "unreadable answer from the service: " + ex.Message); }
    }
}
