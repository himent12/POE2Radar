using System.Net;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Pricing;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

public sealed class PriceCheckTests
{
    private static readonly TradeComparison.TradeStat[] Stats =
        [new("explicit.life", "# to maximum Life"), new("explicit.fire", "#% to Fire Resistance")];

    private static ItemTradeProfile Rare(params string[] mods) => new("Gold Ring", Poe2Live.Rarity.Rare, true,
        mods.Select(m => new ItemTradeMod("explicit", m)).ToArray(), true);

    private static string Fmt(double ex) => $"{ex:0.#} ex";

    [Fact]
    public void Rares_search_a_ladder_from_exact_rolls_down_to_the_closest_items_of_that_slot()
    {
        var (_, needsStats, plan) = PriceCheck.For(Rare("+70 to maximum Life", "+25% to Fire Resistance"), null, "Metadata/Items/Rings/FourRing3");
        Assert.True(needsStats);
        var steps = new List<QueryPlan>();
        for (var p = plan(Stats); p is not null; p = p.Next) steps.Add(p);
        Assert.Equal(5, steps.Count);
        Assert.Contains("all 2 matched mods within ±20%", steps[0].Note);
        Assert.Contains("Gold Ring with 2+ of your 2 mods", steps[1].Note);
        Assert.Contains("any ring with 2+", steps[2].Note);
        Assert.Contains("any ring with 1+", steps[3].Note);
        Assert.Contains("mods not compared", steps[4].Note);

        using (var exact = JsonDocument.Parse(steps[0].Query!))
        {
            var group = exact.RootElement.GetProperty("query").GetProperty("stats")[0];
            Assert.Equal("and", group.GetProperty("type").GetString());
            Assert.Equal(56, group.GetProperty("filters")[0].GetProperty("value").GetProperty("min").GetDouble());
            Assert.Equal(84, group.GetProperty("filters")[0].GetProperty("value").GetProperty("max").GetDouble());
        }
        using (var slot = JsonDocument.Parse(steps[2].Query!))
        {
            var q = slot.RootElement.GetProperty("query");
            Assert.False(q.TryGetProperty("type", out _));   // any base of the slot
            Assert.Equal("accessory.ring", q.GetProperty("filters").GetProperty("type_filters").GetProperty("filters")
                .GetProperty("category").GetProperty("option").GetString());
            var group = q.GetProperty("stats")[0];
            Assert.Equal("count", group.GetProperty("type").GetString());
            Assert.Equal(2, group.GetProperty("value").GetProperty("min").GetInt32());
            Assert.False(group.GetProperty("filters")[0].GetProperty("value").TryGetProperty("max", out _)); // better rolls count too
        }
        using (var loose = JsonDocument.Parse(steps[4].Query!))
            Assert.False(loose.RootElement.GetProperty("query").TryGetProperty("stats", out _));
    }

    [Fact]
    public void Range_mods_match_by_their_average_and_unmatchable_lines_are_reported()
    {
        TradeComparison.TradeStat[] stats = [new("explicit.fire", "Adds # to # Fire Damage"), new("explicit.life", "# to maximum Life")];
        var m = TradeComparison.MatchStats(Rare("Adds 10 to 20 Fire Damage", "+70 to maximum Life", "Weird unknown mod"), stats, out var ignored);
        Assert.Equal(2, m.Count);
        Assert.Equal(15, m[0].Value);
        Assert.Equal(1, ignored);
        var plan = PriceCheck.For(Rare("Weird unknown mod"), null).Plan(stats);
        Assert.Contains("none of the mods could be matched", plan.Note);
    }

