using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>How much buyers of a slot care about a stat. Weights: Filler 0, Minor 0.35, Useful 1, Key 1.8, Premium 3.</summary>
public enum AffixRole { Filler, Minor, Useful, Key, Premium }

public enum AppraisalGrade { Vendor, Low, Decent, Good, Top }

/// <summary>Equipment slot, as far as affix value is concerned (attack and caster weapons value different stats).</summary>
public enum ItemSlot { Other, AttackWeapon, CasterWeapon, Sceptre, Quiver, Focus, Shield, Buckler, Helmet, BodyArmour, Gloves, Boots, Belt, Ring, Amulet, Jewel }

/// <summary>One affix, scored. <see cref="Quality"/> is its roll against the best that stat reaches on this base (1 = a
/// top-tier max roll); <see cref="Score"/> = role weight × quality². <see cref="CountedIn"/> names the aggregate
/// (weapon DPS, defences) its stats feed instead of being scored on their own.</summary>
public sealed record AppraisedAffix(string Kind, string ModId, string Text, int? Tier, int TierCount, bool? Prefix,
    double Quality, AffixRole Role, double Score, string? CountedIn);

/// <summary>Weapon damage per second at the item's rolls, without quality. <see cref="Quality"/> compares it with a
/// top-tier roll of the same base.</summary>
public sealed record WeaponDps(double Physical, double Elemental, double Chaos, double AttacksPerSecond, double Quality)
{
    public double Total => Physical + Elemental + Chaos;

    /// <summary>Physical DPS as the trade site shows it: every weapon normalised to 20% quality, which multiplies
    /// physical damage by 1.2 (verified against a live listing: 176–261 phys, 1.2 APS, no quality → 314.4 pDPS).</summary>
    public double PhysicalQ20 => Physical * 1.2;

    /// <summary>Trade-site total DPS (pDPS at Q20 + eDPS; like Exiled Exchange 2, chaos is left out).</summary>
    public double TradeTotal => PhysicalQ20 + Elemental;
}

/// <summary>Local defences at the item's rolls (quality not included) and how they compare with a top-tier roll.</summary>
public sealed record Defences(int Armour, int Evasion, int EnergyShield, double Quality);

/// <summary>What a price hinges on: an aggregate buyers filter on (weapon DPS, total life, total resistance, movement
/// speed, local defences) or one valuable affix. <see cref="Key"/>: "pdps", "edps", "dps" (Q20 trade DPS), "ar", "ev",
/// "es", "life" (incl. 2 per Strength, as the trade site's pseudo total counts it), "ele_res", "chaos_res", "ms", or
/// "mod" for <see cref="Affix"/>'s strongest line <see cref="Line"/>. <see cref="Importance"/> is its share of the score.</summary>
public sealed record ValueDriver(string Key, string Label, double Value, double Importance, AppraisedAffix? Affix = null, string? Line = null);

/// <summary>A rare/magic item judged on what its slot's buyers pay for.</summary>
public sealed record ItemAppraisal(ItemSlot Slot, string SlotName, AppraisalGrade Grade, double Score,
    IReadOnlyList<AppraisedAffix> Affixes, WeaponDps? Dps, Defences? Defence,
    int TotalLife, int TotalElementalResistance, int TotalChaosResistance, int MovementSpeed,
    int OpenPrefixes, int OpenSuffixes, IReadOnlyList<ValueDriver> Drivers, string? Cap)
{
    public string GradeName => ItemAppraiser.GradeName(Grade);

    /// <summary>Up to three strongest selling points, e.g. "35% move speed", "+142 life", "112% res".</summary>
    public IReadOnlyList<string> Highlights => Drivers.Where(d => d.Importance >= 0.45).Take(3).Select(d => d.Label).ToList();

    /// <summary>One line: grade and the strongest points, e.g. "Good · 35% move speed · +142 life · 112% res".</summary>
    public string Summary => Highlights.Count == 0 ? GradeName : GradeName + " · " + string.Join(" · ", Highlights);
}

