using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ClientLogParserTests
{
    private const string Pre = "2025/01/05 12:34:56 123456789 cffb0719 [INFO Client 12345] ";

    [Fact]
    public void Parses_timestamp_as_local_time()
    {
        var e = ClientLogParser.Parse(Pre + ": Trade accepted.");
        Assert.IsType<TradeAccepted>(e);
        Assert.Equal(new DateTime(2025, 1, 5, 12, 34, 56), e!.Time);
        Assert.Equal(DateTimeKind.Local, e.Time.Kind);
    }

    [Fact]
    public void Parses_incoming_whisper_with_and_without_guild()
    {
        var w = Assert.IsType<WhisperReceived>(ClientLogParser.Parse(
            Pre + "@From Buyer_Name: Hi, I would like to buy your Foo listed for 1 divine in Standard (stash tab \"sale\"; position: left 3, top 5)"));
        Assert.Equal("Buyer_Name", w.From);
        Assert.Null(w.Guild);
        Assert.StartsWith("Hi, I would like to buy your Foo", w.Message);

        var g = Assert.IsType<WhisperReceived>(ClientLogParser.Parse(Pre + "@From <GUILDTAG> Buyer_Name: hello"));
        Assert.Equal("GUILDTAG", g.Guild);
        Assert.Equal("Buyer_Name", g.From);
        Assert.Equal("hello", g.Message);
    }

    [Fact]
    public void Parses_outgoing_whisper()
    {
        var w = Assert.IsType<WhisperSent>(ClientLogParser.Parse(Pre + "@To Seller: Hi, I would like to buy your X listed for 2 exalted in Standard"));
        Assert.Equal("Seller", w.To);
        Assert.Equal("Hi, I would like to buy your X listed for 2 exalted in Standard", w.Message);
    }

    [Fact]
    public void Whisper_message_keeps_colons_after_the_first()
    {
        var w = Assert.IsType<WhisperReceived>(ClientLogParser.Parse(Pre + "@From Bob: time: 5 min"));
        Assert.Equal("time: 5 min", w.Message);
    }

    [Fact]
    public void Parses_join_leave_and_area()
    {
        Assert.Equal("Buyer_Name", Assert.IsType<PlayerJoined>(ClientLogParser.Parse(Pre + ": Buyer_Name has joined the area.")).Player);
        Assert.Equal("xX_Buyer", Assert.IsType<PlayerLeft>(ClientLogParser.Parse(Pre + ": xX_Buyer has left the area.")).Player);
        Assert.Equal("Clearfell", Assert.IsType<AreaEntered>(ClientLogParser.Parse(Pre + ": You have entered Clearfell.")).Name);
        Assert.Equal("The Ziggurat Refuge", Assert.IsType<AreaEntered>(ClientLogParser.Parse(Pre + ": You have entered The Ziggurat Refuge.")).Name);
    }

    [Fact]
    public void Parses_trade_and_afk_notices()
    {
        Assert.IsType<TradeAccepted>(ClientLogParser.Parse(Pre + ": Trade accepted."));
        Assert.IsType<TradeCancelled>(ClientLogParser.Parse(Pre + ": Trade cancelled."));
        Assert.True(Assert.IsType<AfkChanged>(ClientLogParser.Parse(Pre + ": AFK mode is now ON. Autoreply \"brb\"")).On);
        Assert.False(Assert.IsType<AfkChanged>(ClientLogParser.Parse(Pre + ": AFK mode is now OFF.")).On);
    }

    [Fact]
    public void Parses_debug_area_generation()
    {
        var g = Assert.IsType<AreaGenerated>(ClientLogParser.Parse(
            "2025/01/05 12:34:56 123456789 cffb0719 [DEBUG Client 12345] Generating level 65 area \"MapHideoutX\" with seed 1234"));
        Assert.Equal(65, g.Level);
        Assert.Equal("MapHideoutX", g.Code);
    }

    [Fact]
    public void Tolerates_missing_middle_columns_and_crlf()
    {
        Assert.IsType<TradeAccepted>(ClientLogParser.Parse("2025/01/05 12:34:56 [INFO Client 1] : Trade accepted.\r\n"));
        Assert.IsType<TradeAccepted>(ClientLogParser.Parse("2025/01/05 12:34:56 42 [WARN Client 1] : Trade accepted."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] #Global_Guy: wts stuff")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] $Trader: wtb")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] %Party: hi")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] &Guildie: hi")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] LocalGuy: hi")]
    [InlineData("2025/01/05 12:34:56 123 abc [INFO Client 1] : Some unknown notice.")]
    [InlineData("2025/01/05 12:34:56 123 abc [DEBUG Client 1] [SCENE] Set Source [Clearfell]")]
    [InlineData("2025/13/45 12:34:56 123 abc [INFO Client 1] : Trade accepted.")]
    public void Unknown_lines_are_null(string line) => Assert.Null(ClientLogParser.Parse(line));

    [Fact]
    public void Chat_line_saying_has_joined_is_not_a_join()
    {
        Assert.Null(ClientLogParser.Parse(Pre + "Bob: Alice has joined the area."));
    }
}
