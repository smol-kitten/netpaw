namespace NetPaw.Connectivity;

/// <summary>G4: the two liveness questions the commit-confirm timer asks after an apply (docs/DESIGN-G4-commit-confirm.md).
/// Managed code only: no child process per tick (R7). Faked in tests; the real one runs over <see cref="NetworkProbe"/>.</summary>
public interface ILiveness
{
    /// <summary>One ICMP echo to the profile's gateway, 1 s. True = a reply.</summary>
    Task<bool> GatewayUp(string gateway, CancellationToken ct);
    /// <summary>One UDP query for "." to the profile's first DNS server, 1 s. True = any reply (an error rcode still means the server answers).</summary>
    Task<bool> DnsUp(string server, CancellationToken ct);
}

/// <summary>The production liveness: <see cref="System.Net.NetworkInformation.Ping"/> and one UDP DNS query (<see cref="DnsQuery.Ask"/>).</summary>
public sealed class ProbeLiveness(IProbe probe) : ILiveness
{
    public const int TimeoutMs = 1000;
    public async Task<bool> GatewayUp(string gateway, CancellationToken ct) => (await probe.Ping(gateway, TimeoutMs, ct).ConfigureAwait(false)).Ok;
    public async Task<bool> DnsUp(string server, CancellationToken ct) => (await DnsQuery.Ask(server, ".", TimeoutMs, ct).ConfigureAwait(false)).Ok;
}
