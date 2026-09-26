using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class TradeSessionsTests
{
    private static readonly DateTime T = new(2025, 1, 5, 12, 0, 0, DateTimeKind.Local);
    private const string Buy = "Hi, I would like to buy your Foo listed for 1 divine in Standard (stash tab \"sale\"; position: left 3, top 5)";
    private const string BuyBar = "Hi, I would like to buy your Bar listed for 5 exalted in Standard (stash tab \"sale\"; position: left 1, top 1)";

    private DateTime _now = new(2025, 1, 5, 11, 0, 0, DateTimeKind.Utc);
    private TradeSessions New(TradeSettings? cfg = null) => new(cfg ?? new TradeSettings(), () => _now);

    [Fact]
    public void Incoming_trade_whisper_creates_session_and_non_trade_does_not()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", "GLD", "hello there"));
        Assert.Empty(s.Snapshot());
        s.Consume(new WhisperReceived(T, "Bob", "GLD", Buy));
        var only = Assert.Single(s.Snapshot());
        Assert.Equal(TradeDirection.Incoming, only.Direction);
        Assert.Equal("Bob", only.Player);
        Assert.Equal("GLD", only.Guild);
        Assert.Equal("Foo", only.Request.Item);
        Assert.Equal(TradeState.New, only.State);
        Assert.Equal(_now, only.CreatedUtc);
        Assert.Equal("Bob", s.LastWhisperPartner);
    }

    [Fact]
    public void Outgoing_trade_whisper_creates_outgoing_session()
    {
        var s = New();
        s.Consume(new WhisperSent(T, "Seller", Buy));
        var only = Assert.Single(s.Snapshot());
        Assert.Equal(TradeDirection.Outgoing, only.Direction);
        Assert.Equal("Seller", only.Player);
        Assert.Equal("Seller", s.LastWhisperPartner);
    }

    [Fact]
    public void Duplicate_whisper_refreshes_instead_of_duplicating()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        _now = _now.AddMinutes(1);
        s.Consume(new WhisperReceived(T, "bob", null, Buy.Replace("1 divine", "2 divine")));
        var only = Assert.Single(s.Snapshot());
        Assert.Equal(2, only.Request.Amount);
        Assert.Equal(1, only.Repeats);
        Assert.Equal(_now, only.UpdatedUtc);
        s.Consume(new WhisperReceived(T, "Bob", null, BuyBar));
        Assert.Equal(2, s.Snapshot().Count);
    }

    [Fact]
    public void Follow_up_whispers_are_appended_and_capped()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        for (var i = 0; i < 8; i++) s.Consume(new WhisperReceived(T, "Bob", null, "msg " + i));
        var w = Assert.Single(s.Snapshot()).Whispers;
        Assert.Equal(5, w.Count);
        Assert.Equal("msg 7", w[^1]);
        Assert.Equal("msg 3", w[0]);
    }

    [Fact]
    public void Join_and_leave_track_player_in_area()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        var id = s.Snapshot()[0].Id;
        Assert.Equal(["/invite Bob"], s.Execute(id, TradeAction.Invite));
        Assert.Equal(TradeState.Invited, s.Snapshot()[0].State);
        s.Consume(new PlayerJoined(T, "Bob"));
        Assert.True(s.Snapshot()[0].PlayerInArea);
        Assert.Equal(TradeState.InArea, s.Snapshot()[0].State);
        s.Consume(new PlayerLeft(T, "Bob"));
        Assert.False(s.Snapshot()[0].PlayerInArea);
        Assert.Equal(TradeState.Invited, s.Snapshot()[0].State);
    }

    [Fact]
    public void Whisper_from_someone_already_in_area_starts_in_area_and_zone_change_clears_it()
    {
        var s = New();
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        Assert.Equal(TradeState.InArea, s.Snapshot()[0].State);
        s.Consume(new AreaEntered(T, "Clearfell"));
        Assert.False(s.Snapshot()[0].PlayerInArea);
        Assert.Equal("Clearfell", s.CurrentAreaName);
    }

    [Fact]
    public void Trade_accepted_completes_the_marked_session_and_raises_completed()
    {
        var s = New();
        TradeSession? done = null;
        s.Completed += x => done = x;
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new WhisperReceived(T, "Amy", null, BuyBar));
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new PlayerJoined(T, "Amy"));
        var bob = s.Snapshot().Single(x => x.Player == "Bob").Id;
        Assert.Equal(["/tradewith Bob"], s.Execute(bob, TradeAction.Trade));
        Assert.Equal(TradeState.Trading, s.Snapshot().Single(x => x.Id == bob).State);
        s.Consume(new TradeAccepted(T));
        Assert.NotNull(done);
        Assert.Equal("Bob", done!.Player);
        Assert.Equal(TradeState.Completed, done.State);
        Assert.Equal(_now, done.CompletedUtc);
        Assert.Equal(TradeState.InArea, s.Snapshot().Single(x => x.Player == "Amy").State);
    }

    [Fact]
    public void Trade_accepted_falls_back_to_most_recent_in_area_session()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        _now = _now.AddSeconds(5);
        s.Consume(new WhisperReceived(T, "Amy", null, BuyBar));
        _now = _now.AddSeconds(5);
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new TradeAccepted(T));
        Assert.Equal(TradeState.Completed, s.Snapshot().Single(x => x.Player == "Bob").State);
        Assert.Equal(TradeState.New, s.Snapshot().Single(x => x.Player == "Amy").State);
    }

    [Fact]
    public void Unmarked_trade_accepted_with_two_buyers_in_the_area_records_nothing()
    {
        var s = New();
        var fired = false;
        s.Completed += _ => fired = true;
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new WhisperReceived(T, "Amy", null, BuyBar));
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new PlayerJoined(T, "Amy"));
        s.Consume(new TradeAccepted(T));   // with whom? not knowable — don't guess into the earnings history
        Assert.False(fired);
        Assert.All(s.Snapshot(), x => Assert.Equal(TradeState.InArea, x.State));
    }

    [Fact]
    public void Trade_accepted_with_no_plausible_session_is_ignored()
    {
        var s = New();
        var fired = false;
        s.Completed += _ => fired = true;
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new TradeAccepted(T));
        Assert.False(fired);
        Assert.Equal(TradeState.New, s.Snapshot()[0].State);
    }

    [Fact]
    public void Trade_cancelled_reverts_trading()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new PlayerJoined(T, "Bob"));
        var id = s.Snapshot()[0].Id;
        s.MarkTrading(id);
        s.Consume(new TradeCancelled(T));
        Assert.Equal(TradeState.InArea, s.Snapshot()[0].State);
        // The cancelled target is no longer the preferred completion; a later accept falls back to in-area.
        s.Consume(new TradeAccepted(T));
        Assert.Equal(TradeState.Completed, s.Snapshot()[0].State);
    }

    [Fact]
    public void Marking_another_session_reverts_the_previous_trading_one()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new WhisperReceived(T, "Amy", null, BuyBar));
        var ids = s.Snapshot().Select(x => x.Id).ToArray();
        Assert.True(s.MarkTrading(ids[0]));
        Assert.True(s.MarkTrading(ids[1]));
        Assert.Equal(TradeState.New, s.Snapshot()[0].State);
        Assert.Equal(TradeState.Trading, s.Snapshot()[1].State);
        Assert.False(s.MarkTrading(999));
    }

    [Fact]
    public void Sold_and_kick_after_completion_dismiss()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new WhisperReceived(T, "Amy", null, BuyBar));
        var amy = s.Snapshot().Single(x => x.Player == "Amy").Id;
        Assert.Equal(["@Amy sorry, already sold"], s.Execute(amy, TradeAction.Sold));
        Assert.DoesNotContain(s.Snapshot(), x => x.Player == "Amy");

        var bob = s.Snapshot()[0].Id;
        Assert.Equal(["/kick Bob"], s.Execute(bob, TradeAction.Kick)); // not completed: stays
        Assert.Single(s.Snapshot());
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new TradeAccepted(T));
        s.Execute(bob, TradeAction.Kick);
        Assert.Empty(s.Snapshot());
        Assert.Empty(s.Execute(bob, TradeAction.Kick)); // gone
    }

    [Fact]
    public void Thanks_with_kick_after_trade_returns_both_commands()
    {
        var s = New(new TradeSettings { KickAfterTrade = true });
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        s.Consume(new PlayerJoined(T, "Bob"));
        s.Consume(new TradeAccepted(T));
        var id = s.Snapshot()[0].Id;
        Assert.Equal(["@Bob ty, gl!", "/kick Bob"], s.Execute(id, TradeAction.Thanks));
        Assert.Empty(s.Snapshot());
    }

    [Fact]
    public void Command_formatting_respects_direction_and_messages()
    {
        var cfg = new TradeSettings();
        var req = TradeRequest.Parse(Buy)!;
        var inc = new TradeSession(1, TradeDirection.Incoming, "Bob", null, req, _now, _now, TradeState.New, false, [], 0, null);
        var outg = inc with { Direction = TradeDirection.Outgoing };
        Assert.Equal("/invite Bob", TradeSessions.CommandFor(TradeAction.Invite, inc, cfg));
        Assert.Null(TradeSessions.CommandFor(TradeAction.Invite, outg, cfg));
        Assert.Equal("/tradewith Bob", TradeSessions.CommandFor(TradeAction.Trade, inc, cfg));
        Assert.Equal("/kick Bob", TradeSessions.CommandFor(TradeAction.Kick, inc, cfg));
        Assert.Equal("@Bob ty, gl!", TradeSessions.CommandFor(TradeAction.Thanks, inc, cfg));
        Assert.Equal("@Bob busy right now, will invite soon", TradeSessions.CommandFor(TradeAction.Busy, inc, cfg));
        Assert.Equal("@Bob sorry, already sold", TradeSessions.CommandFor(TradeAction.Sold, inc, cfg));
        Assert.Null(TradeSessions.CommandFor(TradeAction.Sold, outg, cfg));
        Assert.Equal("@Bob still interested?", TradeSessions.CommandFor(TradeAction.StillInterested, inc, cfg));
        Assert.Equal("/hideout Bob", TradeSessions.CommandFor(TradeAction.VisitHideout, outg, cfg));
        Assert.Null(TradeSessions.CommandFor(TradeAction.VisitHideout, inc, cfg));
        Assert.Equal("/whois Bob", TradeSessions.CommandFor(TradeAction.WhoIs, inc, cfg));
        Assert.Null(TradeSessions.CommandFor(TradeAction.Thanks, inc, new TradeSettings { ThanksMessage = " " }));
    }

    [Fact]
    public void Stale_sessions_expire_and_cap_drops_oldest()
    {
        var s = New(new TradeSettings { ExpireMinutes = 30, MaxSessions = 3 });
        s.Consume(new WhisperReceived(T, "Old", null, Buy));
        _now = _now.AddMinutes(31);
        Assert.Empty(s.Snapshot());

        for (var i = 0; i < 5; i++)
        {
            _now = _now.AddSeconds(1);
            s.Consume(new WhisperReceived(T, "P" + i, null, Buy));
        }
        Assert.Equal(["P2", "P3", "P4"], s.Snapshot().Select(x => x.Player));
    }

    [Fact]
    public void Snapshot_is_cached_until_generation_changes()
    {
        var s = New();
        s.Consume(new WhisperReceived(T, "Bob", null, Buy));
        var a = s.Snapshot();
        var gen = s.Generation;
        Assert.Same(a, s.Snapshot());
        s.Consume(new PlayerJoined(T, "Bob"));
        Assert.NotEqual(gen, s.Generation);
        Assert.NotSame(a, s.Snapshot());
        Assert.Equal(TradeState.New, a[0].State); // earlier copies are immutable
    }

    [Fact]
    public void Visiting_a_sellers_hideout_puts_them_in_area_so_the_purchase_completes()
    {
        var s = New();
        TradeSession? done = null;
        s.Completed += x => done = x;
        s.Consume(new WhisperSent(T, "Seller", Buy));
        var id = s.Snapshot()[0].Id;
        Assert.Equal(["/hideout Seller"], s.Execute(id, TradeAction.VisitHideout));
        s.Consume(new AreaEntered(T, "Seller's Hideout"));
        Assert.Equal(TradeState.InArea, s.Snapshot()[0].State);
        s.Consume(new TradeAccepted(T));
        Assert.Equal(TradeDirection.Outgoing, done?.Direction);
        s.Consume(new AreaEntered(T, "Clearfell"));
        Assert.False(s.Snapshot()[0].PlayerInArea);
    }

    [Fact]
    public void Afk_and_area_code_are_tracked()
    {
        var s = New();
        s.Consume(new AfkChanged(T, true));
        Assert.True(s.IsAfk);
        s.Consume(new AreaGenerated(T, 65, "HideoutFelled"));
        Assert.Equal("HideoutFelled", s.CurrentAreaCode);
    }

    [Fact]
    public void End_to_end_from_log_lines()
    {
        var s = New();
        TradeSession? done = null;
        s.Completed += x => done = x;
        const string p = "2025/01/05 12:34:56 123456789 cffb0719 [INFO Client 12345] ";
        foreach (var line in new[]
        {
            p + "@From <GLD> Buyer_Name: " + Buy,
            p + ": Buyer_Name has joined the area.",
            p + ": Trade accepted.",
        })
            s.Consume(ClientLogParser.Parse(line));
        Assert.Equal("Buyer_Name", done?.Player);
        Assert.Equal("Foo", done!.Request.Item);
    }
}
