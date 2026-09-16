namespace NetPaw.Model;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum Visibility { Never, OnIssue, Always }

/// <summary>
/// What the info card shows: one mode per row kind (never / on issue / always) and when the card pins itself.
/// Error-level advice ignores the mode — it is the one thing an admin must not be able to hide.
/// </summary>
public sealed class InfoCardSettings
{
    public Dictionary<string, Visibility> Rows { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Visibility AutoPin { get; set; } = Visibility.OnIssue;

    /// <summary>Row kinds in card order; the defaults keep the pre-v0.8 rows visible and quiet the newer ones.</summary>
    public static readonly IReadOnlyList<(string Kind, Visibility Default, string IssueWhen)> Catalog =
    [
        ("Link", Visibility.Always, "adapter down"),
        ("Wi-Fi", Visibility.OnIssue, "signal under 30 %"),
        ("802.1X", Visibility.OnIssue, "authentication failed or in progress"),
        ("Path MTU", Visibility.OnIssue, "below 1500"),
        ("Address", Visibility.Always, "no real address"),
        ("Gateway", Visibility.Always, "not set or no reply"),
        ("DNS", Visibility.Always, "none, or a server does not answer"),
        ("Intranet", Visibility.OnIssue, "an extra target fails"),
        ("Internet", Visibility.Always, "no target answers"),
        ("Checks", Visibility.OnIssue, "never (it is the 'checks are off' notice)"),
        ("Temporary", Visibility.Always, "temporary addresses exist"),
        ("VPN", Visibility.OnIssue, "a tunnel is down"),
        ("Also", Visibility.OnIssue, "another adapter has a problem or advice"),
        ("Switch", Visibility.OnIssue, "never (information only)"),
        ("Advice", Visibility.OnIssue, "warning or error; errors always show"),
    ];
    public static IEnumerable<string> Kinds => Catalog.Select(c => c.Kind);

    public Visibility Mode(string kind) => Rows.TryGetValue(kind, out var v) ? v : Catalog.FirstOrDefault(c => c.Kind == kind).Default;

    /// <summary>true = render this row now. <paramref name="issue"/> is the row's own verdict (see the catalog).</summary>
    public bool Show(string kind, bool issue) => Mode(kind) switch { Visibility.Always => true, Visibility.OnIssue => issue, _ => false };

    /// <summary>Advice rows: errors ignore the mode; warnings count as an issue; info rows only with Always.</summary>
    public bool ShowAdvice(Connectivity.AdvisorySeverity severity) =>
        severity == Connectivity.AdvisorySeverity.Error || Show("Advice", severity == Connectivity.AdvisorySeverity.Warning);

    public bool ShouldPin(bool problem) => AutoPin == Visibility.Always || (AutoPin == Visibility.OnIssue && problem);

    public void ResetToDefaults() { Rows.Clear(); AutoPin = Visibility.OnIssue; }
}

public sealed class Settings
{
    /// <summary>Adapter the tray/CLI act on when a profile does not name one. Null = auto-detect.</summary>
    public string? WorkAdapter { get; set; }
    public string PanelHotkey { get; set; } = "Ctrl+Alt+N";
    public bool ConfirmBeforeApply { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Prefix assumed for reach-mode targets that match no profile/preset.</summary>
    public int ReachDefaultPrefix { get; set; } = 24;
    /// <summary>Reach mode adds a secondary address (keeps connectivity) instead of replacing the primary.</summary>
    public bool ReachAsSecondary { get; set; } = true;
    /// <summary>Run ipconfig /flushdns after every apply so the last network's cached names do not linger.</summary>
    public bool FlushDns { get; set; } = true;
    /// <summary>User-added profile repositories, "url" or "url|keyId:base64PublicKey". Policy repos are added on top.</summary>
    public List<string> RepoUrls { get; set; } = [CommunityRepo];

    /// <summary>The community pack in this repo, pinned to its signing key. Listed by default, fetched only on an explicit Sync (never at start-up).</summary>
    public string InfoHotkey { get; set; } = "Ctrl+Alt+I";
    public Connectivity.CheckSettings Checks { get; set; } = new();
    /// <summary>Null in files written before v0.10; <see cref="CardSettings"/> migrates once from <c>Checks.StickyAlerts</c>.</summary>
    public InfoCardSettings? InfoCard { get; set; }
    public InfoCardSettings CardSettings() => InfoCard ??= new InfoCardSettings { AutoPin = Checks.StickyAlerts ? Visibility.OnIssue : Visibility.Never };
    public Connectivity.RepairSettings Repair { get; set; } = new();

    /// <summary>Random id so the telemetry build can count installs without any machine identity. Created on first use.</summary>
    public string? InstallId { get; set; }
    /// <summary>Profiles whose first automatic switch the user approved / declined on this machine (covers managed profiles too).</summary>
    public List<string> AutoSwitchConfirmedIds { get; set; } = [];
    public List<string> AutoSwitchDeclinedIds { get; set; } = [];
    /// <summary>Runtime switch for the telemetry build (the default build ignores it — there is nothing to switch).</summary>
    public bool TelemetryEnabled { get; set; } = true;
    public bool TelemetryNoticeShown { get; set; }
    /// <summary>Ask GitHub for the latest release once a day and say so once per version. Never downloads. Off until the user opts in on first start; policy AllowUpdateCheck=0 wins.</summary>
    public bool UpdateCheck { get; set; }
    /// <summary>The first-start question was shown (or skipped because policy forbids the check). Asked once, never nagged.</summary>
    public bool UpdateCheckAsked { get; set; }
    public DateTimeOffset? UpdateLastCheck { get; set; }
    public string? UpdateLastVersionSeen { get; set; }
    /// <summary>Telemetry build only: OTLP/HTTP and syslog export to servers the user configures.</summary>
    public Telemetry.LogExportSettings LogExport { get; set; } = new();

    public const string CommunityRepo = "https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json|ec6ef08c:MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkWwClD0ojzS8c3otMcxZ6nzXZiF0QxFvTgpWMT3zmQDywGACDuUqj21aiLzpjfgJBchHNttrU637vWycRKMMnA==";
}
