namespace NetPaw.Model;

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
    public Connectivity.RepairSettings Repair { get; set; } = new();

    /// <summary>Random id so the telemetry build can count installs without any machine identity. Created on first use.</summary>
    public string? InstallId { get; set; }
    /// <summary>Profiles whose first automatic switch the user approved / declined on this machine (covers managed profiles too).</summary>
    public List<string> AutoSwitchConfirmedIds { get; set; } = [];
    public List<string> AutoSwitchDeclinedIds { get; set; } = [];
    /// <summary>Runtime switch for the telemetry build (the default build ignores it — there is nothing to switch).</summary>
    public bool TelemetryEnabled { get; set; } = true;
    public bool TelemetryNoticeShown { get; set; }
    /// <summary>Ask GitHub for the latest release once a day and say so once per version. Never downloads. Policy AllowUpdateCheck=0 wins.</summary>
    public bool UpdateCheck { get; set; } = true;
    public DateTimeOffset? UpdateLastCheck { get; set; }
    public string? UpdateLastVersionSeen { get; set; }
    /// <summary>Telemetry build only: OTLP/HTTP and syslog export to servers the user configures.</summary>
    public Telemetry.LogExportSettings LogExport { get; set; } = new();

    public const string CommunityRepo = "https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json|ec6ef08c:MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkWwClD0ojzS8c3otMcxZ6nzXZiF0QxFvTgpWMT3zmQDywGACDuUqj21aiLzpjfgJBchHNttrU637vWycRKMMnA==";
}
