using System.Text.RegularExpressions;
using NetPaw.Model;
using NetPaw.Net;

namespace NetPaw.Ipc;

/// <summary>
/// Checks a profile that arrives over the pipe before the service plans anything from it. A profile from
/// another user is untrusted input: every value that ends up in a netsh or PowerShell argument must be an
/// IPv4 address, a number in range, or a DNS name. Free text (name, note) only reaches titles and the log,
/// and is bounded. Stricter than <see cref="Profile.Validate"/>, which it includes.
/// </summary>
public static partial class ProfileGuard
{
    public const int MaxAddresses = 32, MaxDns = 8, MaxRoutes = 64, MaxName = 100, MaxNote = 1000;

    // RFC 1123 labels only. Excludes every kind of quote (PowerShell also treats ‘ ’ ‚ ‛ as single quotes).
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex DnsName();

    public static IReadOnlyList<string> Check(Profile p)
    {
        var errors = new List<string>();
        // Addresses first: Validate() does IP math on them and would throw on garbage.
        if (p.Addresses.Count > MaxAddresses) errors.Add($"At most {MaxAddresses} addresses.");
        foreach (var a in p.Addresses)
            if (!IpMath.IsIPv4(a.Address) || a.PrefixLength is < 0 or > 32) errors.Add($"Address '{a.Address}/{a.PrefixLength}' is not an IPv4 address with a prefix 0..32.");
        if (errors.Count > 0) return errors;
        errors.AddRange(p.Validate());
        Text("Name", p.Name, MaxName, errors);
        Text("Note", p.Note, MaxNote, errors);
        if (p.Dns.Count > MaxDns) errors.Add($"At most {MaxDns} DNS servers.");
        if (p.DnsSuffix is not null && !DnsName().IsMatch(p.DnsSuffix)) errors.Add("DNS suffix must be a DNS name (letters, digits, '-' and '.').");
        Metric("Gateway metric", p.GatewayMetric, errors);
        Metric("Interface metric", p.InterfaceMetric, errors);
        if (p.Routes.Count > MaxRoutes) errors.Add($"At most {MaxRoutes} routes.");
        foreach (var r in p.Routes)
        {
            if (!IpMath.IsIPv4(r.Destination) || r.PrefixLength is < 0 or > 32) errors.Add($"Route destination '{r.Destination}/{r.PrefixLength}' is not an IPv4 prefix.");
            if (r.Gateway is not null && !IpMath.IsIPv4(r.Gateway)) errors.Add($"Route gateway '{r.Gateway}' is not an IPv4 address.");
            Metric("Route metric", r.Metric, errors);
        }
        return errors;
    }

    /// <summary>"a.b.c.d" or "a.b.c.d/n": the only shapes a reach target or route prefix may take.</summary>
    public static bool IsIPv4OrPrefix(string? s)
    {
        if (s is null) return false;
        var slash = s.IndexOf('/');
        if (slash < 0) return IpMath.IsIPv4(s);
        return IpMath.IsIPv4(s[..slash]) && int.TryParse(s[(slash + 1)..], System.Globalization.NumberStyles.None, null, out var n) && n is >= 0 and <= 32;
    }

    static void Text(string what, string? s, int max, List<string> errors)
    {
        if (s is null) return;
        if (s.Length > max) errors.Add($"{what} is longer than {max} characters.");
        if (s.Any(char.IsControl)) errors.Add($"{what} contains control characters.");
    }

    static void Metric(string what, int? m, List<string> errors)
    {
        if (m is < 1 or > 9999) errors.Add($"{what} must be 1..9999.");
    }
}
