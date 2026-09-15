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
}
