using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>
/// The price-check hotkey's decisions, kept pure for tests: what to search the trade site for (by item kind),
/// the poe.ninja reference price, and the "what should I list it at" verdict comparing the two.
/// </summary>
public static class PriceCheck
{
    /// <summary>
    /// The search for a hovered item. Rare/magic: a ladder from closest to loosest, stopping at the first step with
    /// listings — (1) every matched mod within ±20% of your roll, same base; (2) most of your mods at ≥70% of your
    /// rolls, same base; (3) the same with any base of that slot (ring, helmet, bow…); (4) all items of that base
    /// and rarity. Unique: that unique by name. Anything else (currency, runes, gems, normal bases): the base type.
    /// Returns the cache key, whether the stat catalogue is needed, and the lazy plan.
    /// </summary>
    public static (string Key, bool NeedsStats, Func<IReadOnlyList<TradeComparison.TradeStat>?, QueryPlan> Plan) For(
        ItemTradeProfile item, string? uniqueName, string? metadata = null)
    {
        var baseType = item.Name;
        if (item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic && item.Identified && item.Complete && item.Mods.Count > 0)
        {
            var key = "mods\n" + metadata + "\n" + JsonSerializer.Serialize(item);
            return (key, true, stats => RareLadder(item, stats, Category(metadata)));
        }
        if (item.Rarity == Poe2Live.Rarity.Unique && !string.IsNullOrWhiteSpace(uniqueName))
            return ($"unique\n{uniqueName}\n{baseType}", false,
                _ => new QueryPlan(BaseQuery(baseType, uniqueName, item.Rarity), "Same unique · rolls not compared"));
        var note = item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic
            ? (item.Identified ? "Affixes unreadable — base type only" : "Unidentified — base type only")
            : "Same base type";
        return ($"base\n{baseType}\n{item.Rarity}", false, _ => new QueryPlan(BaseQuery(baseType, null, item.Rarity), note));
    }

    private static QueryPlan RareLadder(ItemTradeProfile item, IReadOnlyList<TradeComparison.TradeStat>? stats, (string Id, string Name)? category)
    {
        var baseType = item.Name;
        var rarity = Rarity(item.Rarity);
        var baseOnly = new QueryPlan(BaseQuery(baseType, null, item.Rarity), $"all {rarity} {baseType} — mods not compared");
        if (stats is null) return baseOnly;
        var matched = TradeComparison.MatchStats(item, stats, out var ignored);
        var n = matched.Count;
        if (n == 0) return baseOnly with { Note = $"none of the mods could be matched — all {rarity} {baseType}" };

        var most = Math.Max(1, (int)Math.Ceiling(n * 0.6));
        var half = Math.Max(1, (int)Math.Ceiling(n * 0.5));
        QueryPlan? slotLoose = null, slotClose = null;
        if (category is { } cat)
        {
            var slotName = cat.Name.ToLowerInvariant();
            slotLoose = new QueryPlan(StatQuery(null, cat.Id, item.Rarity, matched, "count", half, 0.6, null),
                $"any {slotName} with {half}+ of your {n} mods", baseOnly);
            slotClose = new QueryPlan(StatQuery(null, cat.Id, item.Rarity, matched, "count", most, 0.7, null),
                $"any {slotName} with {most}+ of your {n} mods at about your rolls", slotLoose);
        }
        var similar = new QueryPlan(StatQuery(baseType, null, item.Rarity, matched, "count", most, 0.7, null),
            $"{baseType} with {most}+ of your {n} mods at about your rolls", slotClose ?? baseOnly);
        var unmatched = ignored > 0 ? $" ({ignored} mod{(ignored == 1 ? "" : "s")} not searchable)" : "";
        return new QueryPlan(StatQuery(baseType, null, item.Rarity, matched, "and", 0, 0.8, 1.2),
            $"all {n} matched mods within ±20%{unmatched}", similar);
    }

    /// <summary>Trade query with stat filters: <paramref name="group"/> "and" (all) or "count" (at least
    /// <paramref name="min"/>), each numeric filter from <paramref name="lo"/>× to <paramref name="hi"/>× your roll.</summary>
    private static string StatQuery(string? baseType, string? category, Poe2Live.Rarity rarity, IReadOnlyList<MatchedStat> matched,
        string group, int min, double lo, double? hi)
    {
        var filters = matched.Select(m => m.Value is { } v
            ? (object)new { id = m.Id, value = hi is { } h ? new { min = Math.Floor(v * lo), max = (double?)Math.Ceiling(v * h) } : new { min = Math.Floor(v * lo), max = (double?)null } }
            : new { id = m.Id }).ToList();
        var query = new Dictionary<string, object> { ["status"] = new { option = "online" } };
        if (baseType is not null) query["type"] = baseType;
        query["stats"] = new[] { group == "count" ? (object)new { type = "count", value = new { min }, filters } : new { type = "and", filters } };
        query["filters"] = Filters(rarity, category);
        return JsonSerializer.Serialize(new { query, sort = new { price = "asc" } }, IgnoreNulls);
    }

