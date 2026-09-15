using NetPaw.Net;

namespace NetPaw.Model;

/// <summary>An IPv4 address with prefix length ("192.168.88.10/24").</summary>
public sealed record IpAddr(string Address, int PrefixLength)
{
    public string Mask => IpMath.PrefixToMask(PrefixLength);
    public string Network => IpMath.NetworkOf(Address, PrefixLength);
    public bool Contains(string ip) => IpMath.Contains(Address, PrefixLength, ip);
    public override string ToString() => $"{Address}/{PrefixLength}";

    public static IpAddr Parse(string text) =>
        IpMath.TryParseCidr(text, out var a) ? a : throw new FormatException($"Not an IPv4 address/prefix: '{text}'");
}
