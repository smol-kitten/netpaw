using System.Net;
using NetPaw.Model;

namespace NetPaw.Net;

/// <summary>IPv4 helpers. Everything here is pure so it can be unit-tested on any OS.</summary>
public static class IpMath
{
    public static bool IsIPv4(string? s) =>
        s is not null && IPAddress.TryParse(s, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && s.Count(c => c == '.') == 3;

    public static uint ToUInt(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static string FromUInt(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    public static uint PrefixToMaskBits(int prefix) => prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);

    public static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        return FromUInt(PrefixToMaskBits(prefix));
    }

    public static int MaskToPrefix(string mask)
    {
        var bits = ToUInt(mask);
        var prefix = 0;
        while (prefix < 32 && (bits & (0x80000000u >> prefix)) != 0) prefix++;
        if (bits != PrefixToMaskBits(prefix)) throw new FormatException($"'{mask}' is not a contiguous subnet mask.");
        return prefix;
    }

    public static string NetworkOf(string ip, int prefix) => FromUInt(ToUInt(ip) & PrefixToMaskBits(prefix));

    public static string BroadcastOf(string ip, int prefix) => FromUInt(ToUInt(ip) | ~PrefixToMaskBits(prefix));

    public static bool Contains(string network, int prefix, string ip) =>
        IsIPv4(ip) && (ToUInt(network) & PrefixToMaskBits(prefix)) == (ToUInt(ip) & PrefixToMaskBits(prefix));

    public static bool IsPrivate(string ip)
    {
        var v = ToUInt(ip);
        return (v >> 24) == 10 || (v >> 20) == 0xAC1 || (v >> 16) == 0xC0A8 || (v >> 16) == 0xA9FE;
    }

    /// <summary>
    /// Accepts "a.b.c.d/24", "a.b.c.d 255.255.255.0", "a.b.c.d/255.255.255.0" or a bare "a.b.c.d" (uses <paramref name="defaultPrefix"/>).
    /// </summary>
    public static bool TryParseCidr(string? text, out IpAddr addr, int defaultPrefix = 24)
    {
        addr = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace('/', ' ');
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || !IsIPv4(parts[0])) return false;
        var prefix = defaultPrefix;
        if (parts.Length == 2)
        {
            if (int.TryParse(parts[1], out var p)) prefix = p;
            else if (IsIPv4(parts[1])) { try { prefix = MaskToPrefix(parts[1]); } catch (FormatException) { return false; } }
            else return false;
        }
        if (prefix is < 0 or > 32) return false;
        addr = new IpAddr(parts[0], prefix);
        return true;
    }

    /// <summary>
    /// Host addresses to try for "put me in the same subnet as X": counts down from the top of the
    /// subnet (so it does not collide with the .1/.10 style defaults gear ships with), skipping the
    /// target, network and broadcast addresses and everything in <paramref name="avoid"/>.
    /// </summary>
    public static IEnumerable<string> HostCandidates(string anyIpInSubnet, int prefix, IEnumerable<string> avoid)
    {
        if (prefix >= 31) yield break;
        var avoidSet = new HashSet<uint>(avoid.Where(IsIPv4).Select(ToUInt)) { ToUInt(anyIpInSubnet) };
        var net = ToUInt(NetworkOf(anyIpInSubnet, prefix));
        var bcast = ToUInt(BroadcastOf(anyIpInSubnet, prefix));
        for (var v = bcast - 1; v > net; v--)
            if (!avoidSet.Contains(v)) yield return FromUInt(v);
    }
}