    private static readonly JsonSerializerOptions IgnoreNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static object Filters(Poe2Live.Rarity rarity, string? category)
    {
        var rarityName = rarity switch
        {
            Poe2Live.Rarity.Unique => "unique", Poe2Live.Rarity.Rare => "rare",
            Poe2Live.Rarity.Magic => "magic", _ => null,
        };
        return new { type_filters = new { filters = new
        {
            rarity = rarityName is null ? null : new { option = rarityName },
            category = category is null ? null : new { option = category },
        } } };
    }

    /// <summary>The trade site's item category for an item's metadata path (e.g. Metadata/Items/Rings/… →
    /// accessory.ring), or null when unknown. Ids from the official /api/trade2/data/filters list.</summary>
    public static (string Id, string Name)? Category(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        var m = metadata;
        (string, string)? C(string id, string name) => (id, name);
        if (m.Contains("/Rings/")) return C("accessory.ring", "Ring");
        if (m.Contains("/Amulets/")) return C("accessory.amulet", "Amulet");
        if (m.Contains("/Belts/")) return C("accessory.belt", "Belt");
        if (m.Contains("/Helmets/")) return C("armour.helmet", "Helmet");
        if (m.Contains("/BodyArmours/")) return C("armour.chest", "Body armour");
        if (m.Contains("/Gloves/")) return C("armour.gloves", "Gloves");
        if (m.Contains("/Boots/")) return C("armour.boots", "Boots");
        if (m.Contains("/Quivers/")) return C("armour.quiver", "Quiver");
        if (m.Contains("/Bucklers/")) return C("armour.buckler", "Buckler");
        if (m.Contains("/Shields/")) return C("armour.shield", "Shield");
        if (m.Contains("/Focus")) return C("armour.focus", "Focus");
        if (m.Contains("/Jewels/")) return C("jewel", "Jewel");
        if (m.Contains("/Charms/") || m.Contains("Charm")) return C("flask.charm", "Charm");
        if (m.Contains("/Flasks/")) return m.Contains("Mana") ? C("flask.mana", "Mana flask") : C("flask.life", "Life flask");
        if (m.Contains("/Crossbows/")) return C("weapon.crossbow", "Crossbow");
        if (m.Contains("/Bows/")) return C("weapon.bow", "Bow");
        if (m.Contains("/Wands/")) return C("weapon.wand", "Wand");
        if (m.Contains("/Sceptres/")) return C("weapon.sceptre", "Sceptre");
        if (m.Contains("/Warstaves/") || m.Contains("/Quarterstaves/")) return C("weapon.warstaff", "Quarterstaff");
        if (m.Contains("/Staves/")) return C("weapon.staff", "Staff");
        if (m.Contains("/Spears/")) return C("weapon.spear", "Spear");
        if (m.Contains("/Flails/")) return C("weapon.flail", "Flail");
        if (m.Contains("/Claws/")) return C("weapon.claw", "Claw");
        if (m.Contains("/Daggers/")) return C("weapon.dagger", "Dagger");
        if (m.Contains("/Talismans/")) return C("weapon.talisman", "Talisman");
        if (m.Contains("/TwoHandSwords/")) return C("weapon.twosword", "Two-handed sword");
        if (m.Contains("/OneHandSwords/")) return C("weapon.onesword", "One-handed sword");
        if (m.Contains("/TwoHandAxes/")) return C("weapon.twoaxe", "Two-handed axe");
        if (m.Contains("/OneHandAxes/")) return C("weapon.oneaxe", "One-handed axe");
        if (m.Contains("/TwoHandMaces/")) return C("weapon.twomace", "Two-handed mace");
        if (m.Contains("/OneHandMaces/")) return C("weapon.onemace", "One-handed mace");
        if (m.Contains("/Maps/") || m.Contains("MapKey")) return C("map.waystone", "Waystone");
        return null;
    }

