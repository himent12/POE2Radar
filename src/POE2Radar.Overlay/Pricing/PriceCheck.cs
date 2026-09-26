using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>
/// The price-check hotkey's decisions, kept pure for tests: what to search the trade site for (by item kind),
/// the poe.ninja reference price, and the "what should I list it at" verdict comparing the two.
/// </summary>
public static class PriceCheck
{
    /// <summary>Listing status searched: instant buyout only. Those asks are real — anyone can buy at that price — so
    /// they aren't padded by AFK or price-fixing "in person" listings (Exiled Exchange 2 and Sidekick default to it too).</summary>
    public const string Status = "securable";

    /// <summary>
    /// The search for a hovered item. Rare/magic equipment with a known base: the stats its price hinges on (see
    /// <see cref="ItemAppraiser"/>), searched as "at least about this good" and loosened step by step when too few
    /// listings match (see <see cref="DriverLadder"/>). Other rares/magics (flasks, charms, waystones…): every mod,
    /// closest rolls first (<see cref="RareLadder"/>). Unique: that unique by name. Anything else (currency, runes,
    /// gems, normal bases): the base type. Returns the cache key, whether the stat catalogue is needed, and the plan.
    /// </summary>
    public static (string Key, bool NeedsStats, Func<IReadOnlyList<TradeComparison.TradeStat>?, QueryPlan> Plan) For(
        ItemTradeProfile item, string? uniqueName, string? metadata = null, ItemAppraisal? appraisal = null)
    {
        var baseType = item.Name;
        if (item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic && item.Identified && item.Complete && item.Mods.Count > 0)
        {
            var key = "mods\n" + metadata + "\n" + JsonSerializer.Serialize(item);
            appraisal ??= ItemAppraiser.Appraise(item, metadata);
            var category = Category(metadata);
            return (key, true, stats => appraisal is not null
                ? DriverLadder(item, appraisal, stats, category)
                : RareLadder(item, stats, category));
        }
        if (item.Rarity == Poe2Live.Rarity.Unique && !string.IsNullOrWhiteSpace(uniqueName))
            return ($"unique\n{uniqueName}\n{baseType}", false,
                _ => new QueryPlan(BaseQuery(baseType, uniqueName, item.Rarity), "Same unique · rolls not compared"));
        var note = item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic
            ? (item.Identified ? "Affixes unreadable — base type only" : "Unidentified — base type only")
            : "Same base type";
        return ($"base\n{baseType}\n{item.Rarity}", false, _ => new QueryPlan(BaseQuery(baseType, null, item.Rarity), note));
    }

    /// <summary>One trade filter an appraisal driver turns into: a stat filter (<see cref="StatId"/>, trade-site id) or an
    /// equipment filter (<see cref="Equipment"/>: pdps, edps, dps, ar, ev, es), with the item's own value.</summary>
    public sealed record TradeCriterion(string Label, double Importance, string? StatId, string? Equipment, double? Value);

    /// <summary>Drivers too small to matter to a buyer aren't searched; six filters keep the query under the trade
    /// site's logged-out complexity cap (six stats + an equipment filter measured 21, accepted).</summary>
    private const double MinImportance = 0.3;
    private const int MaxCriteria = 6;

