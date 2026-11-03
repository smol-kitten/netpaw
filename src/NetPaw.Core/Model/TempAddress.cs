namespace NetPaw.Model;

/// <summary>A secondary address NetPaw added on the fly (reach mode / presets) so it can be removed again.</summary>
/// <param name="AdapterId">G5 (18b): the adapter's id when it was known, so a renamed adapter is still found; entries from 0.16 carry none.</param>
public sealed record TempAddress(string Adapter, IpAddr Address, string Reason, DateTimeOffset AddedAt, string? AdapterId = null)
{
    public bool IsOn(Adapters.AdapterInfo a) => AdapterId is { Length: > 0 } && a.Id is { Length: > 0 }
        ? string.Equals(AdapterId, a.Id, StringComparison.OrdinalIgnoreCase)
        : string.Equals(Adapter, a.Name, StringComparison.OrdinalIgnoreCase);
}
