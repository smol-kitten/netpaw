namespace NetPaw.Connectivity;

public sealed record MtuResult(string Host, int Mtu, int Probes)
{
    public bool Reduced => Mtu < 1500;
}

/// <summary>
/// Path MTU by Don't-Fragment pings: the largest ICMP payload that still passes, plus 28 bytes of IP+ICMP
/// header. A tunnel or PPPoE on the way answers 1452/1492 instead of 1500 and large packets silently die
/// (web pages hang after the handshake). Inconclusive (null) when even a small DF ping fails: ICMP blocked or host down.
/// </summary>
public static class Mtu
{
    public const int Overhead = 28;
    public const int Floor = 1200;

    public static async Task<MtuResult?> Discover(IProbe probe, string host, int timeoutMs = 1000, CancellationToken ct = default)
    {
        var probes = 0;
        async Task<bool> Passes(int mtu) { probes++; return (await probe.PingDf(host, mtu - Overhead, timeoutMs, ct)).Ok; }
        if (!await Passes(Floor)) return null;
        if (await Passes(1500)) return new MtuResult(host, 1500, probes);
        int lo = Floor, hi = 1500;                      // lo passes, hi fails
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Passes(mid)) lo = mid; else hi = mid;
        }
        return new MtuResult(host, lo, probes);
    }
}