    /// <summary>
    /// The trade filters for an appraised item, most important first: pseudo totals for life (incl. Strength), elemental
    /// and chaos resistance and movement speed (the site sums every source, as buyers compare them); equipment filters for
    /// weapon DPS (the site's Q20 figure) and local defences; and each valuable affix matched to its trade stat.
    /// </summary>
    public static List<TradeCriterion> Criteria(ItemAppraisal appraisal, IReadOnlyList<TradeComparison.TradeStat>? stats)
    {
        var list = new List<TradeCriterion>();
        foreach (var d in appraisal.Drivers)
        {
            if (d.Importance < MinImportance || list.Count >= MaxCriteria) continue;
            TradeCriterion? c = d.Key switch
            {
                "pdps" or "edps" or "dps" or "ar" or "ev" or "es" => new(d.Label, d.Importance, null, d.Key, d.Value),
                "ms" => new(d.Label, d.Importance, "pseudo.pseudo_increased_movement_speed", null, d.Value),
                "life" => new(d.Label, d.Importance, "pseudo.pseudo_total_life", null, d.Value),
                "ele_res" => new(d.Label, d.Importance, "pseudo.pseudo_total_elemental_resistance", null, d.Value),
                "chaos_res" => new(d.Label, d.Importance, "pseudo.pseudo_total_chaos_resistance", null, d.Value),
                "mod" when stats is not null && d.Line is { } line && d.Affix is { } a
                    && TradeComparison.MatchLine("explicit", line, stats, TradeComparison.IsLocal(a.ModId)) is { } m
                    => new(d.Label, d.Importance, m.Id, null, m.Value),
                _ => null,
            };
            if (c is not null && !list.Any(x => x.StatId is not null && x.StatId == c.StatId)) list.Add(c);
        }
        return list;
    }

    /// <summary>
    /// Rare/magic ladder built on what the item is bought for, each step "at least about this good" (minimums 10% under
    /// the item's own values, no maximums — a better item is still a comparable), any base of its slot: (1) every
    /// criterion; (2) the most important ~60%; (3) the top two at 80% of your values; (4) all items of that base and
    /// rarity (flagged as not comparable). The first step with enough listings wins.
    /// </summary>
    public static QueryPlan DriverLadder(ItemTradeProfile item, ItemAppraisal appraisal, IReadOnlyList<TradeComparison.TradeStat>? stats,
        (string Id, string Name)? category)
    {
        var baseType = item.Name;
        var rarity = Rarity(item.Rarity);
        var baseOnly = new QueryPlan(BaseQuery(baseType, null, item.Rarity), $"all {rarity} {baseType} — nothing comparable listed",
            Comparable: false);
        var criteria = Criteria(appraisal, stats);
        if (criteria.Count == 0)
            return baseOnly with { Note = $"nothing on it buyers search for ({appraisal.GradeName}) — all {rarity} {baseType}" };

        var slot = category?.Name.ToLowerInvariant() ?? appraisal.SlotName;
        var type = category is null ? baseType : null;
        string Names(IEnumerable<TradeCriterion> cs) => string.Join(", ", cs.Select(c => c.Label));
        var n = criteria.Count;
        QueryPlan next = baseOnly;
        if (n >= 2)
        {
            var core = criteria.Take(2).ToList();
            next = new QueryPlan(CriteriaQuery(type, category?.Id, core, 0.8), $"any {slot} with about your {Names(core)}", next);
        }
        var most = (int)Math.Ceiling(n * 0.6);
        if (most > 2 && most < n)
        {
            var top = criteria.Take(most).ToList();
            next = new QueryPlan(CriteriaQuery(type, category?.Id, top, 0.9), $"any {slot} with at least your {Names(top)}", next);
        }
        return new QueryPlan(CriteriaQuery(type, category?.Id, criteria, 0.9), $"any {slot} with at least your {Names(criteria)}", next);
    }

