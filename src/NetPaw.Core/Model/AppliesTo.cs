using System.Text.RegularExpressions;

namespace NetPaw.Model;

/// <summary>
/// Optional adapter binding on a <see cref="Profile"/> (t-0e5511). When present, the service applies the profile
/// only to an adapter whose name matches one of <see cref="Names"/> (case-insensitive, <c>*</c>/<c>?</c>
/// wildcards) or whose MAC is one of <see cref="Macs"/>; anything else is refused and logged. Absent/empty = the
/// profile applies to any adapter the caller names, so fleet-generic managed profiles keep working.
///
/// A match here is also what lets a profile target a non-host-facing (virtual) adapter, which the service
/// otherwise refuses. Matching is on plain strings so the model stays free of the adapter layer; the patterns are
/// translated to an anchored regex with every non-wildcard character escaped, so a profile cannot inject a regex.
/// </summary>
public sealed class AppliesTo
{
    public List<string> Names { get; set; } = [];
    public List<string> Macs { get; set; } = [];

    public bool IsEmpty => Names.Count == 0 && Macs.Count == 0;

    public bool Matches(string adapterName, string mac) =>
        Names.Any(p => NameMatches(p, adapterName)) || Macs.Any(m => MacEquals(m, mac));

    /// <summary>For the refusal message.</summary>
    public string Describe() => string.Join(" / ", Names.Concat(Macs.Select(m => "mac " + m)));

    static bool NameMatches(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var rx = "^" + string.Concat(pattern.Select(c => c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) })) + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static string NormMac(string m) => new([.. m.Where(Uri.IsHexDigit)]);
    static bool MacEquals(string a, string b) => NormMac(a) is { Length: > 0 } na && na.Equals(NormMac(b), StringComparison.OrdinalIgnoreCase);
}
