using NetPaw.Adapters;

namespace NetPaw.Model;

/// <summary>Non-blocking warnings for a profile being saved (0.16, plan M2 "duplicate-IP warning"). Errors live in
/// <see cref="Profile.Validate"/> and the editor; these are things that are probably a mistake but can be intended, so
/// the editor shows them and still saves.</summary>
public static class ProfileLint
{
    public static IReadOnlyList<string> Warnings(Profile p, IEnumerable<Profile> others, IEnumerable<AdapterInfo> adapters)
    {
        var w = new List<string>();
        if (p.Dhcp) return w;
        foreach (var a in p.Addresses)
        {
            // The same address live on ANOTHER adapter of this machine right now: applying would create an IP conflict on the host.
            var live = adapters.FirstOrDefault(n => !string.Equals(n.Name, p.Adapter, StringComparison.OrdinalIgnoreCase)
                                                    && n.Addresses.Any(x => x.Address == a.Address));
            if (live is not null) w.Add($"{a.Address} is already live on '{live.Name}'; applying this profile would put the same address on two adapters.");
            // The same address in another saved profile: fine for "the same lab, two adapters", worth a note otherwise.
            var twin = others.FirstOrDefault(o => o.Id != p.Id && !o.Dhcp && o.Addresses.Any(x => x.Address == a.Address));
            if (twin is not null) w.Add($"{a.Address} is also in profile '{twin.Name}'.");
        }
        if (p.Gateway is not null && p.Addresses.Any(a => a.Address == p.Gateway)) w.Add($"Gateway {p.Gateway} is one of the profile's own addresses.");
        return w;
    }
}
