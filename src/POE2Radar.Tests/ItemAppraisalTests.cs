using POE2Radar.Core.Game;
using POE2Radar.Overlay.Pricing;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ItemAppraisalTests
{
    internal const string EsEvBoots = "Metadata/Items/Armours/Boots/FourBootsDexInt4Endgame";   // Daggerfoot Shoes: 140 EV / 43 ES
    internal const string Bow = "Metadata/Items/Weapons/TwoHandWeapons/Bows/FourBow9Endgame";      // Obliterator Bow: 62–115, 909 ms
    internal const string Amulet = "Metadata/Items/Amulets/FourAmulet9";
    internal const string ColdWand = "Metadata/Items/Weapons/OneHandWeapons/Wands/FourWand9";       // Frigid Wand: no fire/lightning spell mods
    internal const string EsBody = "Metadata/Items/Armours/BodyArmours/FourBodyInt10Endgame";      // Feathered Raiment: 153 ES
    internal const string EsBoots = "Metadata/Items/Armours/Boots/FourBootsInt6Endgame";           // Sekhema Sandals: 83 ES

    internal static ItemAffix A(string id, params int[] values) => A("explicit", id, values);

    internal static ItemAffix A(string kind, string id, params int[] values) =>
        new(kind, id, values, ItemModTranslator.Shared.RenderMod(id, values));

    internal static ItemTradeProfile Item(Poe2Live.Rarity rarity, params ItemAffix[] affixes) =>
        new("Base", rarity, true, affixes.SelectMany(a => a.Lines.Select(l => new ItemTradeMod(a.Kind, l))).ToArray(), true, affixes);

    internal static ItemTradeProfile Rare(params ItemAffix[] affixes) => Item(Poe2Live.Rarity.Rare, affixes);

    internal static ItemTradeProfile TopBoots => Rare(A("MovementVelocity6", 35), A("IncreasedLife9", 142),
        A("LocalIncreasedEvasionAndEnergyShield7", 96), A("FireResist8", 43), A("ColdResist8", 44), A("LightningResist7", 38));

    internal static ItemTradeProfile TopBow => Rare(A("LocalIncreasedPhysicalDamagePercent8", 175), A("LocalAddedPhysicalDamage9", 35, 60),
        A("LocalIncreasedPhysicalDamagePercentAndAccuracyRating8", 77, 190), A("LocalIncreasedAttackSpeed5", 18),
        A("GlobalProjectileSkillGemLevelWeapon5", 4), A("LocalCriticalMultiplier6", 24));

    private static ItemAppraisal Appraise(string metadata, ItemTradeProfile item) => ItemAppraiser.Appraise(item, metadata)!;

    [Fact]
    public void Tiers_rank_the_family_on_this_base_best_first()
    {
        var data = ItemAffixData.Shared;
        Assert.True(data.IsLoaded);
        var boots = data.BaseFor(EsEvBoots)!;
        var body = data.BaseFor(EsBody)!;
        var life9 = data.AffixFor("IncreasedLife9")!;
        Assert.Equal((1, 9), data.TierOf(life9, boots));          // boots top out at the 120–149 tier...
        Assert.Equal((5, 13), data.TierOf(life9, body));          // ...body armours roll four better ones
        Assert.Equal(149, data.BestRoll("base_maximum_life", boots));
        Assert.Equal(214, data.BestRoll("base_maximum_life", body));
        Assert.Equal((1, 6), data.TierOf(data.AffixFor("MovementVelocity6")!, boots));
        Assert.Null(data.TierOf(data.AffixFor("MovementVelocity6")!, body));   // body armours can't roll movement speed
    }

    [Fact]
    public void Spawn_tags_are_read_in_order_so_excluded_families_never_count()
    {
        var data = ItemAffixData.Shared;
        var wand = data.BaseFor(ColdWand)!;
        var rollable = data.Rollable(wand).Select(a => a.Family).ToHashSet();
        Assert.Contains("ColdDamageWeaponPrefix", rollable);
        Assert.DoesNotContain("FireDamageWeaponPrefix", rollable);   // "no_fire_spell_mods" comes before "wand"
        Assert.True(data.BaseFor(EsEvBoots.ToLowerInvariant()) is not null);   // the export mixes Metadata/Items and Metadata/items
    }

    [Fact]
    public void Great_boots_are_top_tier_and_driven_by_resistance_movement_speed_and_life()
    {
        var a = Appraise(EsEvBoots, TopBoots);
        Assert.Equal(AppraisalGrade.Top, a.Grade);
        Assert.Equal(["ele_res", "ms", "life"], a.Drivers.Take(3).Select(d => d.Key));
        Assert.Equal(["125% res", "35% move speed", "+142 life"], a.Highlights);
        Assert.Equal(125, a.TotalElementalResistance);
        Assert.Equal(0, a.OpenPrefixes + a.OpenSuffixes);
        var hybrid = a.Affixes.Single(x => x.ModId == "LocalIncreasedEvasionAndEnergyShield7");
        Assert.Equal("defences", hybrid.CountedIn);
        Assert.Equal(1, hybrid.Tier);
        // 96% increased on the 140 EV / 43 ES base; the driver carries the trade site's Q20 figure (×1.2).
        Assert.Equal(274, a.Defence!.Evasion);
        Assert.Equal(84, a.Defence.EnergyShield);
        var es = a.Drivers.Single(d => d.Key == "es");
        Assert.Equal("84 ES", es.Label);
        Assert.Equal(100.8, es.Value, 6);
    }

    [Fact]
    public void Boots_without_real_movement_speed_are_held_back_however_good_the_rest()
    {
        var noSpeed = Appraise(EsEvBoots, Rare(A("IncreasedLife9", 145), A("FireResist8", 44), A("ColdResist8", 43), A("LightningResist8", 45)));
        Assert.True(noSpeed.Score >= 2.6);   // would be Decent on stats alone
        Assert.Equal(AppraisalGrade.Low, noSpeed.Grade);
        Assert.Equal("no movement speed", noSpeed.Cap);

        // What sells in 0.5.5: a pure-ES boot with top % and flat ES and three resistances — held to Good at 25% speed.
        var slow = Appraise(EsBoots, Rare(A("MovementVelocity4", 25), A("LocalIncreasedEnergyShieldPercent7_", 98), A("LocalIncreasedEnergyShield7", 58),
            A("FireResist8", 44), A("ColdResist8", 43), A("LightningResist8", 45)));
        Assert.True(slow.Score >= 6.5);   // Top on stats alone
        Assert.Equal(AppraisalGrade.Good, slow.Grade);
        Assert.Equal("only 25% movement speed", slow.Cap);
        Assert.Equal(AppraisalGrade.Top, Appraise(EsBoots, Rare(A("MovementVelocity6", 35), A("LocalIncreasedEnergyShieldPercent7_", 98),
            A("LocalIncreasedEnergyShield7", 58), A("FireResist8", 44), A("ColdResist8", 43), A("LightningResist8", 45))).Grade);
    }

    [Theory]
    [InlineData(30, 110, 37, 33, AppraisalGrade.Decent)]   // life + resistance boots list cheap in 0.5.5
    [InlineData(30, 65, 22, 0, AppraisalGrade.Low)]
    [InlineData(20, 35, 0, 0, AppraisalGrade.Vendor)]
    public void Boots_grade_with_their_rolls(int speed, int life, int fire, int cold, AppraisalGrade expected)
    {
        var affixes = new List<ItemAffix>
        {
            A(speed switch { 35 => "MovementVelocity6", 30 => "MovementVelocity5", 25 => "MovementVelocity4", _ => "MovementVelocity3" }, speed),
            A(life >= 100 ? "IncreasedLife8" : life >= 60 ? "IncreasedLife5" : "IncreasedLife3", life),
            A("StunThreshold9", 230),
        };
        if (fire > 0) affixes.Add(A(fire >= 36 ? "FireResist7" : "FireResist4", fire));
        if (cold > 0) affixes.Add(A("ColdResist6", cold));
        Assert.Equal(expected, Appraise(EsEvBoots, Rare([.. affixes])).Grade);
    }

    [Fact]
    public void Filler_counts_for_nothing()
    {
        var body = Appraise(EsBody, Rare(A("IncreasedLife7", 90), A("LocalIncreasedEnergyShield8", 60), A("StunThreshold8", 180),
            A("AttackerTakesDamage5", 60, 90), A("ReducedBleedDuration3", -48)));
        Assert.Equal(AppraisalGrade.Vendor, body.Grade);
        var filler = body.Affixes.Where(x => x.Text.Contains("Stun") || x.Text.Contains("Thorns") || x.Text.Contains("Bleeding")).ToList();
        Assert.Equal(3, filler.Count);
        Assert.All(filler, x => { Assert.Equal(AffixRole.Filler, x.Role); Assert.Equal(0, x.Score); });
        Assert.DoesNotContain(body.Drivers, d => d.Affix is { } a && filler.Contains(a));
    }

    [Fact]
    public void Weapons_are_judged_on_dps_against_a_top_roll_of_the_same_base()
    {
        var a = Appraise(Bow, TopBow);
        // ((62+115)/2 + (35+60)/2) × (1 + (175+77)%) × (1000/909 × 1.18) = 621.4 pDPS; the trade site shows it at Q20 (×1.2).
        Assert.Equal(621.4, a.Dps!.Physical, 1);
        Assert.Equal(745.7, a.Dps.PhysicalQ20, 1);
        Assert.True(a.Dps.Quality > 0.95);
        Assert.Equal(AppraisalGrade.Top, a.Grade);
        Assert.Equal(["pdps", "mod", "mod"], a.Drivers.Take(3).Select(d => d.Key));
        Assert.Equal("746 pDPS", a.Drivers[0].Label);
        Assert.Equal("+4 to Level of all Projectile Skills (T1)", a.Drivers[1].Label);
        Assert.Equal("DPS", a.Affixes.Single(x => x.ModId == "LocalIncreasedAttackSpeed5").CountedIn);

        // Good crit suffixes don't sell a bow with mediocre damage.
        var weak = Appraise(Bow, Rare(A("LocalIncreasedPhysicalDamagePercent5", 110), A("LocalAddedPhysicalDamage6", 15, 28),
            A("LocalIncreasedAttackSpeed3", 12), A("LocalCriticalMultiplier6", 24), A("LocalCriticalStrikeChance6", 480)));
        Assert.True(weak.Score >= 2.6);
        Assert.Equal(AppraisalGrade.Low, weak.Grade);
        Assert.Equal("low damage for its base", weak.Cap);

        // Plain +4 projectile levels don't sell a bow without damage in 0.5.5 (24 listed at 1–30 ex)...
        var levels = Appraise(Bow, Rare(A("GlobalProjectileSkillGemLevelWeapon5", 4), A("LocalIncreasedAccuracy8", 400)));
        Assert.Equal(AppraisalGrade.Low, levels.Grade);
        // ...an essence +attack skill levels mod does (spears with +2 asked 4–45 div).
        var essence = Appraise(Bow, Rare(A("EssenceAttackSkillLevel1H1", 2), A("LocalIncreasedAccuracy8", 400)));
        Assert.Null(essence.Cap);
    }

    [Fact]
    public void Caster_and_jewellery_value_skill_levels_spirit_and_casting_stats()
    {
        var wand = Appraise(ColdWand, Rare(A("GlobalSpellGemsLevelWeapon4", 4), A("SpellDamageOnWeapon8_", 112),
            A("SpellDamageGainedAsFire6", 29), A("IncreasedCastSpeed7", 34), A("SpellCriticalStrikeChance6_", 70), A("IncreasedMana10", 130)));
        Assert.Equal(AppraisalGrade.Top, wand.Grade);
        Assert.Null(wand.Dps);
        Assert.Equal(AffixRole.Minor, wand.Affixes.Single(x => x.ModId == "IncreasedMana10").Role);

        var amulet = Appraise(Amulet, Rare(A("IncreasedSpirit5", 49), A("IncreasedLife8", 112), A("IncreasedEnergyShieldPercent6", 41),
            A("GlobalSpellGemsLevel3", 3), A("CastSpeedJewellery5", 23), A("AllResistances5", 16)));
        Assert.Equal(AppraisalGrade.Top, amulet.Grade);
        Assert.Equal(48, amulet.TotalElementalResistance);   // all-res counts three times, as the trade site's pseudo total does
        Assert.Contains("+49 to Spirit (T1)", amulet.Highlights);

        var junk = Appraise(Amulet, Rare(A("IncreasedLife4", 50), A("IncreasedAccuracy6", 200), A("LifeRegeneration5", 700), A("AllAttributes3", 8)));
        Assert.Equal(AppraisalGrade.Vendor, junk.Grade);
    }

    [Fact]
    public void Pseudo_life_includes_two_per_strength_and_hybrid_life_counts()
    {
        var body = Appraise(EsBody, Rare(A("IncreasedSpirit8", 59), A("IncreasedLife12", 195), A("LocalIncreasedEnergyShieldAndLife6", 40, 45),
            A("Intelligence7", 29), A("ChaosResist6", 25)));
        Assert.Equal(240, body.TotalLife);
        var life = body.Drivers.Single(d => d.Key == "life");
        Assert.Equal(240, life.Value);
        Assert.Equal(25, body.Drivers.Single(d => d.Key == "chaos_res").Value);
        Assert.Equal(AppraisalGrade.Good, body.Grade);

        var ring = Appraise("Metadata/Items/Rings/FourRing3", Rare(A("IncreasedLife6", 80), A("Strength6", 26)));
        Assert.Equal(80 + 2 * 26, ring.Drivers.Single(d => d.Key == "life").Value);
    }

    [Fact]
    public void Implicits_count_toward_the_totals_buyers_filter_on()
    {
        // A Ruby Ring's implicit fire resistance is in the trade site's pseudo total, so it's in ours.
        var ring = Appraise("Metadata/Items/Rings/FourRing3", Rare(A("implicit", "RingImplicitFireResistance1", 25),
            A("IncreasedLife6", 80), A("ColdResist8", 44), A("LightningResist8", 43)));
        Assert.Equal(25 + 44 + 43, ring.TotalElementalResistance);
        Assert.Equal(112, ring.Drivers.Single(d => d.Key == "ele_res").Value);

        // A boot implicit's movement speed adds to the affix: 10% + 25% sells like a 35% boot.
        var plain = Appraise(EsEvBoots, Rare(A("MovementVelocity4", 25), A("IncreasedLife9", 140)));
        var runeforged = Appraise(EsEvBoots, Rare(A("implicit", "BootsImplicitMovementSpeedVerisium2", 10), A("MovementVelocity4", 25),
            A("IncreasedLife9", 140)));
        Assert.Equal(35, runeforged.MovementSpeed);
        Assert.Equal("35% move speed", runeforged.Drivers[0].Label);
        Assert.True(runeforged.Score > plain.Score + 0.5);
        Assert.Null(runeforged.Cap);
    }

    [Fact]
    public void Magic_items_have_one_prefix_and_one_suffix()
    {
        var a = Appraise(EsEvBoots, Item(Poe2Live.Rarity.Magic, A("MovementVelocity6", 35)));
        Assert.Equal(0, a.OpenPrefixes);
        Assert.Equal(1, a.OpenSuffixes);
    }

    [Fact]
    public void An_affix_missing_from_the_table_never_shows_as_an_open_slot()
    {
        var withUnknown = Rare(A("MovementVelocity6", 35), A("IncreasedLife9", 142), A("FireResist8", 43),
            new ItemAffix("explicit", "SomeNewPatchMod", [5], ["5% increased Something New"]));
        var a = Appraise(EsEvBoots, withUnknown);
        Assert.Equal(0, a.OpenPrefixes);
        Assert.Equal(0, a.OpenSuffixes);
        Assert.Equal(0, a.Affixes.Single(x => x.ModId == "SomeNewPatchMod").Score);
    }

    [Fact]
    public void Unknown_bases_uniques_and_partial_reads_are_not_appraised()
    {
        Assert.Null(ItemAppraiser.Appraise(TopBoots, "Metadata/Items/Something/New"));
        Assert.Null(ItemAppraiser.Appraise(TopBoots with { Rarity = Poe2Live.Rarity.Unique }, EsEvBoots));
        Assert.Null(ItemAppraiser.Appraise(TopBoots with { Complete = false }, EsEvBoots));
        Assert.Null(ItemAppraiser.Appraise(TopBoots with { Affixes = null }, EsEvBoots));
    }
}
