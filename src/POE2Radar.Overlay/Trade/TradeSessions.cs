using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Trade;

public enum TradeDirection { Incoming, Outgoing }

public enum TradeState { New, Invited, InArea, Trading, Completed, Cancelled, Dismissed }

public enum TradeAction { Invite, Trade, Kick, Thanks, Busy, Sold, StillInterested, VisitHideout, WhoIs }

/// <summary>
/// Immutable view of one trade conversation. Incoming = someone whispered to buy from us; Outgoing = we
/// whispered a seller. <see cref="PlayerInArea"/> tracks the counterpart via join/leave lines (only
/// meaningful in OUR area — joining a seller's hideout produces no join line for them).
/// </summary>
public sealed record TradeSession(int Id, TradeDirection Direction, string Player, string? Guild, TradeRequest Request,
    DateTime CreatedUtc, DateTime UpdatedUtc, TradeState State, bool PlayerInArea, IReadOnlyList<string> Whispers,
    int Repeats, DateTime? CompletedUtc)
{
    public bool IsOpen => State is TradeState.New or TradeState.Invited or TradeState.InArea or TradeState.Trading;
}

/// <summary>
/// Thread-safe trade-session store. The log tailer thread feeds <see cref="Consume"/>; the render thread
/// reads <see cref="Snapshot"/> each frame (cached until <see cref="Generation"/> changes, so it allocates
/// only on change); UI/HTTP actions go through <see cref="Execute"/>. <see cref="Completed"/> fires
/// outside the lock on the thread that caused completion.
/// </summary>
public sealed class TradeSessions
{
    private const int MaxWhispers = 5;
    private readonly object _gate = new();
    private readonly List<Session> _sessions = new();
    // Everyone seen joining our current instance, so a whisper from someone already here starts InArea.
    private readonly HashSet<string> _inArea = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;
    private IReadOnlyList<TradeSession> _cached = [];
    private int _cachedGeneration = -1;
    private int _nextId = 1, _generation;
    private int? _tradingId, _visitingId;
    private string? _lastPartner, _areaName, _areaCode;
    private bool _afk;

