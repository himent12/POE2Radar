using System.Text.Json;
using POE2Radar.Overlay.Pricing;
using Xunit;
using static POE2Radar.Tests.ItemAppraisalTests;

namespace POE2Radar.Tests;

/// <summary>The trade search built from an appraisal: what a rare is compared on and how the ladder loosens.</summary>
public sealed class ComparableSearchTests
{
    // Trade-site stats as /api/trade2/data/stats lists them (ids and wording copied from the live catalogue, 2026-09-26).
    private static readonly TradeComparison.TradeStat[] Stats =
    [
        new("explicit.stat_681332047", "#% increased Attack Speed"),
        new("explicit.stat_210067635", "#% increased Attack Speed (Local)"),
        new("explicit.stat_1202301673", "# to Level of all Projectile Skills"),
        new("explicit.stat_2694482655", "#% to Critical Damage Bonus"),
        new("explicit.stat_3981240776", "# to Spirit"),
        new("explicit.stat_2704225257", "# to Spirit"),
        new("explicit.stat_4052037485", "# to maximum Energy Shield (Local)"),
        new("explicit.stat_3489782002", "# to maximum Energy Shield"),
        new("explicit.stat_3299347043", "# to maximum Life"),
        new("explicit.stat_2254480358", "# to Level of all Cold Spell Skills"),
        new("explicit.stat_124131830", "# to Level of all Spell Skills"),
    ];

    [Fact]
    public void Local_mods_match_the_local_trade_stat_and_global_ones_the_global()
    {
        Assert.Equal("explicit.stat_210067635", TradeComparison.MatchLine("explicit", "18% increased Attack Speed", Stats, local: true)!.Id);
        Assert.Equal("explicit.stat_681332047", TradeComparison.MatchLine("explicit", "7% increased Attack Speed", Stats, local: false)!.Id);
        Assert.Equal("explicit.stat_4052037485", TradeComparison.MatchLine("explicit", "+60 to maximum Energy Shield", Stats, local: true)!.Id);
        Assert.Equal("explicit.stat_3489782002", TradeComparison.MatchLine("explicit", "+60 to maximum Energy Shield", Stats)!.Id);
        Assert.True(TradeComparison.IsLocalLine(A("LocalIncreasedAttackSpeed5", 18), "18% increased Attack Speed"));
        Assert.False(TradeComparison.IsLocalLine(A("IncreasedLife9", 142), "+142 to maximum Life"));
    }

    [Fact]
    public void Locality_is_decided_per_line_so_a_hybrids_global_half_stays_global()
    {
        // Alloy hybrid: global accuracy + local attack speed on a weapon. The translator knows which stat made each line.
        var hybrid = A("AlloyAccuracyAttackSpeedHybrid1", 400, 7);
        Assert.Equal(["+400 to Accuracy Rating", "7% increased Attack Speed"], hybrid.Lines);
        Assert.False(TradeComparison.IsLocalLine(hybrid, "+400 to Accuracy Rating"));
        Assert.True(TradeComparison.IsLocalLine(hybrid, "7% increased Attack Speed"));
        TradeComparison.TradeStat[] stats =
            [new("explicit.stat_803737631", "# to Accuracy Rating"), new("explicit.stat_691932474", "# to Accuracy Rating (Local)")];
        Assert.Equal("explicit.stat_803737631",
            TradeComparison.MatchLine("explicit", "+400 to Accuracy Rating", stats, TradeComparison.IsLocalLine(hybrid, "+400 to Accuracy Rating"))!.Id);

        // A hybrid whose local line shares no words with its stat id ("increased Armour" ← local_physical_damage_reduction_rating_+%).
        var armourLife = A("LocalIncreasedArmourAndLife1", 30, 20);
        var armourLine = armourLife.Lines.Single(l => l.Contains("Armour"));
        Assert.True(TradeComparison.IsLocalLine(armourLife, armourLine));
        Assert.False(TradeComparison.IsLocalLine(armourLife, armourLife.Lines.Single(l => l.Contains("Life"))));
    }

    [Theory]
    [InlineData(3, 0.9, true, 3)]         // +3 levels stays +3 — flooring 2.7 would let +2 items in
    [InlineData(1, 0.8, true, 1)]
    [InlineData(35, 0.9, true, 32)]       // only the 35% movement speed tier
    [InlineData(100, 0.8, true, 80)]      // 80.000000001 in floating point, still 80
    [InlineData(4.41, 0.9, true, 3.96)]   // fractional rolls keep two decimals
    [InlineData(102, 0.9, false, 91.8)]   // computed figures (DPS, Q20 defences) never round to steps, even when whole
    public void Search_minimums_never_drop_a_whole_step_on_small_stats(double value, double factor, bool wholeSteps, double expected)
        => Assert.Equal(expected, PriceCheck.MinFor(value, factor, wholeSteps), 6);