    /// <summary>Trade query JSON for a base type (+ optional unique name / rarity), cheapest first.</summary>
    public static string? BaseQuery(string? baseType, string? uniqueName, Poe2Live.Rarity rarity)
    {
        if (string.IsNullOrWhiteSpace(baseType)) return null;
        var query = new Dictionary<string, object>
        {
            ["status"] = new { option = "online" },
            ["type"] = baseType,
        };
        if (!string.IsNullOrWhiteSpace(uniqueName)) query["name"] = uniqueName;
        var rarityName = rarity switch
        {
            Poe2Live.Rarity.Unique => "unique", Poe2Live.Rarity.Rare => "rare",
            Poe2Live.Rarity.Magic => "magic", _ => null,
        };
        if (rarityName != null) query["filters"] = new { type_filters = new { filters = new { rarity = new { option = rarityName } } } };
        return JsonSerializer.Serialize(new { query, sort = new { price = "asc" } });
    }

    private static string Rarity(Poe2Live.Rarity r) => r switch
    {
        Poe2Live.Rarity.Rare => "rare", Poe2Live.Rarity.Magic => "magic", Poe2Live.Rarity.Unique => "unique", _ => "",
    };

    /// <summary>poe.ninja reference for the item (uniques by art, everything else by base name). Rares/magics
    /// have no meaningful reference price — their value is in the rolls.</summary>
    public static PriceResult? Estimate(Poe2Live.HoveredItem item, PriceBook.Snapshot snapshot)
    {
        if (item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic) return null;
        if (item.Rarity == Poe2Live.Rarity.Unique)
            return item.Art is { } art && snapshot.ByArt.TryGetValue(art, out var byArt) ? byArt : null;
        return item.Name is { } key && snapshot.ByName.TryGetValue(key, out var byName) ? byName : null;
    }

    public sealed record Summary(double? Min, double? Median, double? Max, IReadOnlyList<double> Points, string Verdict);

    /// <summary>
    /// Compare the listings (per unit, exalted) with the poe.ninja estimate and phrase a pricing suggestion.
    /// The cheapest ask is what a buyer pays today, so the suggestion sits just under it; with no usable
    /// listings it falls back to the estimate.
    /// </summary>
    /// <param name="similar">The listings are similar items, not the same item (rares matched by their closest mods):
    /// the suggestion is then the median of those items, since the cheapest "similar" ask is usually an outlier.</param>
    public static Summary Summarize(IReadOnlyList<MarketListing> listings, double? estimateEx, Func<double, string> format, bool similar = false)
    {
        var points = listings.Where(l => l.Exalted is > 0).Select(l => l.Exalted!.Value).OrderBy(v => v).ToList();
        if (points.Count == 0)
            return new(null, null, null, points, estimateEx is { } e
                ? $"No priced listings online — poe.ninja values it at {format(e)}"
                : "No market data for this item yet");
        var min = points[0];
        var max = points[^1];
        var median = points.Count % 2 == 1 ? points[points.Count / 2] : (points[points.Count / 2 - 1] + points[points.Count / 2]) / 2;
        string verdict;
        if (similar && estimateEx is null && points.Count >= 2)
            verdict = $"{format(min)}–{format(max)} for similar items · list at ~{format(median)}";
        else if (estimateEx is { } est && est > 0)
        {
            var diff = (min - est) / est * 100.0;
            var cmp = Math.Abs(diff) < 5 ? "in line with" : diff < 0 ? $"{-diff:0}% below" : $"{diff:0}% above";
            verdict = $"Cheapest ask is {cmp} poe.ninja · list at ~{format(Undercut(min))} to sell fast";
        }
        else verdict = points.Count < 3
            ? $"Only {points.Count} comparable listing{(points.Count == 1 ? "" : "s")} — ~{format(min)} is a rough guide"
            : $"List at ~{format(Undercut(min))} to sell fast, ~{format(median)} to wait";
        return new(min, median, max, points, verdict);
    }

    /// <summary>The panel's one-word verdict on what the item is worth in total (<paramref name="worthEx"/>, exalted):
    /// "Jackpot" from 5 divine up, "List it" from 1 exalted, "Vendor" below that, "Unknown" with no price at all
    /// ("Reading" while the first search is still running).</summary>
    public static string Tier(double? worthEx, double exPerDivine, bool loading)
    {
        if (worthEx is not { } w || w <= 0) return loading ? "Reading" : "Unknown";
        var jackpot = exPerDivine > 1 ? exPerDivine * 5 : 1000;
        return w >= jackpot ? "Jackpot" : w >= 1 ? "List it" : "Vendor";
    }

    /// <summary>A price just under the cheapest ask (5%), so the listing is first in the buyer's results.</summary>
    public static double Undercut(double cheapest) => cheapest * 0.95;
}