    [Theory]
    [InlineData("Metadata/Items/Weapons/TwoHandWeapons/Bows/FourBow7Cruel", "weapon.bow")]
    [InlineData("Metadata/Items/Armours/Helmets/FourHelmetStrDex4Cruel", "armour.helmet")]
    [InlineData("Metadata/Items/Quivers/FourQuiver8", "armour.quiver")]
    [InlineData("Metadata/Items/Amulets/FourAmulet9", "accessory.amulet")]
    [InlineData("Metadata/Items/Maps/MapKeyTier6", "map.waystone")]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/OneHandSpears/FourSpear8Endgame", "weapon.spear")]
    [InlineData("Metadata/Items/Armours/Focii/FourFocus10Endgame", "armour.focus")]
    [InlineData("Metadata/Items/Weapons/TwoHandWeapons/Staves/FourQuarterstaff10", "weapon.warstaff")]   // not a staff
    [InlineData("Metadata/Items/Weapons/TwoHandWeapons/Staves/FourStaff9", "weapon.staff")]
    [InlineData("Metadata/Items/Armours/Shields/FourShieldDex10", "armour.buckler")]                     // not a shield
    [InlineData("Metadata/Items/Armours/Shields/FourShieldStr10", "armour.shield")]
    [InlineData("Metadata/Items/Armours/Shields/FourShieldDex99NewPatch", "armour.buckler")]   // not in the table: path fallback
    [InlineData("Metadata/Items/Armours/Focus/FourFocus99NewPatch", "armour.focus")]
    [InlineData("Metadata/Items/Something/New", null)]
    public void Maps_item_metadata_to_the_trade_category(string metadata, string? expected)
        => Assert.Equal(expected, PriceCheck.Category(metadata)?.Id);

    [Fact]
    public void Comparable_rares_are_priced_at_the_cheapest_genuine_ask()
    {
        // Comparables are "at least about as good": a buyer takes the cheapest, so that's the list price...
        var listings = new[] { 4.0, 5, 9, 10, 11 }.Select(v => new MarketListing(v, "exalted", v, "s" + v, null, 1)).ToArray();
        var s = PriceCheck.Summarize(listings, null, Fmt, similar: true);
        Assert.Equal(4, s.Suggested);
        Assert.Equal("Comparable items from 4 ex, typically 9 ex · list at ~4 ex", s.Verdict);

        // ...unless the cheapest is a lone ask under half the next one (typo, bait, a worse item that slipped through).
        var outlier = new[] { 1.0, 3, 3, 10, 11 }.Select(v => new MarketListing(v, "exalted", v, "s" + v, null, 1)).ToArray();
        s = PriceCheck.Summarize(outlier, null, Fmt, similar: true);
        Assert.Equal(3, s.Suggested);
        Assert.Contains("one ask at 1 ex looks like a mistake", s.Verdict);
        Assert.Null(PriceCheck.Summarize(outlier, 20, Fmt).Suggested);   // poe.ninja-priced items keep the undercut verdict

        var fixers = new[] { "aug", "transmute", "aug", "exalted", "regal" }.Select((c, i) => new MarketListing(1, c, 1 + i, "s" + i, null, 1)).ToArray();
        Assert.Contains("possible price-fixing", PriceCheck.Summarize(fixers, null, Fmt, similar: true).Verdict);
        Assert.DoesNotContain("price-fixing", s.Verdict);
    }

    [Fact]
    public void Uniques_search_by_name_and_everything_else_by_base_type()
    {
        var unique = new ItemTradeProfile("Heavy Belt", Poe2Live.Rarity.Unique, true, [], true);
        var (key, needs, plan) = PriceCheck.For(unique, "Doomsday");
        Assert.False(needs);
        Assert.StartsWith("unique", key);
        using (var doc = JsonDocument.Parse(plan(null).Query!))
        {
            Assert.Equal("Doomsday", doc.RootElement.GetProperty("query").GetProperty("name").GetString());
            Assert.Equal("unique", doc.RootElement.GetProperty("query").GetProperty("filters").GetProperty("type_filters")
                .GetProperty("filters").GetProperty("rarity").GetProperty("option").GetString());
        }
        var currency = new ItemTradeProfile("Exalted Orb", Poe2Live.Rarity.Normal, true, [], true);
        var c = PriceCheck.For(currency, null);
        Assert.Equal("Same base type", c.Plan(null).Note);
        var unid = PriceCheck.For(Rare() with { Identified = false }, null).Plan(null);
        Assert.Contains("Unidentified", unid.Note);
    }

