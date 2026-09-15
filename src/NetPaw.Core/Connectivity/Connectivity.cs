using System.Net;
using System.Net.NetworkInformation;
using NetPaw.Adapters;

namespace NetPaw.Connectivity;

/// <summary>One probe answer: reachable or not, and how long it took.</summary>
public sealed record ProbeResult(string Target, bool Ok, int Ms, string? Detail = null);

/// <summary>ICMP/DNS probes behind an interface so the state machine is testable without a network.</summary>
public interface IProbe
{
    Task<ProbeResult> Ping(string host, int timeoutMs, CancellationToken ct);
    Task<ProbeResult> Resolve(string host, int timeoutMs, CancellationToken ct);
    /// <summary>GET a URL and report whether the body equals <paramref name="expectedBody"/> — a captive portal answers with its login page instead.</summary>
    Task<ProbeResult> Http(string url, string expectedBody, int timeoutMs, CancellationToken ct) => Task.FromResult(new ProbeResult(url, true, 0, "not implemented"));
}

public sealed class NetworkProbe : IProbe
{
    static readonly HttpClient Http1 = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<ProbeResult> Http(string url, string expectedBody, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            using var res = await Http1.GetAsync(url, cts.Token);
            var body = res.IsSuccessStatusCode ? (await res.Content.ReadAsStringAsync(cts.Token)).Trim() : "";
            var ok = res.IsSuccessStatusCode && body == expectedBody;
            return new ProbeResult(url, ok, (int)sw.ElapsedMilliseconds, ok ? null : (int)res.StatusCode is 301 or 302 or 303 or 307 ? "redirected: " + res.Headers.Location : $"HTTP {(int)res.StatusCode}, body differs");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return new ProbeResult(url, false, (int)sw.ElapsedMilliseconds, ex.GetBaseException().Message); }
    }

    public async Task<ProbeResult> Ping(string host, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var r = await p.SendPingAsync(host, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
            return new ProbeResult(host, r.Status == IPStatus.Success, (int)sw.ElapsedMilliseconds, r.Status.ToString());
        }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            return new ProbeResult(host, false, (int)sw.ElapsedMilliseconds, ex.GetBaseException().Message);
        }
    }

    public async Task<ProbeResult> Resolve(string host, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            return new ProbeResult(host, addrs.Length > 0, (int)sw.ElapsedMilliseconds, addrs.FirstOrDefault()?.ToString());
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            return new ProbeResult(host, false, (int)sw.ElapsedMilliseconds, ex.GetBaseException().Message);
        }
    }
}

/// <summary>Coarse verdicts an admin acts on. Ordered from worst to best.</summary>
public enum NetState { Unknown, LinkDown, NoAddress, NoGateway, IntranetOnly, DnsBroken, Online, ChecksOff, VSwitchUplink, CaptivePortal }

/// <summary>Everything the info toast shows and the monitor compares.</summary>
public sealed record Snapshot(
    DateTimeOffset At,
    AdapterInfo? Adapter,
    bool Link,
    bool HasAddress,
    bool HasGateway,
    IReadOnlyList<ProbeResult> Intranet,
    IReadOnlyList<ProbeResult> Internet,
    ProbeResult? DnsCheck,
    bool ChecksEnabled,
    ProbeResult? Captive = null)
{
    public bool GatewayOk => Intranet.Any(p => p.Ok);
    public bool InternetOk => Internet.Any(p => p.Ok);
    public bool DnsOk => DnsCheck?.Ok ?? false;

    public NetState State
    {
        get
        {
            if (Adapter is null) return NetState.Unknown;
            if (!Link) return NetState.LinkDown;
            if (Adapter.VSwitchUplink) return NetState.VSwitchUplink;
            if (!HasAddress) return NetState.NoAddress;
            if (!ChecksEnabled) return HasGateway ? NetState.ChecksOff : NetState.NoGateway;
            if (!HasGateway && !GatewayOk) return NetState.NoGateway;
            if (!GatewayOk && !InternetOk) return NetState.NoGateway;
            if (!InternetOk) return NetState.IntranetOnly;
            if (DnsCheck is not null && !DnsOk) return NetState.DnsBroken;
            if (Captive is { Ok: false }) return NetState.CaptivePortal;
            return NetState.Online;
        }
    }

    public static string Describe(NetState s) => s switch
    {
        NetState.LinkDown => "Cable unplugged / link down",
        NetState.NoAddress => "Link up, no IPv4 address",
        NetState.NoGateway => "No reachable gateway",
        NetState.IntranetOnly => "Intranet only — internet unreachable",
        NetState.DnsBroken => "Internet reachable, DNS failing",
        NetState.Online => "Online",
        NetState.ChecksOff => "Configured (checks off)",
        NetState.VSwitchUplink => "Hyper-V switch uplink — host address is on the vEthernet adapter",
        NetState.CaptivePortal => "Captive portal — a login page intercepts web traffic",
        _ => "Unknown",
    };

    /// <summary>true = something an admin wants to hear about.</summary>
    public static bool IsProblem(NetState s) => s is NetState.LinkDown or NetState.NoAddress or NetState.NoGateway or NetState.IntranetOnly or NetState.DnsBroken or NetState.CaptivePortal;
}

