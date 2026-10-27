using System.Text.Json;

namespace NetPaw.Ipc;

/// <summary>The gated units policy tiers apply to. Most map 1:1 to <see cref="RequestKind"/>; VirtualUnlock is the
/// extra gate for targeting a non-host-facing adapter (not a request kind of its own).</summary>
public enum PolicyIntent { Status, ApplyProfile, Dhcp, Reach, Renew, ClearTemp, DeleteRoute, ResetAdapter, Release, Prefer, SetMtu, VirtualUnlock, RevertLastApply }

/// <summary>One tier decision, for the audit line and the caller.</summary>
public sealed record PolicyDecision(PolicyIntent Intent, Tier Required, Tier Caller, bool Allowed, string Via);

/// <summary>
/// Reads per-intent tier overrides from machine policy (Windows: HKLM\Software\Policies\NetPaw). Returns
/// Valid=false ONLY when a policy is present but cannot be read or parsed (access denied, a bad tier value); an
/// absent policy is Valid=true with no overrides — not configuring policy is not an error.
/// </summary>
public interface ITierPolicySource
{
    /// <summary>Reason: why the policy is invalid, for the log (e.g. "PolicySchemaVersion 2 unsupported, this build: 1"); null when valid.</summary>
    (IReadOnlyDictionary<PolicyIntent, Tier> Overrides, bool Valid, string? Reason) Read();
}

/// <summary>No machine policy configured: valid, no overrides, so the default table (= today) applies. The
/// default when the service wires no real source, and in tests.</summary>
public sealed class EmptyPolicySource : ITierPolicySource
{
    /// <summary>No overrides, always valid: the defaults apply.</summary>
    public (IReadOnlyDictionary<PolicyIntent, Tier>, bool, string?) Read() => (new Dictionary<PolicyIntent, Tier>(), true, null);
}

/// <summary>
/// PR 4: the service's one authorization point. Each intent has a default minimum tier equal to pre-PR4 behaviour
/// and a hard floor a policy may never loosen below. A machine policy may tighten or loosen each intent within
/// [floor, Admin]. On an unreadable/invalid policy the last-known-good (the last policy that parsed, cached to a
/// SYSTEM/Admins-only file) is used so a tightened override is not silently lost; with no cache, the defaults.
/// Never denies everything. Every decision writes one audit line.
/// </summary>
public sealed class PolicyTiers
{
    // Default table == today's behaviour: StandardUser for the everyday intents, NCO for the destructive ones.
    static readonly IReadOnlyDictionary<PolicyIntent, Tier> Defaults = new Dictionary<PolicyIntent, Tier>
    {
        [PolicyIntent.Status] = Tier.StandardUser,
        [PolicyIntent.ApplyProfile] = Tier.StandardUser,
        [PolicyIntent.Dhcp] = Tier.StandardUser,
        [PolicyIntent.Reach] = Tier.StandardUser,
        [PolicyIntent.Renew] = Tier.StandardUser,
        [PolicyIntent.ClearTemp] = Tier.NetworkConfigOperators,
        [PolicyIntent.DeleteRoute] = Tier.NetworkConfigOperators,
        [PolicyIntent.ResetAdapter] = Tier.NetworkConfigOperators,
        [PolicyIntent.Release] = Tier.NetworkConfigOperators,
        [PolicyIntent.Prefer] = Tier.NetworkConfigOperators,
        [PolicyIntent.SetMtu] = Tier.NetworkConfigOperators,
        [PolicyIntent.VirtualUnlock] = Tier.NetworkConfigOperators,
        [PolicyIntent.RevertLastApply] = Tier.StandardUser,   // G13: undo = re-apply the snapshotted pre-state; same class as ApplyProfile/Dhcp
    };

    // A policy may tighten above the floor but never loosen below it. Only the inline virtual-adapter unlock has a
    // floor above StandardUser (orchestrator m-5032 Q1: never below NCO).
    static readonly IReadOnlyDictionary<PolicyIntent, Tier> Floors = new Dictionary<PolicyIntent, Tier>
    {
        [PolicyIntent.VirtualUnlock] = Tier.NetworkConfigOperators,
    };

    /// <summary>The policy schema this build understands. An optional <c>PolicySchemaVersion</c> value in the
    /// policy key names the schema an admin wrote the policy for; a value this build does not know makes the
    /// whole policy INVALID (so last-known-good or the defaults apply) instead of silently ignoring tiers it
    /// cannot interpret (G9-lite, t-e38207). Absent = 1.</summary>
    public const int SchemaVersion = 1;

