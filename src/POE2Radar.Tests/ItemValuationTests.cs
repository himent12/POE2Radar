using System.Net;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Pricing;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ItemValuationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "poe-prices-" + Guid.NewGuid());
    private string CachePath => Path.Combine(_directory, "prices.json");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class MarketHandler : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        public bool ExaltedPrimary;
        public bool MissingRates;
        public bool Conditional;
        public TaskCompletionSource? Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentBag<string> Categories = [];
        public int DelayMs;
        public int Active, Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            int prior;
            do { prior = Peak; } while (active > prior && Interlocked.CompareExchange(ref Peak, active, prior) != prior);
            try
            {
            if (DelayMs > 0) await Task.Delay(DelayMs, token);
            Started.TrySetResult();
            if (Block != null) await Block.Task;
            if (Fail)
            {
                var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                failure.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
                return failure;
            }
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                Conditional = true;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/leagues"))
                return Reply("""[{"id":"Test HC","name":"Test HC"}]""");
            var type = request.RequestUri.Query.Split("type=")[1];
            Categories.Add(type);
            var core = MissingRates ? """{"primary":"divine"}""" : ExaltedPrimary
                ? """{"primary":"exalted","rates":{"divine":0.005,"chaos":0.1},"items":[{"id":"exalted","name":"Exalted Orb"}]}"""
                : """{"primary":"divine","rates":{"exalted":200,"chaos":20},"items":[{"id":"exalted","name":"Exalted Orb"}]}""";
            // Stash overview cache can carry an older rate than the currency overview.
            if (type == "UniqueCharms" && !ExaltedPrimary) core = core.Replace("200", "100");
            var lines = type == "Currency"
                ? """[{"id":"exalted","primaryValue":1,"volumePrimaryValue":9876,"sparkline":{"totalChange":-12.5}}]"""
                : type == "UniqueCharms"
                ? """[{"id":1,"name":"Test Charm","primaryValue":5,"listingCount":100,"icon":"https://example.test/Charm.png","variant":"normal"},{"id":2,"name":"Test Charm","primaryValue":999,"listingCount":1,"icon":"https://example.test/Charm.png","variant":"perfect"},{"id":3,"name":"Other Charm","primaryValue":1,"listingCount":10,"icon":"https://example.test/Shared.png"},{"id":4,"name":"Different Charm","primaryValue":2,"listingCount":20,"icon":"https://example.test/Shared.png"}]"""
                : "[]";
            return Reply("{\"core\":" + core + ",\"lines\":" + lines + "}");
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        private static HttpResponseMessage Reply(string body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new("\"test-v1\"");
            return response;
        }
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 1)]
    public async Task Uses_response_reference_currency_and_core_metadata(bool primaryExalted, double expected)
    {
        var handler = new MarketHandler { ExaltedPrimary = primaryExalted };
        using var http = new HttpClient(handler);
        var book = new PriceBook(CachePath, http: http);
        await book.RefreshAsync();
        Assert.True(book.IsLoaded, book.Status);
        Assert.Equal("Test HC", book.League);
        Assert.Equal(expected, book.TryByName("exalted orb")!.Value.Exalted);
        Assert.Equal(200, book.ExPerDivine);
        Assert.Equal(10, book.ExPerChaos);
        Assert.Contains("UniqueSanctumRelics", handler.Categories);
        Assert.Contains("UniqueCharms", handler.Categories);
        Assert.False(book.TryByName("Exalted Orb")!.Value.LowConfidence(100));
    }

    [Fact]
    public async Task Chooses_representative_variant_and_rejects_ambiguous_art()
    {
        using var http = new HttpClient(new MarketHandler());
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        var price = book.TryByArt("Charm")!.Value;
        Assert.Equal(1000, price.Exalted);
        Assert.Equal("normal", price.Variant);
        Assert.Null(book.TryByArt("Shared"));
        var hover = HoverValuation.Build(Item(Poe2Live.Rarity.Unique, "Golden Charm", "Charm", identified: false), book, 101, 10);
        Assert.Contains("LOW CONFIDENCE", hover.Detail);
        Assert.Contains("identified reference", hover.Detail);
        Assert.Contains("rolls may change value", hover.Detail);
    }

    [Fact]
    public async Task Stack_total_and_no_fabricated_rare_value()
    {
        using var http = new HttpClient(new MarketHandler());
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        var stack = HoverValuation.Build(Item(Poe2Live.Rarity.Normal, "Exalted Orb", stack: 3), book, 10, 400);
        Assert.Equal("Estimated stack: 3 div", stack.Text);
        Assert.Contains("3 × 1 div each", stack.Detail);
        Assert.True(stack.Highlight);
        var rare = HoverValuation.Build(Item(Poe2Live.Rarity.Rare, "Exalted Orb"), book, 10, 0);
        Assert.Equal("Reading item modifiers", rare.Text);
        Assert.False(rare.Highlight);
        Assert.NotNull(rare.TradeUrl);
        var unknown = HoverValuation.Build(Item(Poe2Live.Rarity.Normal, "Not listed"), book, 10, 0);
        Assert.Equal("No market estimate", unknown.Text);
    }

    [Fact]
    public async Task Rate_limit_backs_off_without_hammering_on_every_tick()
    {
        var handler = new MarketHandler { Fail = true };
        using var http = new HttpClient(handler);
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        for (int i = 0; i < 50; i++) await book.RefreshAsync(force: true);
        Assert.Equal(1, handler.Calls);
        Assert.False(book.IsLoaded);
        Assert.Contains("rate limited", book.Status);
    }

    [Fact]
    public async Task Missing_conversion_never_publishes_assumed_prices()
    {
        using var http = new HttpClient(new MarketHandler { MissingRates = true });
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        Assert.False(book.IsLoaded);
        Assert.Contains("Missing currency conversion", book.Status);
    }

    [Fact]
    public async Task League_change_discards_inflight_response_and_coalesces_refreshes()
    {
        var handler = new MarketHandler { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler);
        var book = new PriceBook(CachePath, "Old", http);
        var first = book.RefreshAsync();
        await handler.Started.Task;
        Assert.Same(first, book.RefreshAsync());
        book.SetLeagueOverride("New");
        handler.Block.SetResult();
        await first;
        Assert.False(book.IsLoaded);
        await book.RefreshAsync();
        Assert.Equal("New", book.League);
        Assert.True(book.IsLoaded);
        book.SetDetectedLeague("Another");
        Assert.Equal("New", book.League); // explicit setting takes precedence
        book.SetLeagueOverride(null);
        Assert.False(book.IsLoaded);
    }

    [Fact]
    public async Task Conditional_requests_reuse_responses_and_cache_is_league_scoped()
    {
        var handler = new MarketHandler();
        using var http = new HttpClient(handler);
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        book.SetLeagueOverride("Other");
        book.SetLeagueOverride("Test");
        await book.RefreshAsync();
        Assert.True(handler.Conditional);
        Assert.True(book.IsLoaded);
        Assert.True(new PriceBook(CachePath, "Test", http).IsLoaded);
        Assert.False(new PriceBook(CachePath, "Different", http).IsLoaded);
        var cached = JsonSerializer.Deserialize<PriceBook.Snapshot>(File.ReadAllText(CachePath))!;
        cached.FetchedUtc = DateTime.UtcNow.AddHours(-3);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(cached));
        var stale = new PriceBook(CachePath, "Test", http);
        var hover = HoverValuation.Build(Item(Poe2Live.Rarity.Unique, "Charm", "Charm"), stale, 2, 1);
        Assert.Contains("STALE cache", hover.Detail);
        Assert.False(hover.Highlight);
        handler.Fail = true;
        await stale.RefreshAsync();
        Assert.True(stale.IsLoaded);
        Assert.True(stale.IsStale);
        Assert.Equal(cached.FetchedUtc, stale.LastFetchUtc);
    }

    [Fact]
    public void Trade_link_encodes_names_league_and_rarity_without_invented_mod_filters()
    {
        var url = HoverValuation.TradeSearch("HC Test & More", "Omen's Ring", "A \"Unique\"", Poe2Live.Rarity.Unique)!;
        Assert.StartsWith("https://www.pathofexile.com/trade2/search/poe2/HC%20Test%20%26%20More?q=", url);
        using var payload = JsonDocument.Parse(Uri.UnescapeDataString(url.Split("?q=")[1]));
        var query = payload.RootElement.GetProperty("query");
        Assert.Equal("Omen's Ring", query.GetProperty("type").GetString());
        Assert.Equal("A \"Unique\"", query.GetProperty("name").GetString());
        Assert.False(query.TryGetProperty("stats", out _));
        Assert.Null(HoverValuation.TradeSearch("", "Ring", null, Poe2Live.Rarity.Rare));
    }

    [Fact]
    public async Task Fetches_two_at_a_time_and_retains_all_variant_and_volume_evidence()
    {
        var handler = new MarketHandler { DelayMs = 5 };
        using var http = new HttpClient(handler);
        var book = new PriceBook(CachePath, "Test", http);
        await book.RefreshAsync();
        Assert.True(book.IsLoaded, book.Status);
        Assert.Equal(2, handler.Peak);
        Assert.Equal(23, handler.Categories.Count);
        Assert.Equal(23, book.CategoryCount);
        Assert.Equal(5, book.MarketRowCount);
        Assert.True(book.FetchMilliseconds > 0);
        var currency = book.TryByName("Exalted Orb")!.Value;
        Assert.Equal(-12.5, currency.TrendPercent);
        Assert.Equal(9876 * 200, currency.TradedVolumeEx);
        var summary = book.Current.RangesByName["Test Charm"];
        Assert.Equal(2, summary.Variants);
        Assert.Equal(101, summary.Listings);
        Assert.Equal(1000, summary.MinExalted);
        Assert.Equal(199800, summary.MaxExalted);
        var hover = HoverValuation.Build(Item(Poe2Live.Rarity.Unique, "Charm", "Charm"), book, 2, 10);
        Assert.Contains("2 market variants", hover.Detail);
        var cached = new PriceBook(CachePath, "Test", http);
        Assert.Equal(2, cached.Current.VariantsByName["test charm"].Count);
        Assert.Equal(currency.TrendPercent, cached.TryByName("Exalted Orb")!.Value.TrendPercent);
    }

    private static Poe2Live.HoveredItem Item(Poe2Live.Rarity rarity, string name, string? art = null, int stack = 1, bool identified = true)
        => new(1, rarity, art, identified, name, stack, 100, 100, 48, 48);
}
