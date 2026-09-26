using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class TradeHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "poe2trade-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sub", "trades.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static TradeSession Done(string player, TradeDirection dir, string item, double amount, string currency, DateTime utc) =>
        new(1, dir, player, null, new TradeRequest(item, amount, currency, "Standard"), utc, utc, TradeState.Completed, true, [], 0, utc);

    [Fact]
    public void Records_completed_sessions_and_persists_across_instances()
    {
        var utc = new DateTime(2025, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        var h = new TradeHistory(FilePath);
        var rec = h.Add(Done("Bob", TradeDirection.Incoming, "Foo", 1, "Divine Orb", utc));
        Assert.NotNull(rec);
        Assert.Equal(TradeSide.Sold, rec!.Direction);
        h.Add(Done("Amy", TradeDirection.Outgoing, "Bar", 5, "Exalted Orb", utc.AddMinutes(1)));
        Assert.Null(h.Add(Done("Zed", TradeDirection.Incoming, "Baz", 1, "Chaos Orb", utc) with { State = TradeState.InArea }));
        Assert.True(File.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));

        var reloaded = new TradeHistory(FilePath);
        var rows = reloaded.Records();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Amy", rows[0].Player); // newest first
        Assert.Equal(TradeSide.Bought, rows[0].Direction);
        Assert.Equal(rec.Id, rows[1].Id);
        Assert.Equal(utc, rows[1].Utc);
        Assert.Contains("\"Sold\"", File.ReadAllText(FilePath)); // enums as strings for hand-editing
    }

    [Fact]
    public void Summary_aggregates_per_currency_and_exalted_with_unconverted_count()
    {
        var utc = new DateTime(2025, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        var h = new TradeHistory(FilePath);
        h.Add(Done("A", TradeDirection.Incoming, "x", 1, "Divine Orb", utc.AddDays(-2))); // before cutoff
        h.Add(Done("B", TradeDirection.Incoming, "x", 2, "Divine Orb", utc));
        h.Add(Done("C", TradeDirection.Incoming, "x", 10, "Exalted Orb", utc));
        h.Add(Done("D", TradeDirection.Outgoing, "x", 30, "Exalted Orb", utc));
        h.Add(Done("E", TradeDirection.Incoming, "x", 3, "Weird Shard", utc));
        h.Add(Done("F", TradeDirection.Incoming, "x", 0, "", utc)); // unpriced

        var sum = h.Summary(utc.AddDays(-1), (cur, amt) => cur switch
        {
            "Divine Orb" => amt * 100,
            "Exalted Orb" => amt,
            _ => null,
        });
        Assert.Equal(4, sum.Sold);
        Assert.Equal(1, sum.Bought);
        Assert.Equal(2, sum.EarnedByCurrency["Divine Orb"]);
        Assert.Equal(10, sum.EarnedByCurrency["Exalted Orb"]);
        Assert.Equal(3, sum.EarnedByCurrency["Weird Shard"]);
        Assert.Equal(30, sum.SpentByCurrency["Exalted Orb"]);
        Assert.Equal(210, sum.EarnedExalted);
        Assert.Equal(30, sum.SpentExalted);
        Assert.Equal(180, sum.ProfitExalted);
        Assert.Equal(1, sum.Unconverted);
    }

    [Fact]
    public void Delete_and_clear_persist()
    {
        var h = new TradeHistory(FilePath);
        var a = h.Add(Done("A", TradeDirection.Incoming, "x", 1, "Divine Orb", DateTime.UtcNow))!;
        h.Add(Done("B", TradeDirection.Incoming, "x", 1, "Divine Orb", DateTime.UtcNow));
        var gen = h.Generation;
        Assert.True(h.Delete(a.Id));
        Assert.False(h.Delete(a.Id));
        Assert.NotEqual(gen, h.Generation);
        Assert.Equal("B", Assert.Single(new TradeHistory(FilePath).Records()).Player);
        h.Clear();
        Assert.Empty(new TradeHistory(FilePath).Records());
    }

    [Fact]
    public void Caps_to_newest_records()
    {
        var h = new TradeHistory(FilePath);
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < TradeHistory.MaxRecords + 3; i++)
            h.Add(new TradeRecord("id" + i, t0.AddSeconds(i), TradeSide.Sold, "P", "x", 1, "Exalted Orb", "Standard"));
        Assert.Equal(TradeHistory.MaxRecords, h.Count);
        Assert.Equal("id3", h.Records()[^1].Id);
    }

    [Fact]
    public void Corrupt_file_loads_empty_and_is_kept_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var h = new TradeHistory(FilePath);
        Assert.Equal(0, h.Count);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Wires_to_session_completion()
    {
        var h = new TradeHistory(FilePath);
        var s = new TradeSessions(new POE2Radar.Overlay.Config.TradeSettings());
        s.Completed += x => h.Add(x);
        var t = DateTime.Now;
        s.Consume(new WhisperReceived(t, "Bob", null, "Hi, I would like to buy your Foo listed for 1 divine in Standard"));
        s.Consume(new PlayerJoined(t, "Bob"));
        s.Consume(new TradeAccepted(t));
        var r = Assert.Single(h.Records());
        Assert.Equal(("Bob", "Foo", 1.0, "Divine Orb", TradeSide.Sold), (r.Player, r.Item, r.Amount, r.Currency, r.Direction));
    }
}