    /// <summary>True when the policy's schema marker is absent or names this build's schema.</summary>
    public static bool SchemaSupported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return int.TryParse(value.Trim(), out var v) && v == SchemaVersion;
    }

    /// <summary>The built-in minimum tier of an intent (equal to pre-0.14 behaviour).</summary>
    public static Tier DefaultFor(PolicyIntent i) => Defaults[i];
    /// <summary>The tier a policy may never go below for an intent (StandardUser unless a floor is set).</summary>
    public static Tier FloorFor(PolicyIntent i) => Floors.GetValueOrDefault(i, Tier.StandardUser);

    readonly ITierPolicySource _source;
    readonly string _cachePath;
    readonly Action<string> _audit;

    /// <summary>Creates the resolver with its policy source, the last-known-good cache path and the audit sink.</summary>
    public PolicyTiers(ITierPolicySource source, string cachePath, Action<string>? audit = null)
    {
        _source = source;
        _cachePath = cachePath;
        _audit = audit ?? (_ => { });
    }

    /// <summary>Resolve the active overrides: the policy when it reads clean (and refresh the cache), else the
    /// last-known-good cache, else none (= defaults). Read once per request by the handler.</summary>
    public IReadOnlyDictionary<PolicyIntent, Tier> Resolve()
    {
        var (overrides, valid, reason) = _source.Read();
        if (valid)
        {
            WriteCache(overrides);
            _lastReason = null;
            return overrides;
        }
        // The reason is logged once per change, not once per request (review of #98).
        if (reason != _lastReason) { _audit("policy invalid: " + (reason ?? "unreadable")); _lastReason = reason; }
        var cached = ReadCache();
        if (cached is not null)
        {
            _audit("policy unreadable or invalid; using the last-known-good cached policy");
            return cached;
        }
        _audit("policy unreadable or invalid and no cache; using the default tier table");
        return EmptyOverrides;
    }

    /// <summary>The effective minimum tier for an intent under these overrides: the override clamped to the floor,
    /// else the default.</summary>
    public static Tier Required(PolicyIntent intent, IReadOnlyDictionary<PolicyIntent, Tier> overrides)
    {
        var floor = FloorFor(intent);
        return overrides.TryGetValue(intent, out var o) ? (o < floor ? floor : o) : Defaults[intent];
    }

    /// <summary>Decide and audit. Allowed when the caller's tier is at least the required tier. <paramref name="who"/>
    /// is the caller identity for the audit line.</summary>
    public PolicyDecision Decide(PolicyIntent intent, Tier caller, IReadOnlyDictionary<PolicyIntent, Tier> overrides, string? who = null)
    {
        var required = Required(intent, overrides);
        var allowed = caller >= required;
        var via = overrides.ContainsKey(intent) ? "policy" : "default";
        _audit($"tier decision: user={who ?? "?"} tier={caller} intent={intent} required={required} via={via} -> {(allowed ? "allow" : "DENY")}");
        return new PolicyDecision(intent, required, caller, allowed, via);
    }

    /// <summary>Parse a tier from a policy/cache string: only an exact defined name (case-insensitive) is a tier.
    /// A number ("5"), a flags-style comma list ("StandardUser,Admin"), or any undefined value is rejected — an
    /// unreachable tier would deny an intent even to Admin and break the never-deny-all guarantee.</summary>
    public static bool TryParseTier(string? s, out Tier tier)
    {
        tier = default;
        if (string.IsNullOrWhiteSpace(s) || s.Contains(',') || s.Any(char.IsDigit)) return false;
        return Enum.TryParse(s, ignoreCase: true, out tier) && Enum.IsDefined(tier);
    }

    /// <summary>Parses a PolicyIntent by exact name; false for numbers or unknown names.</summary>
    static bool TryParseIntent(string? s, out PolicyIntent intent)
    {
        intent = default;
        if (string.IsNullOrWhiteSpace(s) || s.Contains(',') || s.Any(char.IsDigit)) return false;
        return Enum.TryParse(s, ignoreCase: false, out intent) && Enum.IsDefined(intent);
    }

    static readonly IReadOnlyDictionary<PolicyIntent, Tier> EmptyOverrides = new Dictionary<PolicyIntent, Tier>();
    string? _lastWritten;   // the JSON last persisted, so the cache is rewritten only when the policy changes
    string? _lastReason;    // the last invalid-policy reason logged, so it is logged once per change

    void WriteCache(IReadOnlyDictionary<PolicyIntent, Tier> overrides)
    {
        // Store names, not enum integers, so a reorder of the enum can never silently re-map a cached tier.
        var json = JsonSerializer.Serialize(overrides.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString()));
        if (json == _lastWritten) return;   // unchanged: no write per request
        try
        {
            File.WriteAllText(_cachePath, json);
            _lastWritten = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _audit("could not write the policy cache: " + ex.Message);
        }
    }

    /// <summary>The last-known-good overrides from the cache file; null when absent or unreadable.</summary>
    IReadOnlyDictionary<PolicyIntent, Tier>? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var model = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cachePath));
            if (model is null) return null;
            var o = new Dictionary<PolicyIntent, Tier>();
            foreach (var (k, v) in model)
                if (TryParseIntent(k, out var intent) && TryParseTier(v, out var tier))
                    o[intent] = tier;
            return o;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
