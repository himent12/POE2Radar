using POE2Radar.Core.Game;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Pricing;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // ── Price check (hotkey, default Ctrl+D): hover an item and press the key → a panel with the poe.ninja
    //    estimate beside the cheapest live trade listings and a suggested list price. The render thread only
    //    raises requests; the world thread reads the item, queries (via the rate-limited trade client) and
    //    publishes a formatted view. Nothing is sent to the game. ──
    private volatile bool _pcRequest, _pcClose, _pcRefresh;

    private volatile PriceCheckView? _pcView;

    private sealed class PriceCheckState
    {
        public required nint Item;
        public required uint Area;
        public required Poe2Live.HoveredItem Hover;
        public required ItemTradeProfile Profile;
        public required string Key;
        public required bool NeedsStats;
        public required Func<IReadOnlyList<TradeComparison.TradeStat>?, QueryPlan> Plan;
        public required string? UniqueName;
        public MarketSearch? Last;
        public bool Refresh;
    }

    private PriceCheckState? _pc;   // world thread only

    // Short on-screen message (e.g. "no item under the cursor") so a hotkey press never fails silently.
    private sealed record Toast(string Text, DateTime Until);

    private volatile Toast? _toast;

    private void ShowToast(string text, double seconds = 2.5)
    {
        _toast = new Toast(text, DateTime.UtcNow.AddSeconds(seconds));
        Console.WriteLine($"\n{text}");
    }

    /// <summary>World thread: service open/close/refresh requests and keep the published view current.</summary>
    private void UpdatePriceCheck(nint inGameState, uint areaHash)
    {
        if (_pcClose) { _pcClose = false; _pc = null; _pcView = null; }
        if (_pcRequest) { _pcRequest = false; StartPriceCheck(inGameState, areaHash); }
        if (_pc is not { } pc) return;
        if (pc.Area != areaHash) { _pc = null; _pcView = null; return; }
        if (_pcRefresh) { _pcRefresh = false; pc.Refresh = true; }

        var search = _tradeComparison.GetOrQueueSearch(pc.Key, pc.Plan, pc.NeedsStats, _priceBook, pc.Refresh);
        pc.Refresh = false;
        if (ReferenceEquals(search, pc.Last) && _pcView is not null) return;
        pc.Last = search;
        _pcView = BuildPriceCheckView(pc, search);
    }

    private void StartPriceCheck(nint inGameState, uint areaHash)
    {
        if (!GameHost.GetCursorPos(out var pt)) { ShowToast("Price check: can't read the mouse position"); return; }
        var (cx, cy) = ScreenToClientPoint(pt);
        if (_live.ReadHoveredItem(inGameState, _window.Width, _window.Height, cx, cy) is not { } h)
        {
            // Nothing under the cursor: close an open panel, otherwise say why nothing happened.
            if (_pc is not null) { _pc = null; _pcView = null; return; }
            ShowToast($"Price check: no item under the cursor ({cx},{cy}) — hover an item, then press {_settings.HoverPrice.PriceCheckHotkey}");
            return;
        }
        Console.WriteLine($"\nPrice check: {h.Rarity} \"{h.Name}\" (art {h.Art}, stack {h.Stack})");
        if (_pc is { } open && open.Item == h.Item) { _pcRefresh = true; return; }   // same item again = refresh
        var profile = _live.ReadItemTradeProfile(h);
        var unique = h.Rarity == Poe2Live.Rarity.Unique && PriceCheck.Estimate(h, _priceBook.Current) is { } u ? u.Name : null;
        var (key, needsStats, plan) = PriceCheck.For(profile, unique, _live.ItemMetadata(h.Item));
        _pc = new PriceCheckState
        {
            Item = h.Item, Area = areaHash, Hover = h, Profile = profile, Key = key, NeedsStats = needsStats, Plan = plan,
            UniqueName = unique,
        };
    }

    private PriceCheckView BuildPriceCheckView(PriceCheckState pc, MarketSearch search)
    {
        var h = pc.Hover;
        var snap = _priceBook.Current;
        string Fmt(double ex) => _priceBook.Format(ex);

        var stack = Math.Max(1, h.Stack);
        var est = PriceCheck.Estimate(h, snap);
        string? estimate = null;
        var estimateSub = h.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic
            ? "No reference price — rares are priced by their rolls"
            : snap.ByName.Count == 0 ? "poe.ninja prices not loaded yet" : "Not listed on poe.ninja";
        if (est is { } e)
        {
            estimate = stack > 1 ? $"{Fmt(e.Exalted * stack)}" : Fmt(e.Exalted);
            var parts = new List<string>();
            if (stack > 1) parts.Add($"{stack:N0} × {Fmt(e.Exalted)}");
            parts.Add(e.IsListingPrice ? $"{e.Quantity:N0} listings" : "exchange rate");
            if (e.TrendPercent is { } t) parts.Add($"{t:+0;-0;0}% trend");
            estimateSub = "poe.ninja · " + string.Join(" · ", parts);
        }

        var summary = PriceCheck.Summarize(search.Listings, est?.Exalted, Fmt,
            similar: h.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic);
        var now = DateTime.UtcNow;
        var rows = new List<PriceCheckRow>(search.Listings.Count);
        foreach (var l in search.Listings.Take(8))
        {
            var cur = l.Currency switch { "exalted" => "ex", "divine" => "div", _ => l.Currency };
            var price = $"{l.Amount:0.##} {cur}" + (l.Stack > 1 ? " each" : "");
            var value = l.Exalted is { } ex && l.Currency != "exalted" ? "~" + Fmt(ex) : "";
            var age = l.Indexed is { } ix ? AgeShort(now - ix) : "";
            rows.Add(new PriceCheckRow(price, value, l.Seller, age));
        }

        var name = pc.UniqueName ?? h.Name ?? "Unknown item";
        var baseLine = pc.UniqueName is not null && h.Name is { } b ? b : RarityName(h.Rarity);
        if (stack > 1) baseLine += $" · stack of {stack:N0}";
        var mods = pc.Profile.Mods.Where(m => m.Kind == "explicit").Select(m => m.Text).Take(6).ToList();
        var url = search.Url ?? HoverValuation.TradeSearch(_priceBook.ComparisonLeague, h.Name, pc.UniqueName, h.Rarity);

        return new PriceCheckView(h.BoxX, h.BoxY, h.BoxW, h.BoxH, name, baseLine, RarityRgb(h.Rarity, h.Art), mods,
            estimate, estimateSub, search.Status, search.Note, search.Pending, search.Error,
            rows, summary.Points, est?.Exalted,
            summary.Min is { } mn ? Fmt(mn) : null, summary.Median is { } md ? Fmt(md) : null,
            summary.Verdict, url, _priceBook.ComparisonLeague,
            PriceCheck.Tier((est?.Exalted ?? summary.Median) * stack, snap.ExPerDivine, search.Pending));
    }

    private static string AgeShort(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)}m"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalDays}d";

    private static string RarityName(Poe2Live.Rarity r) => r switch
    {
        Poe2Live.Rarity.Unique => "Unique", Poe2Live.Rarity.Rare => "Rare", Poe2Live.Rarity.Magic => "Magic", _ => "Item",
    };

    /// <summary>PoE's rarity colours, lifted a touch to read on the panel's black (currency-ish art gets the
    /// currency tan).</summary>
    private static uint RarityRgb(Poe2Live.Rarity r, string? art) => r switch
    {
        Poe2Live.Rarity.Unique => 0xD9923F,
        Poe2Live.Rarity.Rare => 0xE8D36A,
        Poe2Live.Rarity.Magic => 0x8888FF,
        _ when art?.Contains("Currency", StringComparison.OrdinalIgnoreCase) == true => 0xAA9E82,
        _ => 0xD8D8D8,
    };

    /// <summary>Render thread: price-check panel buttons.</summary>
    private void OnPriceCheckClick(string action)
    {
        switch (action)
        {
            case "pc:close": _pcClose = true; break;
            case "pc:refresh": _pcRefresh = true; break;
            case "pc:trade" when _pcView?.Url is { } url:
                if (!Input.BrowserLauncher.Open(url, out var error)) Console.Error.WriteLine($"Open trade site failed: {error}");
                break;
        }
    }
}
