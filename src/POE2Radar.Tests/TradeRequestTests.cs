using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class TradeRequestTests
{
    [Fact]
    public void Parses_item_whisper_with_stash_position()
    {
        var r = TradeRequest.Parse("Hi, I would like to buy your Foo Bar, Siege Axe listed for 1 divine in Standard (stash tab \"sale\"; position: left 3, top 5)");
        Assert.NotNull(r);
        Assert.Equal("Foo Bar, Siege Axe", r!.Item);
        Assert.Equal(1, r.Amount);
        Assert.Equal("Divine Orb", r.Currency);
        Assert.Equal("Standard", r.League);
        Assert.Equal("sale", r.StashTab);
        Assert.Equal(3, r.Left);
        Assert.Equal(5, r.Top);
        Assert.Equal("", r.Note);
        Assert.False(r.Bulk);
    }

    [Fact]
    public void Parses_decimal_amount_multiword_league_and_trailing_note()
    {
        var r = TradeRequest.Parse("Hi, I would like to buy your Tabula Rasa listed for 2.5 exalted in HC Runes of Aldur (stash tab \"~b/o 2.5 exalted\"; position: left 12, top 1) can you do 2?");
        Assert.NotNull(r);
        Assert.Equal(2.5, r!.Amount);
        Assert.Equal("Exalted Orb", r.Currency);
        Assert.Equal("HC Runes of Aldur", r.League);
        Assert.Equal("~b/o 2.5 exalted", r.StashTab);
        Assert.Equal("can you do 2?", r.Note);
    }

    [Fact]
    public void Parses_item_whisper_without_stash_part()
    {
        var r = TradeRequest.Parse("Hi, I would like to buy your Grand Spectrum listed for 30 chaos in Standard");
        Assert.NotNull(r);
        Assert.Equal("Grand Spectrum", r!.Item);
        Assert.Equal(30, r.Amount);
        Assert.Equal("Chaos Orb", r.Currency);
        Assert.Equal("Standard", r.League);
        Assert.Null(r.StashTab);
        Assert.Null(r.Left);

        var withNote = TradeRequest.Parse("Hi, I would like to buy your Grand Spectrum listed for 30 chaos in Standard. still up?");
        Assert.Equal("Standard", withNote!.League);
        Assert.Equal("still up?", withNote.Note);
    }

    [Fact]
    public void Item_name_containing_in_does_not_swallow_the_price()
    {
        var r = TradeRequest.Parse("Hi, I would like to buy your Blood in the Eyes listed for 3 divine in Standard (stash tab \"x\"; position: left 1, top 1)");
        Assert.Equal("Blood in the Eyes", r!.Item);
        Assert.Equal(3, r.Amount);
        Assert.Equal("Standard", r.League);
    }

    [Fact]
    public void Unpriced_listing_parses_with_zero_amount()
    {
        var r = TradeRequest.Parse("Hi, I would like to buy your Foo in Standard (stash tab \"dump\"; position: left 2, top 4)");
        Assert.NotNull(r);
        Assert.Equal("Foo", r!.Item);
        Assert.Equal(0, r.Amount);
        Assert.Equal("", r.Currency);
        Assert.Equal("dump", r.StashTab);
    }

    [Fact]
    public void Parses_bulk_exchange_whisper()
    {
        var r = TradeRequest.Parse("Hi, I'd like to buy your 10 Chaos Orb for my 1 Divine Orb in Standard. quick pls");
        Assert.NotNull(r);
        Assert.True(r!.Bulk);
        Assert.Equal("10 Chaos Orb", r.Item);
        Assert.Equal(1, r.Amount);
        Assert.Equal("Divine Orb", r.Currency);
        Assert.Equal("Standard", r.League);
        Assert.Equal("quick pls", r.Note);

        var noDot = TradeRequest.Parse("Hi, I'd like to buy your 3 Greater Essence of Ice for my 1.5 exalted in Rise of the Abyssal");
        Assert.Equal("3 Greater Essence of Ice", noDot!.Item);
        Assert.Equal(1.5, noDot.Amount);
        Assert.Equal("Exalted Orb", noDot.Currency);
        Assert.Equal("Rise of the Abyssal", noDot.League);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("still selling?")]
    [InlineData("Hi, I would like to buy your")]
    public void Non_trade_whispers_are_null(string? msg) => Assert.Null(TradeRequest.Parse(msg));

    [Theory]
    [InlineData("divine", "Divine Orb")]
    [InlineData("Divine Orb", "Divine Orb")]
    [InlineData("exalted", "Exalted Orb")]
    [InlineData("exalted orb", "Exalted Orb")]
    [InlineData("chaos", "Chaos Orb")]
    [InlineData("chaos orb", "Chaos Orb")]
    [InlineData("alch", "Orb of Alchemy")]
    [InlineData("mirror", "Mirror of Kalandra")]
    [InlineData(" Something Odd ", "Something Odd")]
    public void Normalizes_currency_names(string raw, string expected) =>
        Assert.Equal(expected, TradeRequest.NormalizeCurrency(raw));
}