/// <summary>
/// Judges a rare or magic item the way a buyer does: the few stats its slot is bought for decide its value; the rest
/// is filler. Each affix is placed in its tier on this base (from the RePoE tables) and its roll compared with the
/// best that stat reaches on the base; weapons are judged on DPS against a top-tier roll of the same base, armour on
/// its local defences. Pure: the same item always gets the same verdict.
/// </summary>
public static class ItemAppraiser
{
    public static string GradeName(AppraisalGrade g) => g switch
    {
        AppraisalGrade.Top => "Top tier",
        AppraisalGrade.Good => "Good",
        AppraisalGrade.Decent => "Decent",
        AppraisalGrade.Low => "Low value",
        _ => "Vendor",
    };

    public static double Weight(AffixRole role) => role switch
    {
        AffixRole.Premium => 3.0,
        AffixRole.Key => 1.8,
        AffixRole.Useful => 1.0,
        AffixRole.Minor => 0.35,
        _ => 0.0,
    };

    // Score thresholds per grade, calibrated on hand-built items (see ItemAppraisalTests): mediocre filler lands in
    // Vendor/Low, one or two strong stats in Decent, three or four in Good, a near-perfect item in Top.
    private const double TopScore = 6.5, GoodScore = 4.5, DecentScore = 2.6, LowScore = 1.2;

    /// <summary>A weapon's DPS counts as two premium affixes: it takes two or three prefixes and a suffix to build.</summary>
    private const double DpsWeight = 2 * 3.0;