    public TradeSessions(TradeSettings settings, Func<DateTime>? utcNow = null)
    {
        Settings = settings;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Live settings (ExpireMinutes/MaxSessions are read on every prune); swap on config reload.</summary>
    public TradeSettings Settings { get; set; }
    public event Action<TradeSession>? Completed;
    public int Generation => Volatile.Read(ref _generation);
    /// <summary>Last @From/@To partner, for an "@last" chat placeholder.</summary>
    public string? LastWhisperPartner { get { lock (_gate) return _lastPartner; } }
    public bool IsAfk { get { lock (_gate) return _afk; } }
    public string? CurrentAreaName { get { lock (_gate) return _areaName; } }
    /// <summary>Internal area code from the last "Generating level N area" line (e.g. "HideoutFelled").</summary>
    public string? CurrentAreaCode { get { lock (_gate) return _areaCode; } }

    public IReadOnlyList<TradeSession> Snapshot()
    {
        lock (_gate)
        {
            Prune();
            if (_cachedGeneration == _generation) return _cached;
            _cached = _sessions.Select(s => s.Freeze()).ToArray();
            _cachedGeneration = _generation;
            return _cached;
        }
    }

    public void Consume(LogEvent? evt)
    {
        if (evt == null) return;
        TradeSession? completed = null;
        lock (_gate)
        {
            var now = _utcNow();
            switch (evt)
            {
                case WhisperReceived w: OnWhisper(TradeDirection.Incoming, w.From, w.Guild, w.Message, now); break;
                case WhisperSent w: OnWhisper(TradeDirection.Outgoing, w.To, null, w.Message, now); break;
                case PlayerJoined j: SetInArea(j.Player, true, now); break;
                case PlayerLeft l: SetInArea(l.Player, false, now); break;
                case AreaEntered a:
                    _areaName = a.Name;
                    _inArea.Clear();
                    // We changed instance: nobody we were tracking is with us any more.
                    foreach (var s in _sessions.Where(s => s.PlayerInArea)) Leave(s, now);
                    // A seller's hideout has no join line for its owner; our own /hideout jump is the signal.
                    if (_visitingId is int v && Find(v) is { IsOpen: true } visit)
                    {
                        visit.PlayerInArea = true;
                        if (visit.State is TradeState.New or TradeState.Invited) visit.State = TradeState.InArea;
                        visit.UpdatedUtc = now;
                    }
                    _visitingId = null;
                    Bump();
                    break;
                case AreaGenerated g: _areaCode = g.Code; break;
                case TradeAccepted: completed = CompleteTrade(now); break;
                case TradeCancelled:
                    if (_tradingId is int id && Find(id) is { State: TradeState.Trading } t) Revert(t, now);
                    _tradingId = null;
                    break;
                case AfkChanged a: _afk = a.On; Bump(); break;
            }
            Prune();
        }
        if (completed != null) Completed?.Invoke(completed);
    }

    /// <summary>Marks the session we're about to /tradewith, so the next "Trade accepted." is attributed to it.</summary>
    public bool MarkTrading(int id)
    {
        lock (_gate) return MarkTradingLocked(id, _utcNow());
    }

    /// <summary>Removes a session from the panel (the X button).</summary>
    public bool Dismiss(int id)
    {
        lock (_gate)
        {
            var s = Find(id);
            if (s == null) return false;
            Remove(s);
            return true;
        }
    }

    /// <summary>Closes an open session without a trade (kept visible until it expires or is dismissed).</summary>
    public bool Cancel(int id)
    {
        lock (_gate)
        {
            if (Find(id) is not { IsOpen: true } s) return false;
            s.State = TradeState.Cancelled;
            s.UpdatedUtc = _utcNow();
            if (_tradingId == id) _tradingId = null;
            Bump();
            return true;
        }
    }

    /// <summary>
    /// Formats the chat command(s) for <paramref name="action"/> and advances the session's state. The caller
    /// sends the returned lines (in order); empty = not applicable. Thanks on a completed incoming trade
    /// appends a kick when <see cref="TradeSettings.KickAfterTrade"/> is set.
    /// </summary>
    public IReadOnlyList<string> Execute(int id, TradeAction action)
    {
        lock (_gate)
        {
            var s = Find(id);
            if (s == null) return [];
            var cfg = Settings;
            var view = s.Freeze();
            var cmd = CommandFor(action, view, cfg);
            if (cmd == null) return [];
            var lines = new List<string> { cmd };
            var now = _utcNow();
            s.UpdatedUtc = now;
            switch (action)
            {
                case TradeAction.Invite when s.State == TradeState.New: s.State = TradeState.Invited; break;
                case TradeAction.Trade: MarkTradingLocked(id, now); break;
                case TradeAction.Kick when s.State == TradeState.Completed: Remove(s); break;
                case TradeAction.Sold: Remove(s); break;
                case TradeAction.VisitHideout: _visitingId = id; break;
                case TradeAction.Thanks when cfg.KickAfterTrade && s is { State: TradeState.Completed, Direction: TradeDirection.Incoming }:
                    if (CommandFor(TradeAction.Kick, view, cfg) is { } kick) lines.Add(kick);
                    Remove(s);
                    break;
            }
            Bump();
            return lines;
        }
    }

    /// <summary>Pure action → chat text. Null when the action doesn't apply to this session/direction.</summary>
    public static string? CommandFor(TradeAction action, TradeSession s, TradeSettings cfg)
    {
        var p = s.Player;
        if (string.IsNullOrWhiteSpace(p)) return null;
        var incoming = s.Direction == TradeDirection.Incoming;
        return action switch
        {
            TradeAction.Invite => incoming ? $"/invite {p}" : null,
            TradeAction.Trade => $"/tradewith {p}",
            TradeAction.Kick => $"/kick {p}",
            TradeAction.Thanks => Whisper(p, cfg.ThanksMessage),
            TradeAction.Busy => Whisper(p, cfg.BusyMessage),
            TradeAction.Sold => incoming ? Whisper(p, cfg.SoldMessage) : null,
            TradeAction.StillInterested => Whisper(p, cfg.StillInterestedMessage),
            TradeAction.VisitHideout => incoming ? null : $"/hideout {p}",
            TradeAction.WhoIs => $"/whois {p}",
            _ => null,
        };
    }

    private static string? Whisper(string player, string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : $"@{player} {message.Trim()}";

    private void OnWhisper(TradeDirection dir, string player, string? guild, string message, DateTime now)
    {
        _lastPartner = player;
        var request = TradeRequest.Parse(message);
        if (request == null)
        {
            // Follow-up chat ("still there?", "sent party") goes onto that player's newest open incoming session.
            if (dir != TradeDirection.Incoming) return;
            var target = _sessions.LastOrDefault(s => s.IsOpen && Same(s.Player, player));
            if (target == null) return;
            target.Whispers.Add(message);
            if (target.Whispers.Count > MaxWhispers) target.Whispers.RemoveAt(0);
            target.UpdatedUtc = now;
            Bump();
            return;
        }

        var existing = _sessions.FirstOrDefault(s => s.IsOpen && s.Direction == dir && Same(s.Player, player)
            && string.Equals(s.Request.Item, request.Item, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Request = request; // the price may have been updated since the first whisper
            existing.Repeats++;
            existing.UpdatedUtc = now;
            if (guild != null) existing.Guild = guild;
            Bump();
            return;
        }
        var here = _inArea.Contains(player);
        _sessions.Add(new Session
        {
            Id = _nextId++, Direction = dir, Player = player, Guild = guild, Request = request,
            CreatedUtc = now, UpdatedUtc = now, PlayerInArea = here, State = here ? TradeState.InArea : TradeState.New,
        });
        Bump();
    }

    private void SetInArea(string player, bool inArea, DateTime now)
    {
        if (inArea) _inArea.Add(player); else _inArea.Remove(player);
        var changed = false;
        foreach (var s in _sessions.Where(s => Same(s.Player, player)))
        {
            if (inArea)
            {
                s.PlayerInArea = true;
                if (s.State is TradeState.New or TradeState.Invited) s.State = TradeState.InArea;
                s.UpdatedUtc = now;
            }
            else Leave(s, now);
            changed = true;
        }
        if (changed) Bump();
    }

    private void Leave(Session s, DateTime now)
    {
        s.PlayerInArea = false;
        // They were in our area, so they had been invited; going back to Invited lets them return.
        if (s.State is TradeState.InArea or TradeState.Trading) s.State = TradeState.Invited;
        if (_tradingId == s.Id) _tradingId = null;
        s.UpdatedUtc = now;
    }

    private TradeSession? CompleteTrade(DateTime now)
    {
        var target = _tradingId is int id ? Find(id) : null;
        if (target is not { IsOpen: true })
        {
            // Unmarked trade: only a lone open session in the area is an unambiguous counterpart. With two buyers here,
            // or a trade with someone we aren't tracking, recording a guess would put the wrong item and price in the
            // earnings history.
            var here = _sessions.Where(s => s.IsOpen && s.PlayerInArea).Take(2).ToList();
            target = here.Count == 1 ? here[0] : null;
        }
        _tradingId = null;
        // No unambiguous counterpart (e.g. a trade with a party member we weren't tracking): ignore.
        if (target == null) return null;
        target.State = TradeState.Completed;
        target.CompletedUtc = now;
        target.UpdatedUtc = now;
        Bump();
        return target.Freeze();
    }

    private bool MarkTradingLocked(int id, DateTime now)
    {
        if (Find(id) is not { IsOpen: true } s) return false;
        if (_tradingId is int prev && prev != id && Find(prev) is { State: TradeState.Trading } old) Revert(old, now);
        if (s.State != TradeState.Trading) s.StateBeforeTrade = s.State;
        s.State = TradeState.Trading;
        s.UpdatedUtc = now;
        _tradingId = id;
        Bump();
        return true;
    }

    private static void Revert(Session s, DateTime now)
    {
        s.State = s.PlayerInArea ? TradeState.InArea
            : s.StateBeforeTrade is TradeState.New or TradeState.Invited ? s.StateBeforeTrade : TradeState.Invited;
        s.UpdatedUtc = now;
    }

    /// <summary>Drops sessions idle past ExpireMinutes, then the oldest (closed first) above MaxSessions.</summary>
    private void Prune()
    {
        var cfg = Settings;
        var cutoff = _utcNow() - TimeSpan.FromMinutes(Math.Max(1, cfg.ExpireMinutes));
        // The active trade target never expires mid-trade.
        var removed = _sessions.RemoveAll(s => s.UpdatedUtc < cutoff && s.Id != _tradingId);
        var cap = Math.Max(1, cfg.MaxSessions);
        while (_sessions.Count > cap)
        {
            var victim = _sessions.Where(s => !s.IsOpen).MinBy(s => s.UpdatedUtc)
                ?? _sessions.Where(s => s.Id != _tradingId).MinBy(s => s.UpdatedUtc) ?? _sessions[0];
            Remove(victim);
            removed++;
        }
        if (removed > 0) Bump();
    }

    private void Remove(Session s)
    {
        s.State = TradeState.Dismissed;
        _sessions.Remove(s);
        if (_tradingId == s.Id) _tradingId = null;
        Bump();
    }

    private Session? Find(int id) => _sessions.Find(s => s.Id == id);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private void Bump() => Interlocked.Increment(ref _generation);

    private sealed class Session
    {
        public int Id;
        public TradeDirection Direction;
        public string Player = "";
        public string? Guild;
        public TradeRequest Request = null!;
        public DateTime CreatedUtc, UpdatedUtc;
        public DateTime? CompletedUtc;
        public TradeState State, StateBeforeTrade;
        public bool PlayerInArea;
        public int Repeats;
        public readonly List<string> Whispers = new();

        public bool IsOpen => State is TradeState.New or TradeState.Invited or TradeState.InArea or TradeState.Trading;

        public TradeSession Freeze() => new(Id, Direction, Player, Guild, Request, CreatedUtc, UpdatedUtc, State,
            PlayerInArea, Whispers.ToArray(), Repeats, CompletedUtc);
    }
}
