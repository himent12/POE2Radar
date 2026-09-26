using POE2Radar.Overlay.Input;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// Chat macros, website bookmarks and item-inspect hotkeys (PoE-Overlay-style). Hotkeys are strings parsed by
/// <see cref="Hotkey.TryParse"/>. Defaults avoid the overlay's own keys (see <see cref="IsReserved"/>) and
/// follow PoE Overlay's convention of F5 = /hideout.
/// </summary>
public sealed class CommandSettings
{
    public bool Enabled { get; set; } = true;

    public List<ChatCommand> Commands { get; set; } =
    [
        new() { Enabled = true, Name = "Hideout", Hotkey = "F5", Text = "/hideout" },
        new() { Enabled = false, Name = "Kingsmarch", Hotkey = "", Text = "/kingsmarch" },
        new() { Enabled = false, Name = "Thank last", Hotkey = "", Text = "@last ty, gl!" },
        new() { Enabled = false, Name = "Exit to char select", Hotkey = "", Text = "/exit" },
    ];

    public List<Bookmark> Bookmarks { get; set; } =
    [
        new() { Name = "Trade", Url = "https://www.pathofexile.com/trade2/search/poe2/{league}" },
        new() { Name = "poe.ninja PoE2", Url = "https://poe.ninja/poe2/economy" },
        new() { Name = "PoE2DB", Url = "https://poe2db.tw/us/" },
        new() { Name = "PoE2 Wiki", Url = "https://www.poe2wiki.net/" },
    ];

    /// <summary>Open the hovered item on the PoE2 wiki.</summary>
    public string InspectWikiHotkey { get; set; } = "Alt+W";

    /// <summary>Open the hovered item on poe2db.</summary>
    public string InspectDbHotkey { get; set; } = "Alt+G";

    /// <summary>
    /// True if the overlay's own handlers would also react to <paramref name="hk"/>. Those poll the bare key
    /// and ignore extra modifiers, so F6–F10 (routes, flask, quit, atlas), F12 (dashboard) and Insert (menu)
    /// are taken in EVERY modifier combination, and Ctrl+D (trade search) with any extra modifier. The buff
    /// keeper's own toggle is user-configurable, so conflicts with it are checked where both are known.
    /// </summary>
    public static bool IsReserved(Hotkey hk)
        => hk.IsBound && (hk.Vk is >= 0x75 and <= 0x79 or 0x7B or 0x2D || hk.Vk == 'D' && hk.Ctrl);
}