public sealed class CheckSettings
{
    /// <summary>Off by default: no probes leave the machine unless the admin opts in.</summary>
    public bool Enabled { get; set; }
    /// <summary>Extra intranet hosts; the default gateway is always probed first.</summary>
    public List<string> IntranetTargets { get; set; } = [];
    public List<string> InternetTargets { get; set; } = ["1.1.1.1", "9.9.9.9"];
    /// <summary>Host resolved to prove DNS works; empty = skip the DNS check.</summary>
    public string DnsCheckHost { get; set; } = "www.msftconnecttest.com";
    public int IntervalSeconds { get; set; } = 30;
    /// <summary>NCSI-style probe: this URL must return exactly the expected body; a portal returns its login page. Empty = skip.</summary>
    public string CaptiveProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";
    public string CaptiveProbeBody { get; set; } = "Microsoft Connect Test";
    public int TimeoutMs { get; set; } = 1000;
    /// <summary>Notify on state changes (lost internet, lost link, back online).</summary>
    public bool MonitorMode { get; set; }
    /// <summary>Keep the info toast on screen while the state is a problem; it closes itself on recovery.</summary>
    public bool StickyAlerts { get; set; } = true;
}

/// <summary>Runs one round of probes for an adapter. Pure orchestration; probes injected.</summary>
public sealed class ConnectivityChecker(IProbe probe)
{
    public async Task<Snapshot> Check(AdapterInfo? adapter, CheckSettings s, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        if (adapter is null) return new Snapshot(now, null, false, false, false, [], [], null, s.Enabled);
        // A 169.254.x.x self-assigned address means "DHCP did not answer", not "configured".
        var link = adapter.Up; var hasAddr = adapter.Addresses.Any(a => !a.Address.StartsWith("169.254.")); var hasGw = adapter.HasGateway;
        if (!s.Enabled || !link || !hasAddr) return new Snapshot(now, adapter, link, hasAddr, hasGw, [], [], null, s.Enabled);

        var intranetTargets = adapter.Gateways.Take(1).Concat(s.IntranetTargets).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        var intranet = await Task.WhenAll(intranetTargets.Select(t => probe.Ping(t, s.TimeoutMs, ct)));
        var internet = await Task.WhenAll(s.InternetTargets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => probe.Ping(t, s.TimeoutMs, ct)));
        ProbeResult? dns = null, captive = null;
        if (!string.IsNullOrWhiteSpace(s.DnsCheckHost) && internet.Any(p => p.Ok)) dns = await probe.Resolve(s.DnsCheckHost, s.TimeoutMs * 2, ct);
        if (!string.IsNullOrWhiteSpace(s.CaptiveProbeUrl) && internet.Any(p => p.Ok) && (dns is null || dns.Ok)) captive = await probe.Http(s.CaptiveProbeUrl, s.CaptiveProbeBody, s.TimeoutMs * 3, ct);
        return new Snapshot(now, adapter, link, hasAddr, hasGw, intranet, internet, dns, true, captive);
    }
}

/// <summary>Compares consecutive snapshots and says what changed, in admin words.</summary>
public static class Transitions
{
    public sealed record Change(NetState From, NetState To, string Title, string Text, bool IsProblem);

    public static Change? Detect(Snapshot? previous, Snapshot current)
    {
        var from = previous?.State ?? NetState.Unknown;
        var to = current.State;
        if (from == to || from == NetState.Unknown && !Snapshot.IsProblem(to)) return null;
        var name = current.Adapter?.Name ?? "adapter";
        var (title, text) = to switch
        {
            NetState.LinkDown => ("Link down", $"{name}: cable unplugged or port down."),
            NetState.NoAddress => ("No address", $"{name} is up but has no IPv4 address (DHCP not answering?)."),
            NetState.NoGateway => ("Gateway unreachable", $"{name}: no reachable gateway — check the switch/router."),
            NetState.IntranetOnly => ("Internet lost", $"{name}: intranet still reachable, internet is not."),
            NetState.DnsBroken => ("DNS failing", $"{name}: internet pings work but names do not resolve."),
            NetState.CaptivePortal => ("Captive portal", $"{name}: a login page intercepts web traffic — open a browser to sign in."),
            NetState.Online => ("Back online", $"{name}: {Snapshot.Describe(from).ToLowerInvariant()} → online."),
            NetState.ChecksOff => ("Configured", $"{name}: address and gateway present."),
            _ => ("Network changed", Snapshot.Describe(to)),
        };
        return new Change(from, to, title, text, Snapshot.IsProblem(to));
    }
}