    /// <summary>Appraise a rare/magic item, or null when its base or affixes aren't known (uniques, currency,
    /// waystones, unreadable mods) — callers then fall back to comparing every mod.</summary>
    public static ItemAppraisal? Appraise(ItemTradeProfile item, string? metadata, ItemAffixData? data = null)
    {
        data ??= ItemAffixData.Shared;
        if (item.Rarity is not (Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic) || !item.Identified || !item.Complete
            || item.Affixes is not { Count: > 0 } affixes)
            return null;
        if (data.BaseFor(metadata) is not { } itemBase) return null;
        var slot = SlotOf(itemBase.Class);
        if (slot == ItemSlot.Other) return null;

        var attack = slot == ItemSlot.AttackWeapon;
        var armour = itemBase.Armour + itemBase.Evasion + itemBase.EnergyShield > 0;
        var totals = new Totals();
        var local = new Dictionary<string, int>(StringComparer.Ordinal);
        var scored = new List<AppraisedAffix>();
        var buckets = new Dictionary<string, double>(StringComparer.Ordinal);
        var drivers = new List<ValueDriver>();
        int prefixes = 0, suffixes = 0;

        // Totals count every source the trade site's pseudo stats count: implicits (a ring's resistance, a Runeforged
        // boot's movement speed) as well as explicits. Implicits aren't in the affix table; their stat ids come from
        // the translator's mod table.
        foreach (var a in affixes)
        {
            var info = data.AffixFor(a.Id);
            var statIds = info?.Stats.Select(x => x.Id).ToArray() ?? ItemModTranslator.Shared.StatIdsFor(a.Id) ?? [];
            for (var i = 0; i < statIds.Length && i < a.Values.Count; i++)
            {
                var (id, v) = (statIds[i], a.Values[i]);
                totals.Add(id, v);
                if (a.Kind == "explicit" && (attack && DpsStats.Contains(id) || armour && DefenceStats.Contains(id)))
                    local[id] = local.GetValueOrDefault(id) + v;
            }
        }
        var implicitSpeed = affixes.Where(a => a.Kind != "explicit")
            .Sum(a => (ItemModTranslator.Shared.StatIdsFor(a.Id) ?? []).Select((id, i) => id == "base_movement_velocity_+%" && i < a.Values.Count ? a.Values[i] : 0).Sum());

        foreach (var a in affixes)
        {
            if (a.Kind != "explicit") continue;
            var info = data.AffixFor(a.Id);
            if (info is not null) { if (info.Prefix) prefixes++; else suffixes++; }
            var (affix, groups) = ScoreAffix(a, info, itemBase, slot, attack, armour, data, implicitSpeed);
            scored.Add(affix);

            // Life, resistances and movement speed are bought as totals; everything else on its own line.
            var own = 0.0; var bestGroup = -1.0; string? line = null;
            foreach (var g in groups)
            {
                switch (Bucket(g.Stat))
                {
                    case "res_hybrid":
                        buckets["ele_res"] = buckets.GetValueOrDefault("ele_res") + g.Score / 2;
                        buckets["chaos_res"] = buckets.GetValueOrDefault("chaos_res") + g.Score / 2;
                        break;
                    case { } bucket: buckets[bucket] = buckets.GetValueOrDefault(bucket) + g.Score; break;
                    default:
                        own += g.Score;
                        if (g.Score > bestGroup) { bestGroup = g.Score; line = LineFor(g.Stat, a.Lines); }
                        break;
                }
            }
            if (own > 0 && affix.Role >= AffixRole.Useful && line is not null)
                drivers.Add(new ValueDriver("mod", affix.Tier is { } t ? $"{Shorten(line)} (T{t})" : Shorten(line),
                    FirstNumber(line), Math.Round(own, 3), affix, line));
        }

        var dps = attack ? Dps(itemBase, local, data) : null;
        var defence = armour ? Defence(itemBase, local, data) : null;
        var score = scored.Sum(s => s.Score);
        if (dps is not null)
        {
            var importance = DpsWeight * Square(Math.Min(dps.Quality, 1.0));
            score += importance;
            var trade = dps.TradeTotal;
            drivers.Add(dps.PhysicalQ20 >= trade * 0.67 ? new ValueDriver("pdps", $"{dps.PhysicalQ20:0} pDPS", dps.PhysicalQ20, importance)
                : dps.Elemental >= trade * 0.67 ? new ValueDriver("edps", $"{dps.Elemental:0} eDPS", dps.Elemental, importance)
                : new ValueDriver("dps", $"{trade:0} DPS", trade, importance));
        }
        if (defence is not null)
        {
            var importance = Weight(DefenceRole(slot, itemBase)) * Square(Math.Min(defence.Quality, 1.0));
            score += importance;
            var kinds = new List<(string Key, int Value, string Name)>();
            if (itemBase.EnergyShield > 0) kinds.Add(("es", defence.EnergyShield, "ES"));
            if (itemBase.Armour > 0) kinds.Add(("ar", defence.Armour, "armour"));
            if (itemBase.Evasion > 0) kinds.Add(("ev", defence.Evasion, "evasion"));
            foreach (var (key, value, name) in kinds)
                drivers.Add(new ValueDriver(key, $"{value} {name}", value, importance / kinds.Count));
        }
        if (buckets.GetValueOrDefault("ms") is > 0 and var ms)
            drivers.Add(new ValueDriver("ms", $"{totals.MoveSpeed}% move speed", totals.MoveSpeed, ms));
        if (buckets.GetValueOrDefault("life") is > 0 and var life)
            drivers.Add(new ValueDriver("life", $"+{totals.Life} life", totals.Life + 2 * totals.Strength, life));
        if (buckets.GetValueOrDefault("ele_res") is > 0 and var res)
            drivers.Add(new ValueDriver("ele_res", $"{totals.EleRes}% res", totals.EleRes, res));
        if (buckets.GetValueOrDefault("chaos_res") is > 0 and var chaos)
            drivers.Add(new ValueDriver("chaos_res", $"{totals.ChaosRes}% chaos res", totals.ChaosRes, chaos));
        drivers.Sort((x, y) => y.Importance.CompareTo(x.Importance));

        var grade = score >= TopScore ? AppraisalGrade.Top : score >= GoodScore ? AppraisalGrade.Good
            : score >= DecentScore ? AppraisalGrade.Decent : score >= LowScore ? AppraisalGrade.Low : AppraisalGrade.Vendor;

        // The one stat a slot can't sell without caps the grade, however good the rest is.
        string? cap = null;
        if (slot == ItemSlot.Boots && totals.MoveSpeed < 30)
            cap = Cap(ref grade, totals.MoveSpeed < 20 ? AppraisalGrade.Low : totals.MoveSpeed < 25 ? AppraisalGrade.Decent : AppraisalGrade.Good,
                totals.MoveSpeed == 0 ? "no movement speed" : $"only {totals.MoveSpeed}% movement speed");
        if (dps is not null && dps.Quality < 0.6 && !scored.Any(s => s.CountedIn is null && s.Role == AffixRole.Premium && s.Quality >= 0.5))
            cap = Cap(ref grade, AppraisalGrade.Low, "low damage for its base") ?? cap;

        var (maxPrefix, maxSuffix) = item.Rarity == Poe2Live.Rarity.Magic ? (1, 1) : (3, 3);
        return new ItemAppraisal(slot, SlotName(itemBase.Class), grade, Math.Round(score, 2), scored, dps, defence,
            totals.Life, totals.EleRes, totals.ChaosRes, totals.MoveSpeed,
            Math.Max(0, maxPrefix - prefixes), Math.Max(0, maxSuffix - suffixes), drivers, cap);
    }

