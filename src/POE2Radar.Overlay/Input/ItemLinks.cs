using POE2Radar.Core.Game;
using POE2Radar.Overlay.Pricing;

namespace POE2Radar.Overlay.Input;

/// <summary>"Inspect" URLs for a hovered item: PoE2 wiki, poe2db, and a trade name search.</summary>
public static class ItemLinks
{
    /// <summary><c>https://www.poe2wiki.net/wiki/Name_With_Underscores</c>; null for a blank name.</summary>
    public static string? Wiki(string? name) => PageUrl("https://www.poe2wiki.net/wiki/", name);

    /// <summary><c>https://poe2db.tw/us/Name_With_Underscores</c>; null for a blank name.</summary>
    public static string? Poe2Db(string? name) => PageUrl("https://poe2db.tw/us/", name);

    /// <summary>
    /// Trade search for the item (online, cheapest first). Delegates to the hover-price builder so the
    /// overlay has exactly one trade-URL format: <c>type</c> = base, <c>name</c> only for uniques.
    /// </summary>
    public static string? TradeSearch(string? league, string? baseType, string? uniqueName, Poe2Live.Rarity rarity)
        => HoverValuation.TradeSearch(league ?? "", baseType,
            rarity == Poe2Live.Rarity.Unique ? uniqueName : null, rarity);

    /// <summary>
    /// MediaWiki-style page slug: spaces → underscores, each segment percent-encoded. Apostrophes and commas
    /// (e.g. "Kaom's Heart", "Atziri's Disdain, …") are unreserved-safe in a path and kept literal so the
    /// wiki doesn't redirect; '/', '?', '#', '&amp;' and the rest are escaped so a name can't alter the URL.
    /// </summary>
    private static string? PageUrl(string root, string? name)
    {
        var n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (n.Length == 0) return null;
        var slug = Uri.EscapeDataString(n.Replace(' ', '_')).Replace("%27", "'").Replace("%2C", ",");
        return root + slug;
    }
}
