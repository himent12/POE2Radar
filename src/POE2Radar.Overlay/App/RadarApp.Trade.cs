using POE2Radar.Overlay.Trade;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // ── Trade assistant: Client.txt → sessions (panel) + history (tracker). The tailer runs on its own
    //    thread and feeds the thread-safe session store; the render thread only reads Snapshot(). ──
    private readonly TradeSessions _trade;

    private readonly TradeHistory _tradeHistory;

    private readonly ClientLogTailer _tradeLog;

    // Client.txt location, re-resolved at most every few seconds (Locate touches the disk; the tailer asks
    // for the path every poll).
    private string? _tradeLogPath;

    private string _tradeLogOverride = "";

    private DateTime _tradeLogResolvedAt = DateTime.MinValue;

    // Render-side cache of the panel cards: rebuilt only when the session store's Generation changes (or
    // once a second, for the relative "age" labels).
    private IReadOnlyList<TradeCard> _tradeCards = Array.Empty<TradeCard>();

    private int _tradeCardsGen = -1;

    private DateTime _tradeCardsAt;

    private string? LastWhisperPartner() => _trade.LastWhisperPartner;

    private TradeMenuInfo? _tradeMenu;

    private DateTime _tradeMenuAt;

    /// <summary>INSERT Trade section data (render thread, ≤ 1 Hz while the tab is open — Summary scans history).</summary>
    private TradeMenuInfo TradeMenu()
    {
        var now = DateTime.UtcNow;
        if (_tradeMenu is not null && now - _tradeMenuAt < TimeSpan.FromSeconds(1)) return _tradeMenu;
        _tradeMenuAt = now;
        string Line(TradeSummary s) => $"{s.Sold} sold · {s.Bought} bought · {(s.ProfitExalted >= 0 ? "+" : "−")}{_priceBook.Format(Math.Abs(s.ProfitExalted))}";
        var stats = new List<(string, string)>
        {
            ("Today", Line(_tradeHistory.Summary(DateTime.Now.Date.ToUniversalTime(), ToExalted))),
            ("Last 7 days", Line(_tradeHistory.Summary(now.AddDays(-7), ToExalted))),
            ("All time", Line(_tradeHistory.Summary(DateTime.MinValue, ToExalted))),
        };
        var recent = _tradeHistory.Records().Take(8)
            .Select(r => $"{(r.Direction == TradeSide.Sold ? "+" : "−")} {r.Amount:0.##} {ShortCurrency(r.Currency)}  {r.Item}  ({r.Player})")
            .ToList();
        var path = _tradeLog.CurrentPath;
        _tradeMenu = new TradeMenuInfo(path is null ? "Client.txt not found — set the path on the dashboard" : _tradeLog.Status,
            path is not null, _tradeHistory.Count == 0 ? Array.Empty<(string, string)>() : stats, recent);
        return _tradeMenu;
    }

    private (TradeSessions Sessions, TradeHistory History, ClientLogTailer Tailer) CreateTrade()
    {
        var sessions = new TradeSessions(_settings.Trade);
        var history = new TradeHistory(Path.Combine(ConfigDir, "trade_history.json"));
        sessions.Completed += s => { if (_settings.Trade.TrackHistory) history.Add(s); };
        var tailer = new ClientLogTailer(ResolveTradeLogPath);
        tailer.OnLine += line =>
        {
            if (_settings.Trade.Enabled) sessions.Consume(ClientLogParser.Parse(line));
        };
        return (sessions, history, tailer);
    }

    /// <summary>Tailer thread: the Client.txt path (settings override, else derived from the game process /
    /// install locations), cached a few seconds.</summary>
    private string? ResolveTradeLogPath()
    {
        var cfg = _settings.Trade;
        if (!cfg.Enabled) return null;
        var now = DateTime.UtcNow;
        if (_tradeLogPath is not null && cfg.ClientLogPath == _tradeLogOverride && now - _tradeLogResolvedAt < TimeSpan.FromSeconds(10))
            return _tradeLogPath;
        if (_tradeLogPath is null && cfg.ClientLogPath == _tradeLogOverride && now - _tradeLogResolvedAt < TimeSpan.FromSeconds(3))
            return null;
        _tradeLogOverride = cfg.ClientLogPath;
        _tradeLogResolvedAt = now;
        _tradeLogPath = ClientLogLocator.Locate(cfg.ClientLogPath, _process.ProcessId, _process.ModulePath);
        return _tradeLogPath;
    }

    /// <summary>Exalted value of a trade amount (the tracker's common unit). Exalted Orb is the price book's
    /// base unit, so it converts 1:1; anything the book doesn't know stays unconverted.</summary>
    private double? ToExalted(string currency, double amount)
    {
        if (string.IsNullOrEmpty(currency) || amount <= 0) return null;
        if (currency.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase)) return amount;
        return _priceBook.TryByName(currency) is { } p ? p.Exalted * amount : null;
    }

    /// <summary>Render thread: the trade-panel cards (formatted once per change, not per frame).</summary>
    private IReadOnlyList<TradeCard> TradeCards()
    {
        var cfg = _settings.Trade;
        // The dashboard swaps in a whole new TradeSettings object on edit; hand it to the session store so
        // new messages / expiry / caps apply without a restart.
        if (!ReferenceEquals(_trade.Settings, cfg)) _trade.Settings = cfg;
        if (!cfg.Enabled || !cfg.ShowPanel) return Array.Empty<TradeCard>();
        var gen = _trade.Generation;
        var now = DateTime.UtcNow;
        if (gen == _tradeCardsGen && now - _tradeCardsAt < TimeSpan.FromSeconds(1)) return _tradeCards;
        _tradeCardsGen = gen;
        _tradeCardsAt = now;
        var sessions = _trade.Snapshot();
        var cards = new List<TradeCard>(sessions.Count);
        foreach (var s in sessions)
        {
            if (s.State is TradeState.Dismissed) continue;
            var r = s.Request;
            var price = r.Amount > 0 ? $"{r.Amount:0.##} {ShortCurrency(r.Currency)}" : "no price";
            var value = ToExalted(r.Currency, r.Amount) is { } ex && !r.Currency.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase)
                ? "~" + _priceBook.Format(ex) : "";
            var where = r.StashTab is { Length: > 0 } tab ? $"\"{tab}\"" + (r.Left is { } l && r.Top is { } t ? $" · {l},{t}" : "") : "";
            cards.Add(new TradeCard(s.Id, s.Direction == TradeDirection.Incoming, s.Player, r.Item, price, value, where,
                s.State.ToString(), s.PlayerInArea, Age(now - s.CreatedUtc), s.Repeats,
                s.Whispers.Count > 0 ? s.Whispers[^1] : ""));
        }
        _tradeCards = cards;
        return cards;
    }

    private static string Age(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0}s" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0}m" : $"{t.TotalHours:0}h";

    private static string ShortCurrency(string c) => c switch
    {
        "Divine Orb" => "div",
        "Exalted Orb" => "ex",
        "Chaos Orb" => "chaos",
        "Orb of Annulment" => "annul",
        "Orb of Alchemy" => "alch",
        "Regal Orb" => "regal",
        "Vaal Orb" => "vaal",
        "Mirror of Kalandra" => "mirror",
        _ => c,
    };

    /// <summary>Overlay click on a trade-panel button (<c>trade:&lt;id&gt;:&lt;action&gt;</c>). Chat lines go through
    /// the gated chat worker (PoE2 must still be foreground when each line is typed).</summary>
    private void OnTradeClick(string action)
    {
        var parts = action.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var id)) return;
        if (parts[2] == "dismiss") { _trade.Dismiss(id); return; }
        if (!Enum.TryParse<TradeAction>(parts[2], ignoreCase: true, out var act)) return;
        var lines = _trade.Execute(id, act);
        if (lines.Count > 0) _chat.Enqueue(lines);
    }

    private int OpenTradeCount()
    {
        var open = 0;
        foreach (var c in _tradeCards) if (c.State is "New" or "Invited" or "InArea" or "Trading") open++;
        return open;
    }

    /// <summary>API (POST /api/trade): bookkeeping only — {op:"dismiss",id} | {op:"delete",id} | {op:"clear"}.</summary>
    private object TradeCommandJson(System.Text.Json.JsonElement body)
    {
        string? Str(string name) => body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty(name, out var v)
            ? v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetRawText() : v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null
            : null;
        switch (Str("op"))
        {
            case "dismiss" when int.TryParse(Str("id"), out var id): _trade.Dismiss(id); return new { ok = true };
            case "delete" when Str("id") is { Length: > 0 } rid: return new { ok = _tradeHistory.Delete(rid) };
            case "clear": _tradeHistory.Clear(); return new { ok = true };
            default: return new { ok = false, error = "unknown op" };
        }
    }

    /// <summary>API: live sessions + tracker summary (today / 7 days / all) + recent history.</summary>
    private object TradeJson()
    {
        var now = DateTime.UtcNow;
        object Sum(DateTime since)
        {
            var s = _tradeHistory.Summary(since, ToExalted);
            return new
            {
                sold = s.Sold, bought = s.Bought, earnedEx = s.EarnedExalted, spentEx = s.SpentExalted,
                profitEx = s.ProfitExalted, unconverted = s.Unconverted,
                earned = s.EarnedByCurrency, spent = s.SpentByCurrency,
                profitText = _priceBook.Format(s.ProfitExalted),
            };
        }
        return new
        {
            enabled = _settings.Trade.Enabled,
            log = new { path = _tradeLog.CurrentPath, status = _tradeLog.Status, lines = _tradeLog.LinesRead },
            afk = _trade.IsAfk,
            sessions = _trade.Snapshot().Select(s => new
            {
                id = s.Id, direction = s.Direction.ToString(), player = s.Player, guild = s.Guild, state = s.State.ToString(),
                inArea = s.PlayerInArea, item = s.Request.Item, amount = s.Request.Amount, currency = s.Request.Currency,
                league = s.Request.League, stashTab = s.Request.StashTab, left = s.Request.Left, top = s.Request.Top,
                note = s.Request.Note, created = s.CreatedUtc, whispers = s.Whispers, repeats = s.Repeats,
            }),
            summary = new
            {
                today = Sum(DateTime.Now.Date.ToUniversalTime()),
                week = Sum(now.AddDays(-7)),
                all = Sum(DateTime.MinValue),
            },
            history = _tradeHistory.Records().Take(200).Select(r => new
            {
                id = r.Id, utc = r.Utc, direction = r.Direction.ToString(), player = r.Player, item = r.Item,
                amount = r.Amount, currency = r.Currency, league = r.League,
                valueEx = ToExalted(r.Currency, r.Amount),
            }),
            historyCount = _tradeHistory.Count,
            historyError = _tradeHistory.LastError,
        };
    }
}
