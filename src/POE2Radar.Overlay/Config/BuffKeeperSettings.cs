using POE2Radar.Overlay.Input;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// Buff keeper: recast self-buffs when they expire or go missing (see <see cref="BuffKeeper"/>).
/// <see cref="Enabled"/> is the persisted ARMED state — toggled only by <see cref="ToggleHotkey"/>, never
/// over HTTP. Rules are evaluated in list order (first = highest priority); at most one key is pressed per
/// tick and any two presses are at least <see cref="GlobalGapMs"/> apart.
/// </summary>
public sealed class BuffKeeperSettings
{
    // Armed by default (like auto-flask); rules still only fire once ticked with a buff name and key. The toggle
    // hotkey is the kill-switch, and every toggle is announced on screen so it's never flipped silently.
    public bool Enabled { get; set; } = true;
    public string ToggleHotkey { get; set; } = "F4";
    public List<BuffRule> Rules { get; set; } = new()
    {
        new BuffRule { Enabled = false, Name = "Example buff", Key = 0x54, Trigger = BuffKeeper.TriggerMissing, BuffName = "" },
    };
    public int GlobalGapMs { get; set; } = 250;
}