    /// <summary>Trade query JSON: rarity non-unique, not mirrored/sanctified, one listing per seller, cheapest first; stat
    /// criteria in one "and" group and equipment criteria as equipment filters, each with a minimum of
    /// <paramref name="factor"/> × the item's value.</summary>
    private static string CriteriaQuery(string? baseType, string? category, IReadOnlyList<TradeCriterion> criteria, double factor)
    {
        object Min(TradeCriterion c) => new { min = Math.Floor(c.Value!.Value * factor) };
        var statFilters = criteria.Where(c => c.StatId is not null)
            .Select(c => c.Value is > 0 ? (object)new { id = c.StatId, value = Min(c) } : new { id = c.StatId }).ToList();
        var equipment = criteria.Where(c => c.Equipment is not null && c.Value is > 0).ToDictionary(c => c.Equipment!, Min);
        var query = new Dictionary<string, object> { ["status"] = new { option = Status } };
        if (baseType is not null) query["type"] = baseType;
        if (statFilters.Count > 0) query["stats"] = new[] { new { type = "and", filters = statFilters } };
        var filters = new Dictionary<string, object>
        {
            ["type_filters"] = new { filters = new { rarity = new { option = "nonunique" }, category = category is null ? null : new { option = category } } },
            ["misc_filters"] = new { filters = new { mirrored = new { option = "false" }, sanctified = new { option = "false" } } },
            ["trade_filters"] = new { filters = new { collapse = new { option = "true" } } },
        };
        if (equipment.Count > 0) filters["equipment_filters"] = new { filters = equipment };
        query["filters"] = filters;
        return JsonSerializer.Serialize(new { query, sort = new { price = "asc" } }, IgnoreNulls);
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
        var query = new Dictionary<string, object> { ["status"] = new { option = Status } };
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
            ["status"] = new { option = Status },
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

    /// <summary>Listing spread and the pricing suggestion. <see cref="Suggested"/> is the list price for similar-item
    /// (rare) searches — the cheapest genuine comparable — and null otherwise.</summary>
    public sealed record Summary(double? Min, double? Median, double? Max, IReadOnlyList<double> Points, string Verdict,
        double? Suggested = null);

    /// <summary>
    /// Compare the listings (per unit, exalted) with the poe.ninja estimate and phrase a pricing suggestion.
    /// The cheapest ask is what a buyer pays today, so the suggestion sits just under it; with no usable
    /// listings it falls back to the estimate.
    /// </summary>
    /// <param name="similar">The listings are comparable items, not the same item (rares searched as "at least about as
    /// good on the stats that matter"): a buyer takes the cheapest of them, so that — skipping a lone ask far below the
    /// rest — is what this item can be listed at.</param>
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
        double? suggested = null;
        if (similar && estimateEx is null && points.Count >= 2)
        {
            var anchor = RobustLow(points);
            suggested = anchor;
            verdict = anchor > min
                ? $"Comparable items from {format(anchor)} (one ask at {format(min)} looks like a mistake) · list at ~{format(anchor)}"
                : $"Comparable items from {format(anchor)}, typically {format(median)} · list at ~{format(anchor)}";
        }
        else if (estimateEx is { } est && est > 0)
        {
            var diff = (min - est) / est * 100.0;
            var cmp = Math.Abs(diff) < 5 ? "in line with" : diff < 0 ? $"{-diff:0}% below" : $"{diff:0}% above";
            verdict = $"Cheapest ask is {cmp} poe.ninja · list at ~{format(Undercut(min))} to sell fast";
        }
        else verdict = points.Count < 3
            ? $"Only {points.Count} comparable listing{(points.Count == 1 ? "" : "s")} — ~{format(min)} is a rough guide"
            : $"List at ~{format(Undercut(min))} to sell fast, ~{format(median)} to wait";
        // Price fixers list in currencies few buyers hold, so the cheapest ask converts to less than anyone pays.
        if (listings.Count >= 5 && listings.Count(l => l.Currency is "exalted" or "divine" or "chaos") * 2 < listings.Count)
            verdict += " · most asks are in odd currencies (possible price-fixing)";
        return new(min, median, max, points, verdict, suggested);
    }

    /// <summary>The cheapest genuine ask in ascending <paramref name="sorted"/> prices: the lowest, unless it's a lone
    /// listing under half the next one (a typo, bait, or a worse item that slipped through) and there are at least three
    /// listings to tell.</summary>
    public static double RobustLow(IReadOnlyList<double> sorted) =>
        sorted.Count >= 3 && sorted[0] < sorted[1] * 0.5 ? sorted[1] : sorted[0];

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
