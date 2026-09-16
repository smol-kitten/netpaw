using System.Net;

namespace NetPaw.Connectivity;

public enum PortVerdict { Open, Refused, Timeout, Unreachable, Unresolved }

/// <summary>
/// "Is 10.0.0.5:443 open from here?" — one TCP connect, one answer. Refused means the host is there and
/// nothing listens (or a host firewall rejects); timeout means a firewall drops or the host is off;
/// unreachable means no route. Single port only — this is a check, not a scanner.
/// </summary>
public static class PortCheck
{
    /// <summary>Parses "host:port" — host is an IPv4 address or a DNS name, port 1–65535. A bare address is not a port check.</summary>
    public static bool TryParse(string text, out string host, out int port)
    {
        host = ""; port = 0;
        var t = text.Trim(); var colon = t.LastIndexOf(':');
        if (colon <= 0 || colon == t.Length - 1 || t.Count(c => c == ':') != 1) return false;
        if (!int.TryParse(t[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        host = t[..colon];
        if (host.Contains('/') || host.Contains(' ')) return false;
        return IPAddress.TryParse(host, out var ip) ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork : Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    public static async Task<PortResult> Run(IProbe probe, string host, int port, int timeoutMs = 2000, CancellationToken ct = default)
    {
        var r = await probe.Connect(host, port, timeoutMs, ct);
        var verdict = r.Detail switch
        {
            "open" => PortVerdict.Open,
            "refused" => PortVerdict.Refused,
            "unreachable" => PortVerdict.Unreachable,
            "unresolved" => PortVerdict.Unresolved,
            _ => r.Ok ? PortVerdict.Open : PortVerdict.Timeout,
        };
        return new PortResult(host, port, verdict, r.Ms);
    }
}

public sealed record PortResult(string Host, int Port, PortVerdict Verdict, int Ms)
{
    public bool Open => Verdict == PortVerdict.Open;
    /// <summary>One line an admin can act on.</summary>
    public string Text => Verdict switch
    {
        PortVerdict.Open => $"{Host}:{Port} is open ({Ms} ms)",
        PortVerdict.Refused => $"{Host}:{Port} refused — the host answers, nothing listens on {Port} (or its firewall rejects)",
        PortVerdict.Timeout => $"{Host}:{Port} timed out — a firewall drops it, or the host is off",
        PortVerdict.Unreachable => $"{Host} is not reachable from this adapter — no route; try 'reach {Host}' first",
        _ => $"'{Host}' does not resolve — check the DNS suffix or use the address",
    };
    /// <summary>CLI exit code: 0 open, 1 refused/unreachable, 2 timeout, 3 unresolved.</summary>
    public int ExitCode => Verdict switch { PortVerdict.Open => 0, PortVerdict.Timeout => 2, PortVerdict.Unresolved => 3, _ => 1 };
}
