namespace NetPaw;

/// <summary>Actions an organisation can deny by policy. The UI hides denied ones; the CLI exits 4.</summary>
public enum Capability { UserProfiles, UserRepos, Reach, TempAddresses, Dhcp, UnsignedRepos }

public sealed class DeniedByPolicyException(Capability what) : InvalidOperationException($"{what} is disabled by your organisation's policy.")
{
    public Capability What { get; } = what;
}