    private static string? Cap(ref AppraisalGrade grade, AppraisalGrade max, string why)
    {
        if (grade <= max) return null;
        grade = max;
        return why;
    }

    private static double Square(double q) => q * q;

    private sealed record Group(string Stat, AffixRole Role, double Quality, double Score);

    /// <param name="implicitSpeed">Movement speed the base's implicit already grants: it adds to the affix's roll, since
    /// buyers see the total.</param>
    private static (AppraisedAffix Affix, List<Group> Groups) ScoreAffix(ItemAffix a, ItemAffixData.Affix? info,
        ItemAffixData.ItemBase itemBase, ItemSlot slot, bool attack, bool armour, ItemAffixData data, int implicitSpeed)
    {
        var text = string.Join(" / ", a.Lines);
        var groups = new List<Group>();
        if (info is null) return (new AppraisedAffix(a.Kind, a.Id, text, null, 0, null, 0, AffixRole.Minor, 0, null), groups);
        var tier = data.TierOf(info, itemBase);

        // Score each stat group once ("Adds 5 to 12" is one group of two stats); stats feeding DPS/defences are left to
        // those aggregates. The affix's score sums its groups, so a hybrid is worth its parts.
        var sums = new Dictionary<string, (string Stat, double Q, int N)>(StringComparer.Ordinal);
        var counted = new Dictionary<string, (double Q, int N)>(StringComparer.Ordinal);
        for (var i = 0; i < info.Stats.Count && i < a.Values.Count; i++)
        {
            var id = info.Stats[i].Id;
            var best = data.BestRoll(id, itemBase);
            if (best <= 0) best = Math.Max(Math.Abs(info.Stats[i].Min), Math.Abs(info.Stats[i].Max));
            var value = Math.Abs(a.Values[i]) + (id == "base_movement_velocity_+%" ? implicitSpeed : 0);
            var q = best > 0 ? Math.Clamp(value / (double)best, 0, 1.2) : 0;
            var aggregate = attack && DpsStats.Contains(id) ? "DPS" : armour && DefenceStats.Contains(id) ? "defences" : null;
            if (aggregate is not null)
            {
                counted[aggregate] = counted.TryGetValue(aggregate, out var c) ? (c.Q + q, c.N + 1) : (q, 1);
                continue;
            }
            var key = id.Replace("minimum_", "").Replace("maximum_", "");
            sums[key] = sums.TryGetValue(key, out var g) ? (g.Stat, g.Q + q, g.N + 1) : (id, q, 1);
        }
        var score = 0.0; var quality = 0.0; var topRole = AffixRole.Filler;
        foreach (var (stat, q0, n) in sums.Values)
        {
            var q = q0 / n;
            var role = RoleOf(stat, slot);
            var s = Weight(role) * Square(Math.Min(q, 1.0));
            groups.Add(new Group(stat, role, q, s));
            score += s;
            if (role > topRole || role == topRole && q > quality) { topRole = role; quality = q; }
        }
        // An affix that only feeds DPS/defences is shown with that aggregate's role and its own roll quality.
        string? countedIn = null;
        if (groups.Count == 0 && counted.Count > 0)
        {
            var (name, (q0, n)) = counted.First();
            countedIn = name;
            topRole = name == "DPS" ? AffixRole.Premium : DefenceRole(slot, itemBase);
            quality = q0 / n;
        }
        var affix = new AppraisedAffix(a.Kind, a.Id, text, tier?.Tier, tier?.Count ?? 0, info.Prefix,
            Math.Round(Math.Min(quality, 1.2), 3), topRole, Math.Round(score, 3), countedIn);
        return (affix, groups);
    }

