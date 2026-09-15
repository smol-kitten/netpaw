namespace NetPaw.Model;

/// <summary>A secondary address NetPaw added on the fly (reach mode / presets) so it can be removed again.</summary>
public sealed record TempAddress(string Adapter, IpAddr Address, string Reason, DateTimeOffset AddedAt);
