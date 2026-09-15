using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace NetPaw.Telemetry;

/// <summary>Where structured events go besides the local log. Both off by default; user-configured servers only.</summary>
public sealed class LogExportSettings
{
    /// <summary>OTLP/HTTP logs endpoint, e.g. http://collector.corp:4318/v1/logs. Empty = off.</summary>
    public string OtlpEndpoint { get; set; } = "";
    /// <summary>Optional "Header: value" lines sent with every OTLP request (e.g. Authorization).</summary>
    public List<string> OtlpHeaders { get; set; } = [];
    /// <summary>Syslog server host[:port] (UDP, RFC 5424). Empty = off.</summary>
    public string SyslogServer { get; set; } = "";
    public int SyslogFacility { get; set; } = 1; // user-level
    /// <summary>Send monitor state changes and configuration findings.</summary>
    public bool ExportIncidents { get; set; } = true;
    /// <summary>Send apply/reach/scan outcomes.</summary>
    public bool ExportActions { get; set; } = true;
    public bool Enabled => OtlpEndpoint.Length > 0 || SyslogServer.Length > 0;
}

/// <summary>A log record as NetPaw emits it; the exporters map it to OTLP and syslog.</summary>
public sealed record LogRecord(DateTimeOffset At, string Severity, string Body, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Structured log export for the telemetry build: OTLP/HTTP (JSON) and RFC 5424 syslog over UDP.
/// Same privacy contract as the hub telemetry, except that the *user* chose these servers, so the
/// records carry the adapter name and state text (still no addresses or profile contents).
/// </summary>
public sealed class LogExporter : IDisposable
{
    readonly LogExportSettings _s;
    readonly string _service, _version, _host;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    UdpClient? _udp; IPEndPoint? _syslogEp;
    public Action<string>? Log { get; set; }

    public LogExporter(LogExportSettings s, string version, string hostName)
    {
        _s = s; _service = "netpaw"; _version = version; _host = hostName;
        foreach (var h in s.OtlpHeaders) { var i = h.IndexOf(':'); if (i > 0) _http.DefaultRequestHeaders.TryAddWithoutValidation(h[..i].Trim(), h[(i + 1)..].Trim()); }
        if (s.SyslogServer.Length > 0) _syslogEp = ParseEndpoint(s.SyslogServer, 514);
    }

    public static IPEndPoint? ParseEndpoint(string spec, int defaultPort)
    {
        var host = spec.Trim(); var port = defaultPort;
        if (host.Length == 0) return null;
        var i = host.LastIndexOf(':');
        if (i > 0 && int.TryParse(host[(i + 1)..], out var p)) { port = p; host = host[..i]; }
        try { var addr = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); return addr is null ? null : new IPEndPoint(addr, port); }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
    }

    public async Task Send(LogRecord r)
    {
        if (_s.OtlpEndpoint.Length > 0) await SendOtlp(r);
        if (_syslogEp is not null) SendSyslog(r);
    }

    // ---- OTLP/HTTP JSON (opentelemetry-proto logs v1) ----------------------------------------
    public object OtlpPayload(LogRecord r)
    {
        static object Attr(string k, object? v) => new { key = k, value = v switch { null => new { stringValue = "" }, int i => (object)new { intValue = i.ToString() }, long l => new { intValue = l.ToString() }, double d => new { doubleValue = d }, bool b => new { boolValue = b }, _ => new { stringValue = v.ToString() } } };
        var sev = r.Severity.ToLowerInvariant() switch { "error" or "critical" => 17, "warn" or "warning" => 13, "info" => 9, _ => 5 };
        return new
        {
            resourceLogs = new[] { new {
                resource = new { attributes = new[] { Attr("service.name", _service), Attr("service.version", _version), Attr("host.name", _host) } },
                scopeLogs = new[] { new { scope = new { name = "netpaw" }, logRecords = new[] { new {
                    timeUnixNano = (r.At.ToUnixTimeMilliseconds() * 1_000_000).ToString(),
                    severityNumber = sev, severityText = r.Severity.ToUpperInvariant(),
                    body = new { stringValue = r.Body },
                    attributes = r.Attributes.Select(kv => Attr(kv.Key, kv.Value)).ToArray(),
                } } } } } },
        };
    }

    async Task SendOtlp(LogRecord r)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(_s.OtlpEndpoint, OtlpPayload(r));
            if (!res.IsSuccessStatusCode) Log?.Invoke($"otlp {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log?.Invoke("otlp failed: " + ex.Message); }
    }

    // ---- RFC 5424 syslog over UDP -------------------------------------------------------------
    public string SyslogLine(LogRecord r)
    {
        var sevNum = r.Severity.ToLowerInvariant() switch { "critical" => 2, "error" => 3, "warn" or "warning" => 4, "info" => 6, _ => 7 };
        var pri = _s.SyslogFacility * 8 + sevNum;
        static string Esc(string v) => v.Replace("\\", "\\\\").Replace("]", "\\]").Replace("\"", "\\\"");
        var sd = "[netpaw@0 " + string.Join(" ", r.Attributes.Select(kv => $"{Sanitize(kv.Key)}=\"{Esc(kv.Value?.ToString() ?? "")}\"")) + "]";
        return $"<{pri}>1 {r.At.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {_host} netpaw - - {sd} {r.Body}";
    }

    static string Sanitize(string k) => new(k.Where(c => c > 32 && c < 127 && c != '=' && c != ']' && c != '"').Take(32).ToArray());

    void SendSyslog(LogRecord r)
    {
        try
        {
            _udp ??= new UdpClient();
            var bytes = Encoding.UTF8.GetBytes(SyslogLine(r));
            _udp.Send(bytes, bytes.Length, _syslogEp!);
        }
        catch (SocketException ex) { Log?.Invoke("syslog failed: " + ex.Message); }
    }

    public void Dispose() { _http.Dispose(); _udp?.Dispose(); }
}