    private static string? Bucket(string stat) => stat switch
    {
        "base_maximum_life" => "life",
        "base_movement_velocity_+%" => "ms",
        "base_fire_damage_resistance_%" or "base_cold_damage_resistance_%" or "base_lightning_damage_resistance_%"
            or "base_resist_all_elements_%" => "ele_res",
        "base_chaos_damage_resistance_%" => "chaos_res",
        "fire_and_chaos_damage_resistance_%" or "cold_and_chaos_damage_resistance_%" or "lightning_and_chaos_damage_resistance_%" => "res_hybrid",
        _ => null,
    };

    /// <summary>Stat totals the trade site filters as pseudo stats (implicits included, as the site counts them).</summary>
    private sealed class Totals
    {
        public int Life, Strength, EleRes, ChaosRes, MoveSpeed;

        public void Add(string id, int v)
        {
            switch (id)
            {
                case "base_maximum_life": Life += v; break;
                case "additional_strength" or "additional_all_attributes" or "additional_strength_and_dexterity"
                    or "additional_strength_and_intelligence":
                    Strength += v; break;
                case "base_fire_damage_resistance_%" or "base_cold_damage_resistance_%" or "base_lightning_damage_resistance_%": EleRes += v; break;
                case "base_resist_all_elements_%": EleRes += 3 * v; break;
                case "base_chaos_damage_resistance_%": ChaosRes += v; break;
                case "fire_and_chaos_damage_resistance_%" or "cold_and_chaos_damage_resistance_%" or "lightning_and_chaos_damage_resistance_%":
                    EleRes += v; ChaosRes += v; break;
                case "base_movement_velocity_+%": MoveSpeed += v; break;
            }
        }
    }

    /// <summary>The rendered line of a multi-line affix that carries <paramref name="stat"/>: the one sharing the most
    /// words with the stat id ("spell_damage_+%" → "…increased Spell Damage", not "+30 to maximum Mana").</summary>
    private static string? LineFor(string stat, IReadOnlyList<string> lines)
    {
        if (lines.Count <= 1) return lines.Count == 1 ? lines[0] : null;
        var words = stat.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && w != "base").ToArray();
        return lines.OrderByDescending(l => words.Count(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).First();
    }

