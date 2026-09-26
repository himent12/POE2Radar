using System.Net;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Pricing;
using Xunit;

namespace POE2Radar.Tests;

public sealed class TradeComparisonTests
{
    private static ItemTradeProfile Ring => new("Gold Ring", Poe2Live.Rarity.Rare, true,
        [new("explicit", "+70 to maximum Life"), new("explicit", "+25% to Fire Resistance")], true);
    private static TradeComparison.TradeStat[] Stats =>
        [new("explicit.life", "# to maximum Life"), new("explicit.fire", "#% to Fire Resistance")];

    [Fact]
    public void Matches_mods_to_trade_stats_by_text_and_refuses_ambiguous_ones()
    {
        var m = TradeComparison.MatchStats(Ring, Stats, out var ignored);
        Assert.Equal(["explicit.life", "explicit.fire"], m.Select(x => x.Id));
        Assert.Equal([70.0, 25.0], m.Select(x => x.Value!.Value));
        Assert.Equal(0, ignored);
        Assert.Empty(TradeComparison.MatchStats(Ring with { Mods = [] }, Stats, out _));
        var amb = TradeComparison.MatchStats(Ring, [.. Stats, new("explicit.other", "# to maximum Life")], out ignored);
        Assert.Single(amb);
        Assert.Equal(1, ignored);
    }

    [Fact]
    public async Task Rate_limit_is_reported_and_not_retried_on_every_lookup()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new PriceBook.Snapshot
            {
                League = "Test", FetchedUtc = DateTime.UtcNow, ExPerDivine = 200,
                ByName = new() { ["Divine Orb"] = new("Divine Orb", 200, 0, "Currency") },
            }));
            var handler = new Handler();
            using var http = new HttpClient(handler);
            var service = new TradeComparison(http);
            var prices = new PriceBook(path, "Test");
            var (key, needs, plan) = PriceCheck.For(Ring, null);
            service.GetOrQueueSearch(key, plan, needs, prices);
            await service.Pending;
            Assert.NotNull(service.GetOrQueueSearch(key, plan, needs, prices).Error);
            var other = PriceCheck.For(Ring with { Name = "Sapphire Ring" }, null);
            for (var i = 0; i < 20; i++)
                Assert.Contains("asked us to wait", service.GetOrQueueSearch(other.Key, other.Plan, other.NeedsStats, prices).Status);
            Assert.Equal(1, handler.Calls);
        }
        finally { File.Delete(path); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }
    }
}