    [Fact]
    public void Verdict_compares_the_cheapest_ask_with_poe_ninja_and_suggests_an_undercut()
    {
        var listings = new[] { 10.0, 12, 15, 40 }.Select(v => new MarketListing(v, "exalted", v, "s" + v, null, 1)).ToArray();
        var s = PriceCheck.Summarize(listings, 20, Fmt);
        Assert.Equal(10, s.Min);
        Assert.Equal(13.5, s.Median);
        Assert.Contains("50% below poe.ninja", s.Verdict);
        Assert.Contains("~9.5 ex", s.Verdict);

        Assert.Contains("in line with", PriceCheck.Summarize(listings, 10.2, Fmt).Verdict);
        Assert.Contains("rough guide", PriceCheck.Summarize(listings[..1], null, Fmt).Verdict);
        Assert.Contains("poe.ninja values it at 20 ex", PriceCheck.Summarize([], 20, Fmt).Verdict);
        Assert.Equal("No market data for this item yet", PriceCheck.Summarize([], null, Fmt).Verdict);
        // Listings in unknown currencies are shown but can't be placed on the price scale.
        Assert.Empty(PriceCheck.Summarize([new MarketListing(3, "mystery", null, "x", null, 1)], null, Fmt).Points);
    }

    [Fact]
    public void Parses_listing_price_seller_age_and_stack()
    {
        var snap = new PriceBook.Snapshot { League = "T", ExPerDivine = 200, ExPerChaos = 10,
            ByName = new() { ["Regal Orb"] = new("Regal Orb", 0.5, 0, "Currency") } };
        using var doc = JsonDocument.Parse("""
            {"result":[
              {"listing":{"price":{"amount":2,"currency":"divine"},"account":{"name":"A"},"indexed":"2026-09-24T10:00:00Z"},"item":{"stackSize":20}},
              {"listing":{"price":{"amount":4,"currency":"regal"},"account":{"name":"B"}}},
              {"listing":{"price":{"amount":1,"currency":"weird"},"account":{"name":"C"}}},
              {"listing":{"account":{"name":"D"}}}, null]}
            """);
        var l = TradeComparison.ParseListings(doc.RootElement, snap);
        Assert.Equal(3, l.Count);
        Assert.Equal(400, l[0].Exalted);
        Assert.Equal(20, l[0].Stack);
        Assert.Equal(new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc), l[0].Indexed);
        Assert.Equal(2, l[1].Exalted);
        Assert.Null(l[2].Exalted);
    }

    [Fact]
    public async Task Explicit_search_returns_cheapest_listings_per_seller_and_caches()
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
            var handler = new Handler();
            using var http = new HttpClient(handler);
            var service = new TradeComparison(http) { MinRequestGap = TimeSpan.Zero };
            var (key, needs, plan) = PriceCheck.For(new ItemTradeProfile("Heavy Belt", Poe2Live.Rarity.Unique, true, [], true), "Doomsday");
            var first = service.GetOrQueueSearch(key, plan, needs, prices);
            Assert.True(first.Pending);
            await service.Pending;
            var done = service.GetOrQueueSearch(key, plan, needs, prices);
            Assert.False(done.Pending);
            Assert.Equal(57, done.Total);
            Assert.Equal(["A", "B"], done.Listings.Select(l => l.Seller));   // duplicate seller dropped
            Assert.EndsWith("/Test/query-id", done.Url);
            Assert.Same(done, service.GetOrQueueSearch(key, plan, needs, prices));
            Assert.Equal(2, handler.Calls);   // search + fetch, then cached
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Price_check_hotkey_is_sanitized_and_blocks_command_bindings()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.True(ApiServer.TryParseHoverPrice(J("""{"enabled":true,"priceCheckHotkey":"alt+q"}"""), out var hp));
        Assert.Equal("Alt+Q", hp.PriceCheckHotkey);
        Assert.True(ApiServer.TryParseHoverPrice(J("""{"priceCheckHotkey":"F8"}"""), out hp));
        Assert.Equal("Ctrl+D", hp.PriceCheckHotkey);
        Assert.True(ApiServer.TryParseCommands(J("""{"commands":[{"name":"x","hotkey":"Alt+Q","text":"/x"}],"bookmarks":[]}"""),
            "F4", out var c, "Alt+Q"));
        Assert.Equal("", c.Commands[0].Hotkey);
    }

    [Theory]
    [InlineData(2000.0, 200.0, false, "Jackpot")]   // 10 div
    [InlineData(999.0, 200.0, false, "List it")]    // just under 5 div
    [InlineData(1.0, 200.0, false, "List it")]
    [InlineData(0.4, 200.0, false, "Vendor")]
    [InlineData(1000.0, 0.0, false, "Jackpot")]     // no divine rate yet: fixed 1000 ex bar
    [InlineData(null, 200.0, true, "Reading")]
    [InlineData(null, 200.0, false, "Unknown")]
    public void Tier_names_the_worth(double? worthEx, double exPerDivine, bool loading, string expected)
        => Assert.Equal(expected, PriceCheck.Tier(worthEx, exPerDivine, loading));

    [Fact]
    public void Confidence_reflects_listing_count_and_spread()
    {
        Assert.Equal("None", OverlayRenderer.Confidence([]).Word);
        Assert.Equal("Low", OverlayRenderer.Confidence([10, 12]).Word);
        Assert.Equal("Scattered", OverlayRenderer.Confidence([1, 2, 30]).Word);
        Assert.Equal("Good", OverlayRenderer.Confidence([10, 12, 14]).Word);
        Assert.Equal("High", OverlayRenderer.Confidence([10, 11, 12, 13, 15, 20]).Word);
    }

    [Fact]
    public void Panel_renders_listings_verdict_and_buttons()
    {
        using var window = OverlayWindow.CreateHeadless(1280, 800);
        using var renderer = new OverlayRenderer(window);
        var view = new PriceCheckView(900, 420, 48, 96, "Doomsday", "Heavy Belt", 0xAF6025,
            ["+64 to maximum Life", "+31% to Cold Resistance", "12% increased Rarity of Items found"],
            "18 ex", "poe.ninja · 412 listings · +6% trend", "57 listed online", "Same unique · rolls not compared", false, null,
            [new("14 ex", "", "Zed_Ex", "3m"), new("15 ex", "", "MapLord", "22m"), new("0.1 div", "~20 ex", "Kalguuran", "2h"),
             new("22 ex", "", "BuyerOne", "5h")],
            [14, 15, 20, 22], 18, "14 ex", "17.5 ex", "Cheapest ask is 22% below poe.ninja · list at ~13.3 ex to sell fast",
            "https://www.pathofexile.com/trade2/search/poe2/Standard/abc", "Standard", "List it");
        renderer.RenderPriceCheckPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null, PriceCheck = view });
        var actions = renderer.LegendRowRects.Select(r => r.Action).ToList();
        Assert.Contains("pc:trade", actions);
        Assert.Contains("pc:refresh", actions);
        Assert.Contains("pc:close", actions);
        var dir = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "price-check.png"), window.SnapshotPng());
            renderer.RenderPriceCheckPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null, PriceCheck = view with
            {
                Loading = true, Rows = [], Points = [], MinText = null, MedianText = null, Status = "Searching the trade site…",
                Name = "Gold Ring", BaseLine = "Rare", RarityRgb = 0xE6D36A, Estimate = null,
                EstimateSub = "No reference price — rares are priced by their rolls", EstimateEx = null, Tier = "Reading",
            } });
            File.WriteAllBytes(Path.Combine(dir, "price-check-loading.png"), window.SnapshotPng());
            renderer.RenderPriceCheckPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null, PriceCheck = RareView(view) });
            File.WriteAllBytes(Path.Combine(dir, "price-check-rare.png"), window.SnapshotPng());
        }
        renderer.RenderPriceCheckPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null, PriceCheck = RareView(view) });
        Assert.Contains("pc:trade", renderer.LegendRowRects.Select(r => r.Action));
    }

    /// <summary>A rare boots appraisal as <c>RadarApp.BuildPriceCheckView</c> formats it.</summary>
    private static PriceCheckView RareView(PriceCheckView unique)
    {
        var a = ItemAppraiser.Appraise(ItemAppraisalTests.TopBoots, ItemAppraisalTests.EsEvBoots)!;
        return unique with
        {
            Name = "Doom Stride", BaseLine = "Rare", RarityRgb = 0xE8D36A, Estimate = null, EstimateEx = null,
            EstimateSub = "No reference price — rares are priced against comparable listings",
            Mods = a.Affixes.Select(x => $"T{x.Tier} · {x.Text}").ToList(),
            ModWeights = a.Affixes.Select(x => x.Role >= AffixRole.Key && x.Quality >= 0.6 ? 2 : x.Role >= AffixRole.Useful ? 1 : 0).ToList(),
            Appraisal = RadarApp.AppraisalLine(a), Worth = "2.4 div", WorthSource = "cheapest comparable",
            Status = "302 listed online", Note = "any boots with at least your 35% move speed, 125% res, +142 life",
            Verdict = "Comparable items from 2.4 div, typically 4 div · list at ~2.4 div", Tier = "List it",
        };
    }

    [Fact]
    public async Task Ladder_skips_steps_with_too_few_results_and_fetches_only_the_chosen_one()
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
            var handler = new LadderHandler();
            using var http = new HttpClient(handler);
            var service = new TradeComparison(http) { MinRequestGap = TimeSpan.Zero };
            var (key, needs, plan) = PriceCheck.For(Rare("+70 to maximum Life", "+25% to Fire Resistance"), null, "Metadata/Items/Rings/FourRing3");
            service.GetOrQueueSearch(key, _ => plan(Stats), false, prices);
            await service.Pending;
            var done = service.GetOrQueueSearch(key, _ => plan(Stats), false, prices);
            Assert.Equal(4, done.Total);
            Assert.StartsWith("Closest match — ", done.Note);
            Assert.Contains("any ring", done.Note);
            Assert.Equal(3, handler.Searches);   // exact (0) → same base (2, too few) → any ring (4)
            Assert.Equal(1, handler.Fetches);
        }
        finally { File.Delete(path); }
    }

    private sealed class LadderHandler : HttpMessageHandler
    {
        public int Searches, Fetches;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json;
            if (request.Method == HttpMethod.Post)
            {
                var total = ++Searches switch { 1 => 0, 2 => 2, _ => 4 };
                json = $$"""{"id":"q{{Searches}}","total":{{total}},"result":[{{string.Join(",", Enumerable.Range(0, total).Select(i => $"\"r{i}\""))}}]}""";
            }
            else
            {
                Fetches++;
                json = """{"result":[{"listing":{"price":{"amount":3,"currency":"exalted"},"account":{"name":"A"}}}]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var json = request.Method == HttpMethod.Post
                ? """{"id":"query-id","total":57,"result":["a","b","c"]}"""
                : """{"result":[{"listing":{"price":{"amount":14,"currency":"exalted"},"account":{"name":"A"}}},{"listing":{"price":{"amount":15,"currency":"exalted"},"account":{"name":"A"}}},{"listing":{"price":{"amount":1,"currency":"divine"},"account":{"name":"B"}}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