    private static double FirstNumber(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(line, @"\d+(?:\.\d+)?");
        return m.Count == 0 ? 0 : m.Take(2).Average(x => double.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";

    // ── Weapon DPS ──

    private static readonly HashSet<string> DpsStats = new(StringComparer.Ordinal)
    {
        "local_minimum_added_physical_damage", "local_maximum_added_physical_damage",
        "local_minimum_added_fire_damage", "local_maximum_added_fire_damage",
        "local_minimum_added_cold_damage", "local_maximum_added_cold_damage",
        "local_minimum_added_lightning_damage", "local_maximum_added_lightning_damage",
        "local_minimum_added_chaos_damage", "local_maximum_added_chaos_damage",
        "local_physical_damage_+%", "local_attack_speed_+%",
    };

    private static WeaponDps? Dps(ItemAffixData.ItemBase b, IReadOnlyDictionary<string, int> local, ItemAffixData data)
    {
        if (b.AttackTimeMs <= 0) return null;
        int L(string id) => local.GetValueOrDefault(id);
        double Avg(string element) => (L($"local_minimum_added_{element}_damage") + L($"local_maximum_added_{element}_damage")) / 2.0;
        var aps = 1000.0 / b.AttackTimeMs * (1 + L("local_attack_speed_+%") / 100.0);
        var phys = ((b.DamageMin + b.DamageMax) / 2.0 + Avg("physical")) * (1 + L("local_physical_damage_+%") / 100.0) * aps;
        var ele = (Avg("fire") + Avg("cold") + Avg("lightning")) * aps;
        var chaos = Avg("chaos") * aps;

        // Benchmark: the same base with a top-tier roll — best % physical, best flat physical, best attack speed; or,
        // for elemental, the three best flat elemental prefixes.
        double Best(string id) => data.BestRoll(id, b);
        double BestAvg(string element) => (Best($"local_minimum_added_{element}_damage") + Best($"local_maximum_added_{element}_damage")) / 2.0;
        var bestAps = 1000.0 / b.AttackTimeMs * (1 + Best("local_attack_speed_+%") / 100.0);
        var bestPhys = ((b.DamageMin + b.DamageMax) / 2.0 + BestAvg("physical")) * (1 + Best("local_physical_damage_+%") / 100.0) * bestAps;
        var bestEle = (BestAvg("fire") + BestAvg("cold") + BestAvg("lightning")) * bestAps;
        var q = 0.0;
        if (bestPhys > 0) q = Math.Max(q, phys / bestPhys);
        if (bestEle > 0) q = Math.Max(q, ele / bestEle);
        var top = Math.Max(bestPhys, bestEle);
        if (top > 0) q = Math.Max(q, (phys + ele + chaos) / top);
        return new WeaponDps(Math.Round(phys, 1), Math.Round(ele, 1), Math.Round(chaos, 1), Math.Round(aps, 2), Math.Round(Math.Min(q, 1.2), 3));
    }

    // ── Local defences ──

    private static readonly HashSet<string> DefenceStats = new(StringComparer.Ordinal)
    {
        "local_base_physical_damage_reduction_rating", "local_base_evasion_rating", "local_energy_shield",
        "local_physical_damage_reduction_rating_+%", "local_evasion_rating_+%", "local_energy_shield_+%",
        "local_armour_and_evasion_+%", "local_armour_and_energy_shield_+%", "local_evasion_and_energy_shield_+%",
        "local_armour_and_evasion_and_energy_shield_+%",
    };

    private static Defences? Defence(ItemAffixData.ItemBase b, IReadOnlyDictionary<string, int> local, ItemAffixData data)
    {
        int L(string id) => local.GetValueOrDefault(id);
        const string all = "local_armour_and_evasion_and_energy_shield_+%";
        var ar = (b.Armour + L("local_base_physical_damage_reduction_rating"))
            * (1 + (L("local_physical_damage_reduction_rating_+%") + L("local_armour_and_evasion_+%") + L("local_armour_and_energy_shield_+%") + L(all)) / 100.0);
        var ev = (b.Evasion + L("local_base_evasion_rating"))
            * (1 + (L("local_evasion_rating_+%") + L("local_armour_and_evasion_+%") + L("local_evasion_and_energy_shield_+%") + L(all)) / 100.0);
        var es = (b.EnergyShield + L("local_energy_shield"))
            * (1 + (L("local_energy_shield_+%") + L("local_armour_and_energy_shield_+%") + L("local_evasion_and_energy_shield_+%") + L(all)) / 100.0);

        // Benchmark per defence the base has: its best flat roll plus its best single % roll (hybrids included).
        double Best(string id) => data.BestRoll(id, b);
        double Pct(params string[] ids) => ids.Max(Best);
        var parts = new List<double>();
        if (b.Armour > 0)
            parts.Add(ar / ((b.Armour + Best("local_base_physical_damage_reduction_rating"))
                * (1 + Pct("local_physical_damage_reduction_rating_+%", "local_armour_and_evasion_+%", "local_armour_and_energy_shield_+%", all) / 100.0)));
        if (b.Evasion > 0)
            parts.Add(ev / ((b.Evasion + Best("local_base_evasion_rating"))
                * (1 + Pct("local_evasion_rating_+%", "local_armour_and_evasion_+%", "local_evasion_and_energy_shield_+%", all) / 100.0)));
        if (b.EnergyShield > 0)
            parts.Add(es / ((b.EnergyShield + Best("local_energy_shield"))
                * (1 + Pct("local_energy_shield_+%", "local_armour_and_energy_shield_+%", "local_evasion_and_energy_shield_+%", all) / 100.0)));
        if (parts.Count == 0) return null;
        return new Defences((int)Math.Round(ar), (int)Math.Round(ev), (int)Math.Round(es), Math.Round(Math.Min(parts.Average(), 1.2), 3));
    }

    /// <summary>Body armours, shields and foci are bought for their defences, and ES builds stack local ES on every
    /// piece; elsewhere defences are a bonus.</summary>
    private static AffixRole DefenceRole(ItemSlot slot, ItemAffixData.ItemBase b) => slot switch
    {
        ItemSlot.BodyArmour or ItemSlot.Shield or ItemSlot.Focus => AffixRole.Key,
        _ when b.EnergyShield > 0 => AffixRole.Key,
        _ => AffixRole.Useful,
    };

    // ── Slots and roles ──

    public static ItemSlot SlotOf(string itemClass) => itemClass switch
    {
        "Boots" => ItemSlot.Boots, "Gloves" => ItemSlot.Gloves, "Helmet" => ItemSlot.Helmet, "Body Armour" => ItemSlot.BodyArmour,
        "Shield" => ItemSlot.Shield, "Buckler" => ItemSlot.Buckler, "Focus" => ItemSlot.Focus, "Quiver" => ItemSlot.Quiver,
        "Ring" => ItemSlot.Ring, "Amulet" => ItemSlot.Amulet, "Belt" => ItemSlot.Belt, "Jewel" => ItemSlot.Jewel,
        "Wand" or "Staff" => ItemSlot.CasterWeapon, "Sceptre" => ItemSlot.Sceptre,
        "One Hand Mace" or "Two Hand Mace" or "Warstaff" or "Spear" or "Bow" or "Crossbow" or "Talisman" or "One Hand Sword"
            or "Two Hand Sword" or "One Hand Axe" or "Two Hand Axe" or "Flail" or "Dagger" or "Claw" => ItemSlot.AttackWeapon,
        _ => ItemSlot.Other,
    };

    private static string SlotName(string itemClass) => itemClass == "Warstaff" ? "quarterstaff" : itemClass.ToLowerInvariant();

    /// <summary>
    /// What a stat is worth on a slot. Premium: the stat the slot is bought for (boots' movement speed, spirit,
    /// +levels to skills, % life, maximum resistances). Key: the main damage/defence scalers of the slot. Useful:
    /// resistances and secondary stats most builds want. Minor: nice-to-haves. Filler: stats no buyer filters on.
    /// </summary>
    public static AffixRole RoleOf(string stat, ItemSlot slot)
    {
        if (slot == ItemSlot.Jewel) return JewelRole(stat);
        var caster = slot is ItemSlot.CasterWeapon or ItemSlot.Focus;
        switch (stat)
        {
            case "base_movement_velocity_+%": return slot == ItemSlot.Boots ? AffixRole.Premium : AffixRole.Useful;
            case "base_spirit_from_equipment" or "maximum_life_+%" or "all_skill_gem_level_+"
                or "additional_maximum_all_elemental_resistances_%" or "additional_maximum_all_resistances_%":
                return AffixRole.Premium;
            case "base_maximum_life" or "base_resist_all_elements_%": return AffixRole.Key;
            case "base_fire_damage_resistance_%" or "base_cold_damage_resistance_%" or "base_lightning_damage_resistance_%"
                or "base_chaos_damage_resistance_%" or "fire_and_chaos_damage_resistance_%" or "cold_and_chaos_damage_resistance_%"
                or "lightning_and_chaos_damage_resistance_%":
                return AffixRole.Useful;
            case "local_spirit_+%" or "skill_speed_+%" or "base_spirit_reservation_efficiency_+%" or "aura_effect_+%":
                return AffixRole.Key;
            case "spell_damage_+%": return caster || slot is ItemSlot.Amulet ? AffixRole.Key : AffixRole.Useful;
            case "fire_damage_+%" or "cold_damage_+%" or "lightning_damage_+%" or "chaos_damage_+%" or "spell_physical_damage_+%"
                or "elemental_damage_+%":
                return caster ? AffixRole.Key : AffixRole.Useful;
            case "base_cast_speed_+%": return caster ? AffixRole.Key : AffixRole.Useful;
            case "spell_critical_strike_chance_+%" or "base_spell_critical_strike_multiplier_+": return AffixRole.Key;
            case "attack_speed_+%": return slot is ItemSlot.Gloves or ItemSlot.Quiver ? AffixRole.Key : AffixRole.Useful;
            case "base_critical_strike_multiplier_+" or "attack_critical_strike_multiplier_+": return AffixRole.Key;
            case "critical_strike_chance_+%" or "attack_critical_strike_chance_+%": return AffixRole.Useful;
            case "maximum_energy_shield_+%": return AffixRole.Key;
            case "chance_to_fire_1_additional_projectile_%_with_rollover_with_bow_attacks" or "base_number_of_crossbow_bolts"
                or "number_of_additional_arrows":
                return AffixRole.Key;
            case "base_maximum_fire_damage_resistance_%" or "base_maximum_cold_damage_resistance_%"
                or "base_maximum_lightning_damage_resistance_%" or "base_maximum_chaos_damage_resistance_%":
                return AffixRole.Key;
            case "base_item_found_rarity_+%" or "additional_all_attributes" or "local_critical_strike_chance"
                or "local_critical_strike_multiplier_+" or "damage_+%_with_bow_skills" or "elemental_damage_with_attack_skills_+%"
                or "evasion_rating_+%" or "physical_damage_reduction_rating_+%" or "global_armour_evasion_energy_shield_+%"
                or "base_maximum_energy_shield" or "local_block_chance_+%" or "base_cooldown_speed_+%"
                or "armour_%_applies_to_fire_cold_lightning_damage" or "base_deflection_rating_%_of_evasion_rating"
                or "base_additional_physical_damage_reduction_%" or "curse_effect_+%" or "projectile_damage_+%"
                or "minion_damage_+%" or "minion_maximum_life_+%" or "minion_attack_and_cast_speed_+%":
                return AffixRole.Useful;
        }
        if (stat.EndsWith("_skill_gem_level_+", StringComparison.Ordinal))
            return stat is "trap_skill_gem_level_+" or "mark_skill_gem_level_+" ? AffixRole.Useful : AffixRole.Premium;
        if (stat.StartsWith("non_skill_base_all_damage_%_to_gain_as_", StringComparison.Ordinal)) return AffixRole.Key;
        if (stat.StartsWith("attack_minimum_added_", StringComparison.Ordinal) || stat.StartsWith("attack_maximum_added_", StringComparison.Ordinal))
            return AffixRole.Useful;
        if (stat.StartsWith("allies_in_presence_", StringComparison.Ordinal)) return AffixRole.Useful;
        if (IsFiller(stat)) return AffixRole.Filler;
        return AffixRole.Minor;
    }

    /// <summary>Jewels hold only small modifiers, so every real stat counts (Useful); speed, crit damage and % life/ES
    /// are what builds stack them for (Key).</summary>
    private static AffixRole JewelRole(string stat)
    {
        if (IsFiller(stat)) return AffixRole.Filler;
        return stat is "attack_speed_+%" or "base_cast_speed_+%" or "skill_speed_+%" or "base_critical_strike_multiplier_+"
            or "maximum_life_+%" or "maximum_energy_shield_+%" or "minion_attack_and_cast_speed_+%" or "base_spirit_reservation_efficiency_+%"
            ? AffixRole.Key : AffixRole.Useful;
    }

    private static bool IsFiller(string stat) =>
        stat.Contains("light_radius", StringComparison.Ordinal)
        || stat.StartsWith("thorns_", StringComparison.Ordinal) || stat.Contains("_thorns_", StringComparison.Ordinal)
        || stat is "stun_threshold_+" or "local_attribute_requirements_+%" or "global_item_attribute_requirements_+%"
            or "local_base_stun_duration_+%" or "local_hit_damage_stun_multiplier_+%" or "base_life_gain_per_target"
            or "local_life_gain_per_target" or "gain_x_rage_when_hit" or "base_all_ailment_duration_on_self_+%"
        || stat.StartsWith("base_self_", StringComparison.Ordinal) || stat.StartsWith("self_", StringComparison.Ordinal)
        || stat.StartsWith("fish", StringComparison.Ordinal);
}
