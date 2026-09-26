using System.Globalization;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Trade;

/// <summary>
/// A trade-site whisper, parsed. <see cref="Currency"/> is canonicalised to the poe.ninja display name
/// (so it can be looked up in the PriceBook); <see cref="Amount"/> is 0 / <see cref="Currency"/> "" for
/// unpriced listings. <see cref="Note"/> is whatever the sender typed after the generated text.
/// </summary>
public sealed partial record TradeRequest(string Item, double Amount, string Currency, string League,
    string? StashTab = null, int? Left = null, int? Top = null, string Note = "", bool Bulk = false)
{
    private const string Num = @"\d+(?:[.,]\d+)?|[.,]\d+";

    private const string Head = @"^Hi, I would like to buy your (?<item>.+?)";
    private const string Price = @" listed for (?<amt>" + Num + @") (?<cur>.+?)";
    private const string Stash = @" in (?<league>.+?) \(stash tab ""(?<tab>.*?)""; position: left (?<l>\d+), top (?<t>\d+)\)(?<rest>.*)$";
    // Without a stash part the league runs to the first '.'/'(' or end of line; anything after is the note.
    private const string Bare = @" in (?<league>[^.(]+?)\s*(?:[.(]|$)(?<rest>.*)$";

    // Tried priced-first so an item name containing " in " can't swallow the price into the league.
    [GeneratedRegex(Head + Price + Stash)] private static partial Regex PricedStashRx();
    [GeneratedRegex(Head + Price + Bare)] private static partial Regex PricedBareRx();
    [GeneratedRegex(Head + Stash)] private static partial Regex UnpricedStashRx();
    [GeneratedRegex(Head + Bare)] private static partial Regex UnpricedBareRx();
    // Bulk / currency exchange.
    [GeneratedRegex(@"^Hi, I'd like to buy your (?<n>" + Num + @") (?<want>.+?) for my (?<m>" + Num + @") (?<have>.+?) in (?<league>[^.]+?)\s*(?:\.|$)(?<rest>.*)$")]
    private static partial Regex BulkRx();

    /// <summary>Null for anything that isn't a trade-site whisper.</summary>
    public static TradeRequest? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Trim();

        foreach (var rx in (Regex[])[PricedStashRx(), PricedBareRx(), UnpricedStashRx(), UnpricedBareRx()])
        {
            var im = rx.Match(text);
            if (!im.Success) continue;
            var tab = im.Groups["tab"];
            return tab.Success
                ? FromItemMatch(im, tab.Value, int.Parse(im.Groups["l"].Value, CultureInfo.InvariantCulture),
                    int.Parse(im.Groups["t"].Value, CultureInfo.InvariantCulture))
                : FromItemMatch(im, null, null, null);
        }

        var m = BulkRx().Match(text);
        if (!m.Success) return null;
        return new TradeRequest($"{m.Groups["n"].Value} {m.Groups["want"].Value.Trim()}", ParseAmount(m.Groups["m"].Value),
            NormalizeCurrency(m.Groups["have"].Value), m.Groups["league"].Value.Trim(),
            Note: m.Groups["rest"].Value.Trim(), Bulk: true);
    }

    private static TradeRequest FromItemMatch(Match m, string? tab, int? left, int? top) => new(
        m.Groups["item"].Value.Trim(),
        m.Groups["amt"].Success ? ParseAmount(m.Groups["amt"].Value) : 0,
        m.Groups["cur"].Success ? NormalizeCurrency(m.Groups["cur"].Value) : "",
        m.Groups["league"].Value.Trim(), tab, left, top, m.Groups["rest"].Value.Trim());

    private static double ParseAmount(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Trade-site short ids and common spoken names → poe.ninja display names. Unknown names pass through.
    private static readonly Dictionary<string, string> Currencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["divine"] = "Divine Orb", ["divines"] = "Divine Orb", ["div"] = "Divine Orb", ["divine orb"] = "Divine Orb", ["divine orbs"] = "Divine Orb",
        ["exalted"] = "Exalted Orb", ["exalt"] = "Exalted Orb", ["exalts"] = "Exalted Orb", ["ex"] = "Exalted Orb", ["exa"] = "Exalted Orb",
        ["exalted orb"] = "Exalted Orb", ["exalted orbs"] = "Exalted Orb",
        ["greater-exalted"] = "Greater Exalted Orb", ["perfect-exalted"] = "Perfect Exalted Orb",
        ["chaos"] = "Chaos Orb", ["c"] = "Chaos Orb", ["chaos orb"] = "Chaos Orb", ["chaos orbs"] = "Chaos Orb",
        ["greater-chaos"] = "Greater Chaos Orb", ["perfect-chaos"] = "Perfect Chaos Orb",
        ["alch"] = "Orb of Alchemy", ["alchemy"] = "Orb of Alchemy", ["orb of alchemy"] = "Orb of Alchemy",
        ["regal"] = "Regal Orb", ["regal orb"] = "Regal Orb",
        ["vaal"] = "Vaal Orb", ["vaal orb"] = "Vaal Orb",
        ["annul"] = "Orb of Annulment", ["annulment"] = "Orb of Annulment", ["orb of annulment"] = "Orb of Annulment",
        ["mirror"] = "Mirror of Kalandra", ["mirror of kalandra"] = "Mirror of Kalandra",
        ["gcp"] = "Gemcutter's Prism", ["gemcutter's prism"] = "Gemcutter's Prism",
        ["bauble"] = "Glassblower's Bauble", ["glassblower's bauble"] = "Glassblower's Bauble",
        ["aug"] = "Orb of Augmentation", ["orb of augmentation"] = "Orb of Augmentation",
        ["transmute"] = "Orb of Transmutation", ["orb of transmutation"] = "Orb of Transmutation",
        ["chance"] = "Orb of Chance", ["orb of chance"] = "Orb of Chance",
        ["wisdom"] = "Scroll of Wisdom", ["scroll of wisdom"] = "Scroll of Wisdom",
        ["artificers"] = "Artificer's Orb", ["artificer's orb"] = "Artificer's Orb",
        ["fracturing-orb"] = "Fracturing Orb", ["fracturing orb"] = "Fracturing Orb",
        ["chromatic"] = "Chromatic Orb", ["chrome"] = "Chromatic Orb",
    };

    public static string NormalizeCurrency(string? raw)
    {
        var s = (raw ?? "").Trim();
        return Currencies.TryGetValue(s, out var name) ? name : s;
    }
}
