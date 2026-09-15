using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPaw.Telemetry;

/// <summary>
/// Minimal CatTelemetry ingest client (telemetry.catboy.systems). Used only by the opt-in
/// telemetry build of the tray and by <c>netpaw-cli telemetry adopt</c>. Everything sent is
/// listed in docs/TELEMETRY.md: no addresses, no adapter or host names, no profile contents.
/// The "machine" identity is a random install id, not the computer name.
/// </summary>
public sealed class TelemetryClient : IDisposable
{
    public const string DefaultEndpoint = "https://telemetry.catboy.systems";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly HttpClient _http;
    readonly ConcurrentQueue<Dictionary<string, object?>> _errors = new();
    readonly SemaphoreSlim _flushLock = new(1, 1);
    readonly Timer _flush;
    readonly string _version, _installId, _os;
    public Action<string>? Log { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sent { get; private set; }

    public TelemetryClient(string endpoint, string token, string version, string installId)
    {
        _http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("X-Project-Token", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NetPaw/{version}");
        _version = version; _installId = installId; _os = OsLabel();
        _flush = new Timer(_ => _ = FlushAsync(), null, 5000, 5000);
    }

    static string OsLabel()
    {
        var v = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() ? $"Windows {v.Major}.{v.Minor}.{v.Build}" : Environment.OSVersion.Platform.ToString();
    }

    Dictionary<string, object?> Context(Dictionary<string, object?>? extra = null)
    {
        var ctx = new Dictionary<string, object?> { ["version"] = _version, ["os"] = _os, ["install"] = _installId, ["environment"] = "production" };
        if (extra is not null) foreach (var kv in extra) ctx[kv.Key] = kv.Value;
        return ctx;
    }

    /// <summary>An error class + message only; callers pass pre-scrubbed text (no command output).</summary>
    public void Error(string type, string message, string level = "error", Dictionary<string, object?>? context = null, Exception? ex = null)
    {
        if (!Enabled) return;
        _errors.Enqueue(new Dictionary<string, object?>
        {
            ["type"] = type, ["message"] = message, ["level"] = level, ["version"] = _version,
            ["trace"] = ex?.StackTrace, ["file"] = ex?.TargetSite?.DeclaringType?.FullName, ["context"] = Context(context),
        });
        if (_errors.Count >= 10) _ = FlushAsync();
    }

    public Task Event(string category, string action, string? label = null, double? value = null, Dictionary<string, object?>? metadata = null) =>
        Enabled ? Send("api/v2/events", new { events = new[] { new { category, action, label, value, metadata = Context(metadata) } } }) : Task.CompletedTask;

    public Task Heartbeat(string status = "healthy", Dictionary<string, object?>? metadata = null) =>
        Enabled ? Send("api/v2/heartbeat", new { status, metadata = Context(metadata), version = _version }) : Task.CompletedTask;

    public async Task FlushAsync()
    {
        if (_errors.IsEmpty || !await _flushLock.WaitAsync(0)) return;
        try
        {
            var batch = new List<Dictionary<string, object?>>();
            while (_errors.TryDequeue(out var e) && batch.Count < 20) batch.Add(e);
            if (batch.Count > 0) await Send("api/v2/errors", new { errors = batch });
        }
        finally { _flushLock.Release(); }
    }

    async Task Send(string path, object body)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(path, body, Json);
            Sent++;
            Log?.Invoke($"telemetry {path} -> {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log?.Invoke($"telemetry {path} failed: {ex.Message}"); }
    }

    // ---- adoption (operator, one-time) ------------------------------------------------------

    public sealed record AdoptRequest(string Id, string Secret, string Code, DateTimeOffset ExpiresAt);

    /// <summary>Asks the hub for a 6-digit code; the operator confirms it in Workflow Manager → Adoptions.</summary>
    public static async Task<AdoptRequest> RequestAdoption(string endpoint, string project, string version, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var res = await http.PostAsJsonAsync(endpoint.TrimEnd('/') + "/api/adopt/request", new { project, host = "release-build", environment = "production", version }, Json, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        string S(params string[] names) { foreach (var n in names) if (doc.TryGetProperty(n, out var v)) return v.ToString(); return ""; }
        var exp = DateTimeOffset.TryParse(S("expires_at", "expiresAt"), out var e) ? e : DateTimeOffset.Now.AddMinutes(10);
        return new AdoptRequest(S("id", "request_id"), S("secret", "poll_secret"), S("code"), exp);
    }

    /// <summary>Long-polls until confirmed (token), rejected (null) or expired (null).</summary>
    public static async Task<string?> WaitForToken(string endpoint, AdoptRequest req, Action<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        while (DateTimeOffset.Now < req.ExpiresAt && !ct.IsCancellationRequested)
        {
            try
            {
                var doc = await http.GetFromJsonAsync<JsonElement>($"{endpoint.TrimEnd('/')}/api/adopt/status?id={Uri.EscapeDataString(req.Id)}&secret={Uri.EscapeDataString(req.Secret)}&wait=25", ct);
                var state = doc.TryGetProperty("state", out var s) ? s.GetString() : null;
                progress?.Invoke(state ?? "?");
                if (state == "confirmed" && doc.TryGetProperty("token", out var t)) return t.GetString();
                if (state is "rejected" or "expired") return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { progress?.Invoke("retry: " + ex.Message); await Task.Delay(5000, ct); }
        }
        return null;
    }

    public void Dispose() { _flush.Dispose(); try { FlushAsync().GetAwaiter().GetResult(); } catch { } _flushLock.Dispose(); _http.Dispose(); }
}