    [Fact]
    public void Lines_the_trade_site_words_differently_are_aliased()
    {
        TradeComparison.TradeStat[] stats = [new("explicit.stat_1967051901", "Loads an additional bolt")];
        var m = TradeComparison.MatchLine("explicit", "Loads 2 additional bolts", stats);
        Assert.Equal("explicit.stat_1967051901", m!.Id);
        Assert.Equal(2, m.Value);
    }

    [Fact]
    public void Spirit_uses_the_id_item_affixes_are_listed_under()
    {
        var m = TradeComparison.MatchLine("explicit", "+49 to Spirit", Stats);
        Assert.Equal("explicit.stat_3981240776", m!.Id);
        Assert.Equal(49, m.Value);
    }

    [Fact]
    public void Old_line_matching_now_resolves_local_weapon_mods()
    {
        var bow = TopBow;
        var matched = TradeComparison.MatchStats(bow, Stats, out _);
        Assert.Contains(matched, m => m.Id == "explicit.stat_210067635" && m.Value == 18);
        Assert.DoesNotContain(matched, m => m.Id == "explicit.stat_681332047");
    }

    private static JsonElement Query(QueryPlan p) => JsonDocument.Parse(p.Query!).RootElement.GetProperty("query").Clone();

    private static List<QueryPlan> Steps(QueryPlan plan)
    {
        var steps = new List<QueryPlan>();
        for (var p = plan; p is not null; p = p.Next) steps.Add(p);
        return steps;
    }

