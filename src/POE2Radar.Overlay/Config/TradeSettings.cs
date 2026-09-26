namespace POE2Radar.Overlay.Config;

/// <summary>
/// Trade assistant (Client.txt-driven trade panel + tracker). Plain properties with defaults so it
/// round-trips through System.Text.Json and older config files pick up the defaults.
/// </summary>
public sealed class TradeSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Explicit Client.txt (or game folder) path; empty = auto-locate from the process / install paths.</summary>
    public string ClientLogPath { get; set; } = "";
    public string ThanksMessage { get; set; } = "ty, gl!";
    public string BusyMessage { get; set; } = "busy right now, will invite soon";
    public string SoldMessage { get; set; } = "sorry, already sold";
    public string StillInterestedMessage { get; set; } = "still interested?";
    /// <summary>Sessions with no activity for this long drop off the panel.</summary>
    public int ExpireMinutes { get; set; } = 30;
    public int MaxSessions { get; set; } = 12;
    public bool ShowPanel { get; set; } = true;
    // Panel anchor as a fraction of the game window, so it survives resolution changes.
    public float PanelX { get; set; } = 0.40f;
    public float PanelY { get; set; } = 0.08f;
    public bool TrackHistory { get; set; } = true;
    /// <summary>"Thanks" on a completed incoming trade also kicks the buyer from the party.</summary>
    public bool KickAfterTrade { get; set; }
}
