namespace NetPaw.Model;

/// <summary>
/// Optional adapter binding on a <see cref="Profile"/> (t-0e5511). When present, the service applies the profile
/// only to an adapter whose name matches one of <see cref="Names"/> (case-insensitive, <c>*</c>/<c>?</c>
/// wildcards) or whose MAC is one of <see cref="Macs"/>; anything else is refused and logged. Absent/empty = the
/// profile applies to any adapter the caller names, so fleet-generic managed profiles keep working.
///
/// A match here is also what lets a profile target a non-host-facing (virtual) adapter, which the service
/// otherwise refuses. Matching is on plain strings so the model stays free of the adapter layer; name patterns
/// use a bounded iterative glob matcher (no regex), so no pattern an admin writes can make the service hang.
/// </summary>
public sealed class AppliesTo
{
    public List<string> Names { get; set; } = [];
    public List<string> Macs { get; set; } = [];

    public bool IsEmpty => Names.Count == 0 && Macs.Count == 0;

    /// <summary>True when the adapter name matches one of the globs or the MAC equals one of the listed MACs.</summary>
    public bool Matches(string adapterName, string mac) =>
        Names.Any(p => NameMatches(p, adapterName)) || Macs.Any(m => MacEquals(m, mac));

    /// <summary>For the refusal message.</summary>
    public string Describe() => string.Join(" / ", Names.Concat(Macs.Select(m => "mac " + m)));

    /// <summary>Case-insensitive glob (<c>*</c> = any run, <c>?</c> = one char). Iterative with the classic
    /// star-backtrack, so it is O(name·pattern) worst case and cannot blow up — no regex, no ReDoS.</summary>
    static bool NameMatches(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))) { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star != -1) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>A MAC reduced to its hex digits (separators and case ignored).</summary>
    static string NormMac(string m) => new([.. m.Where(Uri.IsHexDigit)]);
    /// <summary>MAC equality after normalisation; an empty MAC never matches.</summary>
    static bool MacEquals(string a, string b) => NormMac(a) is { Length: > 0 } na && na.Equals(NormMac(b), StringComparison.OrdinalIgnoreCase);
}