    [Fact]
    public void Boots_are_searched_on_movement_speed_resistance_and_life_totals_at_ninety_percent()
    {
        var (_, needsStats, plan) = PriceCheck.For(TopBoots, null, EsEvBoots);
        Assert.True(needsStats);
        var steps = Steps(plan(Stats));
        Assert.Equal(4, steps.Count);   // all criteria → top 60% → top two at 80% → every item of the base
        Assert.Contains("any boots with at least your 125% res, 35% move speed, +142 life", steps[0].Note);
        Assert.True(steps[0].Comparable);
        Assert.False(steps[^1].Comparable);

        var q = Query(steps[0]);
        Assert.Equal(PriceCheck.Status, q.GetProperty("status").GetProperty("option").GetString());
        Assert.False(q.TryGetProperty("type", out _));   // any base of the slot
        var filters = q.GetProperty("filters");
        Assert.Equal("armour.boots", filters.GetProperty("type_filters").GetProperty("filters").GetProperty("category").GetProperty("option").GetString());
        Assert.Equal("nonunique", filters.GetProperty("type_filters").GetProperty("filters").GetProperty("rarity").GetProperty("option").GetString());
        Assert.Equal("true", filters.GetProperty("trade_filters").GetProperty("filters").GetProperty("collapse").GetProperty("option").GetString());
        Assert.Equal("false", filters.GetProperty("misc_filters").GetProperty("filters").GetProperty("mirrored").GetProperty("option").GetString());
        var stats = q.GetProperty("stats")[0];
        Assert.Equal("and", stats.GetProperty("type").GetString());
        var byId = stats.GetProperty("filters").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("value"));
        Assert.Equal(32, byId["pseudo.pseudo_increased_movement_speed"].GetProperty("min").GetDouble());   // 35 × 0.9 → only 35% tiers
        Assert.Equal(113, byId["pseudo.pseudo_total_elemental_resistance"].GetProperty("min").GetDouble());
        Assert.Equal(128, byId["pseudo.pseudo_total_life"].GetProperty("min").GetDouble());
        // Local defences are searched at the trade site's Q20 figure: 84 ES × 1.2 × 0.9.
        var equipment = filters.GetProperty("equipment_filters").GetProperty("filters");
        Assert.Equal(90.72, equipment.GetProperty("es").GetProperty("min").GetDouble(), 6);
        Assert.All(byId.Values, v => Assert.False(v.TryGetProperty("max", out _)));   // better items are comparables too
        // The looser rung keeps only the two most important stats, at 80%.
        var core = Query(steps[2]).GetProperty("stats")[0].GetProperty("filters");
        Assert.Equal(2, core.GetArrayLength());
        Assert.Equal(100, core[0].GetProperty("value").GetProperty("min").GetDouble());   // 125% res × 0.8
    }

    [Fact]
    public void Weapons_are_searched_on_trade_dps_plus_their_valuable_affixes()
    {
        var plan = PriceCheck.For(TopBow, null, Bow).Plan(Stats);
        var q = Query(plan);
        Assert.Equal("weapon.bow", q.GetProperty("filters").GetProperty("type_filters").GetProperty("filters").GetProperty("category").GetProperty("option").GetString());
        Assert.Equal(671.11, q.GetProperty("filters").GetProperty("equipment_filters").GetProperty("filters").GetProperty("pdps").GetProperty("min").GetDouble(), 6);
        var ids = q.GetProperty("stats")[0].GetProperty("filters").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
        Assert.Equal(["explicit.stat_1202301673", "explicit.stat_2694482655"], ids);   // +4 projectile levels, crit damage — never attack speed again
    }

    [Fact]
    public void Criteria_skip_what_buyers_ignore_and_cap_the_query_size()
    {
        var wand = Rare(A("GlobalSpellGemsLevelWeapon4", 4), A("SpellDamageOnWeapon8_", 112), A("SpellDamageGainedAsFire6", 29),
            A("IncreasedCastSpeed7", 34), A("SpellCriticalStrikeChance6_", 70), A("IncreasedMana10", 130));
        var criteria = PriceCheck.Criteria(ItemAppraiser.Appraise(wand, ColdWand)!, Stats);
        Assert.Equal("explicit.stat_124131830", criteria[0].StatId);   // +4 spell levels leads
        Assert.DoesNotContain(criteria, c => c.Label.Contains("Mana"));   // minor stat, not searched
        Assert.True(criteria.Count <= 6);

        var filler = Rare(A("IncreasedLife7", 90), A("LocalIncreasedEnergyShield8", 60), A("StunThreshold8", 180));
        var plan = PriceCheck.For(filler with { Name = "Feathered Raiment" }, null, EsBody).Plan(Stats);
        Assert.Contains("any body armour with at least your", plan.Note);
        var trash = Rare(A("StunThreshold8", 180), A("AttackerTakesDamage5", 60, 90));
        var none = PriceCheck.For(trash with { Name = "Feathered Raiment" }, null, EsBody).Plan(Stats);
        Assert.Contains("nothing on it buyers search for", none.Note);
        Assert.False(none.Comparable);
    }

    [Fact]
    public void Items_without_a_known_base_keep_the_every_mod_ladder()
    {
        var plan = PriceCheck.For(TopBoots, null, "Metadata/Items/Something/New").Plan(Stats);
        Assert.StartsWith("all ", plan.Note);   // "all N matched mods within ±20%"
        var steps = Steps(plan);
        Assert.All(steps[..^1], p => Assert.True(p.Comparable));
        Assert.False(steps[^1].Comparable);   // "all rare X — mods not compared" doesn't price the item
    }

    [Fact]
    public void Rares_searched_by_base_alone_are_not_comparables()
    {
        Assert.False(PriceCheck.For(TopBoots with { Identified = false }, null, EsEvBoots).Plan(null).Comparable);
        Assert.False(PriceCheck.For(TopBoots with { Complete = false }, null, EsEvBoots).Plan(null).Comparable);
        Assert.True(PriceCheck.For(new("Exalted Orb", POE2Radar.Core.Game.Poe2Live.Rarity.Normal, true, [], true), null).Plan(null).Comparable);
    }

    [Fact]
    public async Task A_few_real_comparables_beat_hundreds_of_unrelated_listings()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new PriceBook.Snapshot
            {
                League = "Test", FetchedUtc = DateTime.UtcNow, ExPerDivine = 200, ExPerChaos = 10,
                ByName = new() { ["Divine Orb"] = new("Divine Orb", 200, 0, "Currency") },
            }));
            var prices = new PriceBook(path, "Test");
            // Full criteria: 2, top 60%: 1, top two: 0 — the base-only step (400) must not win.
            var handler = new CountingHandler(2, 1, 0, 400);
            using var http = new HttpClient(handler);
            var service = new TradeComparison(http) { MinRequestGap = TimeSpan.Zero };
            var (key, _, plan) = PriceCheck.For(TopBoots, null, EsEvBoots);
            service.GetOrQueueSearch(key, _ => plan(Stats), needsStats: false, prices);
            await service.Pending;
            var done = service.GetOrQueueSearch(key, _ => plan(Stats), needsStats: false, prices);
            Assert.True(done.Comparable);
            Assert.Equal(2, done.Total);
            Assert.DoesNotContain("Closest match", done.Note);   // step 1 won
            Assert.Equal(3, handler.Searches);   // the base-only step is never searched once a comparable exists
        }
        finally { File.Delete(path); }
    }

    private sealed class CountingHandler(params int[] totals) : HttpMessageHandler
    {
        public int Searches;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json;
            if (request.Method == HttpMethod.Post)
            {
                var total = totals[Math.Min(Searches++, totals.Length - 1)];
                var ids = string.Join(",", Enumerable.Range(0, Math.Min(total, 10)).Select(i => $"\"r{i}\""));
                json = $$"""{"id":"q{{Searches}}","total":{{total}},"result":[{{ids}}]}""";
            }
            else json = """{"result":[{"listing":{"price":{"amount":3,"currency":"exalted"},"account":{"name":"A"}}}]}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
