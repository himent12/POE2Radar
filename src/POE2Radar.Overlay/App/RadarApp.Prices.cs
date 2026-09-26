using System.Linq;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    private readonly Poe2Runeforge _runeforge;

    private readonly Poe2CurrencyExchange _exchange;

    private readonly ModCatalog _modCatalog;

    private readonly Pricing.PriceBook _priceBook;

    private volatile RuneRender _runeRender = RuneRender.Closed;

    private volatile ExchangeRender _exchangeRender = ExchangeRender.Closed;

    private volatile RitualRender _ritualRender = RitualRender.Closed;

    private volatile LootTagRender _lootTags = LootTagRender.Empty;

    private readonly List<LootTagLabel> _lootTagFrame = new();

    private volatile HoverPriceRender? _hoverPrice;

    private HoverPriceLabel? _hoverFrame;

    private readonly Pricing.TradeComparison _tradeComparison = new();
    private nint _tradeHoverItem;
    private DateTime _tradeHoverSince, _tradeProfileAt;
    private ItemTradeProfile? _tradeProfile;
    private volatile object? _hoverDiagnostic;

    // Waystone check cache: one mod read per hovered waystone (the item address changes when another is hovered).
    private nint _mapCheckItem;
    private (string Text, string Detail, bool Danger) _mapCheckView;
    private int _mapCheckGen = -1;

    // ── Runeshape monoliths (priced offered rewards, read off the in-world device — works area-wide,
    //    before the panel is opened). World-space markers; published per area-hash for the zone-load guard. ──
    private readonly RuneMonolithCatalog _monoCatalog = RuneMonolithCatalog.Instance;

    private volatile MonolithRender _monoRender = MonolithRender.Empty;

    private readonly List<HpBarTarget> _hpFrame = new();

    private readonly List<ItemLabel> _itemFrame = new();

    /// <summary>API (/api/version): this build's version + the latest known on GitHub + a download URL.
    /// Lets the dashboard show an "update available" banner. Null-ish until the async check completes.</summary>
    /// <summary>PriceBook status for the dashboard ground-item panel.</summary>
    private object PricesJson() => new
    {
        loaded = _priceBook.IsLoaded,
        league = _priceBook.League,
        count = _priceBook.ItemCount,
        marketRows = _priceBook.MarketRowCount,
        categories = _priceBook.CategoryCount,
        fetchMilliseconds = _priceBook.FetchMilliseconds,
        hoverScanNodes = _live.HoverScanNodes,
        hoverScanReads = _live.HoverScanReads,
        hoverScanMilliseconds = _live.HoverScanMilliseconds,
        hoverCacheHits = _live.HoverCacheHits,
        hoveredItem = _hoverDiagnostic,
        exPerDivine = _priceBook.ExPerDivine,
        exPerChaos = _priceBook.ExPerChaos,
        lastFetchUtc = _priceBook.LastFetchUtc,
        status = _priceBook.Status,
        stale = _priceBook.IsStale,
        refreshIntervalMinutes = _priceBook.RefreshIntervalMinutes,
    };

    /// <summary>Read the open "Runeshape Combinations" panel (cheap when closed) and publish a priced label
    /// per visible reward (stack-total = unit × count, in Exalted, colored by value tier) for the renderer
    /// to draw on each row. Screen rects are scaled for the current game-window size. World thread.</summary>
    private void UpdateRuneforge(nint inGameState)
    {
        var cfg = _settings.GroundItems;   // shares the ground-item pricing toggle + league
        if (!cfg.Enabled || !_priceBook.IsLoaded)
        {
            if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
            return;
        }
        var rewards = _runeforge.ReadRewards(inGameState, _window.Width, _window.Height);
        if (!_runeforge.PanelOpen || rewards.Count == 0)
        {
            if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
            return;
        }
        var labels = new List<RuneLabel>(rewards.Count);
        foreach (var r in rewards)
        {
            if (_priceBook.TryByName(r.Name) is not { } pr) continue;     // unknown reward → no label
            var count = Math.Max(1, r.Count);
            var totalEx = pr.Exalted * count;
            // Show ONLY the value of the offer itself (full-stack value) — no "N×" count prefix, which
            // read confusingly next to the price (e.g. "2× greater chaos orbs" → just its value).
            var text = _priceBook.Format(totalEx);
            // Value tier (absolute Exalted): ≥5 ex green, <0.5 ex dim red, else amber.
            var color = totalEx >= 5.0 ? 0xFF66E066u : totalEx < 0.5 ? 0xFFE06666u : 0xFFE6C84Du;
            labels.Add(new RuneLabel(r.X, r.Y, r.W, r.H, text, color));
        }
        _runeRender = new RuneRender(labels.Count > 0, labels);
    }

    /// <summary>Read the open Currency Exchange (Kalguur market) order book (cheap when closed — the reader
    /// returns <c>Book.Closed</c> without a panel) and publish an aggregated depth ladder per side for the
    /// renderer to draw in the top-right corner. OFFERED ("I Have") is sorted best-ratio-first (Ratio
    /// descending), WANTED ("I Want") best-first too (Ratio ascending); the "&lt; rest" everything-below bucket
    /// is excluded from the drawn rows. Mirrors <see cref="UpdateRuneforge"/>'s gate→read→publish shape. World thread.</summary>
    private void UpdateCurrencyExchange(nint inGameState)
    {
        var cfg = _settings.CurrencyExchange;
        if (!cfg.Enabled)
        {
            if (!ReferenceEquals(_exchangeRender, ExchangeRender.Closed)) _exchangeRender = ExchangeRender.Closed;
            return;
        }
        var book = _exchange.Read(inGameState);
        if (!book.Open)
        {
            if (!ReferenceEquals(_exchangeRender, ExchangeRender.Closed)) _exchangeRender = ExchangeRender.Closed;
            return;
        }

        // Build the OFFERED ladder — the orders the user actually transacts with. Validated live 2026-06-29
        // (Research --exchange-read, Exalt→Divine): each OFFERED entry is a counterparty selling the user's
        // WANT currency. The user PAYS `Get` units of their HAVE currency and RECEIVES `Give` units of their
        // WANT currency, so rate = Get/Give = HAVE-per-WANT (LOWER is the better price for the user), and
        // `ListedCount` is that order's depth in HAVE currency (the cap on how much the user can pay into it).
        // Sort cheapest-rate-first (the order the game fills) and accumulate HAVE-currency depth. The "< rest"
        // everything-below bucket is dropped from the drawn rows.
        static List<ExchangeRow> BuildSellLadder(IReadOnlyList<Poe2CurrencyExchange.StockEntry> side)
        {
            var priced = new List<Poe2CurrencyExchange.StockEntry>(side.Count);
            foreach (var e in side) if (!e.IsRest && e.Get > 0 && e.Give > 0 && e.ListedCount > 0) priced.Add(e);
            priced.Sort((a, b) => a.Ratio.CompareTo(b.Ratio));   // Ratio = Get/Give = have-per-want; cheapest first
            var rows = new List<ExchangeRow>(priced.Count);
            long cum = 0;
            for (var i = 0; i < priced.Count; i++)
            {
                long depthHave = priced[i].ListedCount;          // depth in HAVE currency (what the user pays)
                cum += depthHave;
                rows.Add(new ExchangeRow(priced[i].Ratio, depthHave, cum, Recommended: i == 0, Give: priced[i].Give, Get: priced[i].Get));
            }
            return rows;
        }

        var offered = BuildSellLadder(book.Offered);
        // WANTED = competing buyers (people paying their HAVE for the user's WANT). For the spread display only,
        // express their bid in the SAME have-per-want unit: a WANTED entry GETS `Get` want-units and GIVES
        // `Give` have-units, so its bid = Give/Get. Highest bid first.
        var wanted = new List<ExchangeRow>();
        {
            var bids = new List<Poe2CurrencyExchange.StockEntry>(book.Wanted.Count);
            foreach (var e in book.Wanted) if (!e.IsRest && e.Get > 0 && e.Give > 0 && e.ListedCount > 0) bids.Add(e);
            bids.Sort((a, b) => (b.Give / (double)b.Get).CompareTo(a.Give / (double)a.Get));
            long cum = 0;
            foreach (var e in bids) { var rate = e.Give / (double)e.Get; cum += e.ListedCount; wanted.Add(new ExchangeRow(rate, e.ListedCount, cum, Recommended: false, Give: e.Give, Get: e.Get)); }
        }

        var bestOfferedRatio = offered.Count > 0 ? offered[0].Ratio : 0.0;   // best (cheapest) price the user pays
        var bestWantedRatio = wanted.Count > 0 ? wanted[0].Ratio : 0.0;      // best competing buy bid

        // Summary (API + subtitle source): best price each side + the spread between them, all have-per-want.
        var parts = new List<string>(3);
        if (offered.Count > 0) parts.Add($"buy @ {bestOfferedRatio:0.##}:1");
        if (wanted.Count > 0) parts.Add($"bid {bestWantedRatio:0.##}:1");
        if (offered.Count > 0 && wanted.Count > 0 && bestOfferedRatio > 0 && bestWantedRatio > 0)
        {
            var spreadPct = System.Math.Abs(bestOfferedRatio - bestWantedRatio) / System.Math.Min(bestOfferedRatio, bestWantedRatio) * 100.0;
            parts.Add($"spread {spreadPct:0.#}%");
        }

        // RECOMMENDED FILL (the headline): simulate the game filling the user's "I Have" quantity against the
        // sell ladder, cheapest tier first, accumulating the WANT units received (= pay / rate). Then recommend
        // the clean WHOLE-want-unit result: the HAVE spent to receive exactly `whole` want units, the blended
        // rate, and any HAVE left over. (e.g. 1000 exalt → "spend 974 → get 2  @ 487:1  ·  26 left".)
        var fillNote = "";
        var haveQty = book.HaveQty;
        if (offered.Count > 0)
        {
            if (haveQty <= 0)
            {
                fillNote = $"best @ {bestOfferedRatio:0.##}:1  ·  set 'I have' to size it";
            }
            else
            {
                long remaining = haveQty;   // HAVE-currency still to spend
                double received = 0;        // WANT-currency received (fractional)
                foreach (var r in offered)
                {
                    if (remaining <= 0) break;
                    long pay = System.Math.Min(remaining, r.Stock);   // r.Stock = HAVE-currency depth at this tier
                    if (r.Ratio > 0) received += pay / r.Ratio;       // want received = have paid / (have-per-want)
                    remaining -= pay;
                }
                var whole = (long)System.Math.Floor(received);
                if (whole <= 0)
                {
                    // not even one whole want-unit fills — show the fractional result + effective rate
                    var eff = received > 0 ? (haveQty - remaining) / received : bestOfferedRatio;
                    fillNote = $"{haveQty:N0} → {received:0.##}  @ {eff:0.##}:1";
                }
                else
                {
                    // HAVE needed for exactly `whole` want units: re-sweep, stopping once `whole` is reached.
                    long payForWhole = 0; double acc = 0;
                    foreach (var r in offered)
                    {
                        if (acc >= whole || r.Ratio <= 0) break;
                        var needWant = whole - acc;                                  // want units still needed
                        var canWant = System.Math.Min(needWant, r.Stock / r.Ratio);  // want available at this tier
                        payForWhole += (long)System.Math.Ceiling(canWant * r.Ratio); // have for that
                        acc += canWant;
                    }
                    var effRate = payForWhole / (double)whole;
                    var leftover = haveQty - payForWhole;
                    fillNote = leftover > 0
                        ? $"spend {payForWhole:N0} → get {whole:N0}  @ {effRate:0.##}:1  ·  {leftover:N0} left"
                        : $"spend {payForWhole:N0} → get {whole:N0}  @ {effRate:0.##}:1";
                }
            }
        }
        // Pin anchor: the exchange panel's screen rect (read live on its own element) so the renderer can
        // place the card at the panel's top-left. Falls back to (0,0) → renderer uses its default corner.
        float panelX = 0f, panelY = 0f;
        if (book.PanelAddr != 0 && _live.TryUiElementRect(book.PanelAddr, _window.Width, _window.Height, out var px, out var py, out _, out _))
        { panelX = px; panelY = py; }

        _exchangeRender = new ExchangeRender(true, offered, wanted, string.Join(" · ", parts), haveQty, fillNote,
            panelX, panelY, _settings.CurrencyExchange.Collapsed);
    }

    /// <summary>Read the open ritual tribute shop (cheap when closed — gated on a shop-signature text element)
    /// and publish a priced value label per offered reward for the renderer to draw on each tile. Uniques
    /// price by 2D-art basename, everything else by base-type name (same rule as ground items). Shares the
    /// ground-item pricing toggle + league + value floor. World thread.</summary>
    private void UpdateRitualRewards(nint inGameState)
    {
        var cfg = _settings.GroundItems;   // shares the ground-item pricing toggle + league + thresholds
        if (!cfg.Enabled || !_priceBook.IsLoaded)
        {
            if (!ReferenceEquals(_ritualRender, RitualRender.Closed)) _ritualRender = RitualRender.Closed;
            return;
        }
        var rewards = _live.ReadRitualRewards(inGameState, _window.Width, _window.Height);
        if (rewards.Count == 0)
        {
            if (!ReferenceEquals(_ritualRender, RitualRender.Closed)) _ritualRender = RitualRender.Closed;
            return;
        }
        var labels = new List<RitualLabel>(rewards.Count);
        foreach (var r in rewards)
        {
            // Uniques key off art (each has its own icon; an unID unique's name is hidden); everything else
            // keys off the base-type name (currency/omen tiers share one art). Same logic as BuildItemLabels.
            var pr = r.Rarity == Poe2Live.Rarity.Unique
                ? _priceBook.TryByArt(r.Art)
                : (r.Name is { Length: > 0 } nm ? _priceBook.TryByName(nm) : null);
            if (pr is not { } p) continue;                       // unknown reward → no label
            var text = _priceBook.Format(p.Exalted);
            // Value tier (absolute Exalted): ≥5 ex green, <0.5 ex dim red, else amber (same palette as runeforge).
            var color = p.Exalted >= 5.0 ? 0xFF66E066u : p.Exalted < 0.5 ? 0xFFE06666u : 0xFFE6C84Du;
            labels.Add(new RitualLabel(r.X, r.Y, r.W, r.H, text, color, p.Exalted >= cfg.HighlightMinEx));
        }
        _ritualRender = new RitualRender(labels.Count > 0, labels);
    }

    /// <summary>Scan the visible UI tree for loot-tag text (world thread, THROTTLED + invisible-subtree
    /// pruned, so it's cheap) and match each tag's first line to a priced item by NAME — the tag's text IS
    /// the item name, so no item-entity link is needed. Publishes a spec per match (the tag's UiElement
    /// address + formatted value + highlight); the render thread reads each element's LIVE screen rect
    /// (<see cref="Poe2Live.TryUiElementRect"/> → game-computed, smooth, perfectly aligned) and draws a value
    /// chip on it. Covers everything the game already names — currency, runes, essences, fragments, IDENTIFIED
    /// uniques — leaving only UNIDENTIFIED uniques (name hidden by the game) to the world-projected reveal in
    /// <see cref="BuildItemLabels"/>. Cheap no-op when disabled or between throttle windows.</summary>
    private void UpdateLootTags(nint inGameState)
    {
        var cfg = _settings.GroundItems;
        var enabled = cfg.Categories is { Count: > 0 }
            ? new HashSet<string>(cfg.Categories, StringComparer.OrdinalIgnoreCase) : null;
        if (!cfg.Enabled || !cfg.AnchorValuesToTags || !_priceBook.IsLoaded || enabled is null)
        {
            if (!ReferenceEquals(_lootTags, LootTagRender.Empty)) _lootTags = LootTagRender.Empty;
            return;
        }

        // ScanLootLabels grabs EVERY visible UI text element, including the open "Runeshape Combinations"
        // (monolith) reward panel rows ("3x Glassblower's Bauble"). Those would be priced at the UNIT value
        // (StripCount drops the "3x") and chipped on top of the runeforge stack-total chip — two overlapping
        // labels. While that panel is open you're interacting with it, not the ground, so suppress ground
        // tags (UpdateRuneforge ran earlier this tick, so PanelOpen is current).
        if (_runeforge.PanelOpen)
        {
            if (!ReferenceEquals(_lootTags, LootTagRender.Empty)) _lootTags = LootTagRender.Empty;
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextLootScanUtc) return;   // between scans: keep the last published specs (render re-reads rects live)
        _nextLootScanUtc = now.AddMilliseconds(LootScanThrottleMs);

        var tags = _live.ScanLootLabels(inGameState);
        if (tags.Count == 0)
        {
            if (!ReferenceEquals(_lootTags, LootTagRender.Empty)) _lootTags = LootTagRender.Empty;
            return;
        }

        var specs = new List<LootTagSpec>();
        var seen = new HashSet<nint>();
        foreach (var (el, text) in tags)
        {
            if (!seen.Add(el)) continue;
            // The tag's text is the item name; stacks may read "5x Chaos Orb" — try the raw line, then the
            // count-stripped name. Uniques are indexed by name too, so identified-unique tags resolve here.
            var pr = _priceBook.TryByName(text) ?? _priceBook.TryByName(StripCount(text));
            if (pr is not { } p) continue;
            var group = CategoryGroup(p.Category);
            if (!enabled.Contains(group)) continue;          // category group toggled off
            if (p.Exalted < GroundFloor(group)) continue;    // below this bucket's value floor
            // Low-listing confidence: a price backed by fewer than MinQuantity live listings is flagged
            // with a "?" rather than hidden (0 volume = no data, not low confidence — see LowConfidence).
            var value = _priceBook.Format(p.Exalted);
            if (p.LowConfidence(cfg.MinQuantity)) value += " ?";
            specs.Add(new LootTagSpec(el, text, value, p.Exalted >= cfg.HighlightMinEx));
        }
        _lootTags = specs.Count > 0 ? new LootTagRender(specs) : LootTagRender.Empty;
    }

    /// <summary>Hover price: resolve the item under the cursor in any item UI (inventory/stash/vendor),
    /// price it (uniques by art — works on unidentified ones — everything else by base name), and publish a
    /// <see cref="HoverPriceSpec"/> the render thread anchors beside the game tooltip. Ignores the ground
    /// value floors/toggles (hovering is explicit intent). Stacks carry per-unit + total. Throttled + gated
    /// to PoE2 foreground; publishes null when no item is hovered. World thread.</summary>
    private void UpdateHoverPrice(nint inGameState, uint areaHash)
    {
        var cfg = _settings.HoverPrice;
        if (!cfg.Enabled)
        {
            if (_hoverPrice != null) _hoverPrice = null;
            return;
        }
        if (!GameFocused()) { _hoverPrice = null; return; }
        var now = DateTime.UtcNow;
        if (now < _nextHoverScanUtc) return;
        _nextHoverScanUtc = now.AddMilliseconds(HoverScanThrottleMs);

        // Cursor in overlay-client pixels (same space as TryUiElementRect); only while PoE2 is foreground.
        if (!GameFocused() || !GameHost.GetCursorPos(out var pt)) { _hoverPrice = null; return; }
        var (cx, cy) = ScreenToClientPoint(pt);
        var hov = _live.ReadHoveredItem(inGameState, _window.Width, _window.Height, cx, cy);
        if (hov is not { } h) { _hoverPrice = null; _hoverDiagnostic = null; _tradeHoverItem = 0; return; }

        if (!string.IsNullOrEmpty(h.Name)) _lastHover = new HoverRef(h.Name, now);
        var valuation = Pricing.HoverValuation.Build(h, _priceBook,
            _settings.GroundItems.MinQuantity, cfg.HighlightMinEx);
        if (h.Item != _tradeHoverItem)
        {
            _tradeHoverItem = h.Item; _tradeHoverSince = now; _tradeProfile = null;
        }
        var danger = false;
        if (_settings.MapCheck.Enabled && MapCheckFor(h) is { } mc)
        {
            danger = mc.Danger;
            valuation = valuation with
            {
                Text = mc.Text,
                Detail = mc.Detail + (valuation.Text.Length > 0 ? "\n" + valuation.Text : ""),
                Highlight = false,
            };
        }
        else if (h.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic)
        {
            // Same closest-match search as the price-check hotkey, so the two always agree (cached per item).
            string text = "Hold to compare with similar listings", detail = "Reading the item's mods…";
            string? url = null;
            if ((now - _tradeHoverSince).TotalMilliseconds >= 500)
            {
                if (_tradeProfile == null || (now - _tradeProfileAt).TotalMilliseconds >= 750)
                {
                    _tradeProfile = _live.ReadItemTradeProfile(h); _tradeProfileAt = now;
                }
                var (key, needsStats, plan) = Pricing.PriceCheck.For(_tradeProfile, null, _live.ItemMetadata(h.Item));
                var search = _tradeComparison.GetOrQueueSearch(key, plan, needsStats, _priceBook);
                url = search.Url;
                if (search.Pending) { text = "Checking similar listings…"; detail = search.Status; }
                else if (search.Error is { } err) { text = "Trade lookup unavailable"; detail = err; }
                else
                {
                    var sum = Pricing.PriceCheck.Summarize(search.Listings, null, _priceBook.Format, similar: true);
                    text = sum.Median is { } median ? $"Similar items: ~{_priceBook.Format(median)}" : search.Status;
                    detail = search.Note + (sum.Min is { } lo && sum.Max is { } hi
                        ? $"\n{_priceBook.Format(lo)}–{_priceBook.Format(hi)} across {search.Listings.Count} sellers · {search.Total:N0} listed"
                        : "");
                }
            }
            valuation = valuation with
            {
                Text = text,
                Detail = $"{h.Name}\n{detail}\nOfficial trade · {_priceBook.ComparisonLeague}\n{_settings.HoverPrice.PriceCheckHotkey}: full price check",
                TradeUrl = url ?? valuation.TradeUrl,
                Highlight = false,
            };
        }
        _hoverDiagnostic = new { name = h.Name, rarity = h.Rarity.ToString(), art = h.Art,
            identified = h.Identified, status = valuation.Text, detail = valuation.Detail };
        _hoverPrice = new HoverPriceRender(new HoverPriceSpec(
            h.BoxX, h.BoxY, h.BoxW, h.BoxH, valuation.Text, valuation.Detail, valuation.Highlight, danger), areaHash, now);
    }

    /// <summary>Strip a leading "&lt;count&gt;x " from a stack tag ("5x Chaos Orb" → "Chaos Orb") so the name
    /// matches the PriceBook key; returns the input trimmed when there's no count prefix.</summary>
    /// <summary>World thread: waystone mod check for the hovered item, or null when it isn't a waystone. Mods
    /// are read once per hovered waystone (and again only if the dangerous-mod list is edited).</summary>
    private (string Text, string Detail, bool Danger)? MapCheckFor(Poe2Live.HoveredItem h)
    {
        var gen = _settings.MapCheck.Dangerous.Count ^ string.Join("|", _settings.MapCheck.Dangerous).GetHashCode();
        if (h.Item == _mapCheckItem && gen == _mapCheckGen) return _mapCheckView.Text.Length > 0 ? _mapCheckView : null;
        _mapCheckItem = h.Item; _mapCheckGen = gen;
        _mapCheckView = default;
        if (!Pricing.MapCheck.IsWaystone(_live.ItemMetadata(h.Item), h.Name)) return null;
        var result = Pricing.MapCheck.Check(_live.ReadItemTradeProfile(h), _settings.MapCheck.Dangerous);
        _mapCheckView = Pricing.MapCheck.Describe(h.Name ?? "Waystone", result);
        return _mapCheckView;
    }

    private static string StripCount(string raw)
    {
        var name = raw?.Trim() ?? "";
        var i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        return i > 0 && i < name.Length && (name[i] == 'x' || name[i] == 'X')
            ? name[(i + 1)..].TrimStart() : name;
    }

    /// <summary>Resolve every runeshape-monolith device in the area (the persistent Expedition2Encounter
    /// POI entities, already in <see cref="_entities"/>), read its hole count + anchor rune off the device→
    /// station chain, compute the rewards it will offer (<see cref="RuneMonolithCatalog"/>, level-gated),
    /// price each via the PriceBook, and publish a value-coloured <see cref="MonolithRender"/> bundle. Works
    /// area-wide and BEFORE the panel is opened (the station persists out of the network bubble).</summary>
    private void UpdateMonoliths(nint areaInstance, int areaLevel, uint areaHash)
    {
        var cfg = _settings.Monoliths;
        if (!cfg.Enabled || !_priceBook.IsLoaded || !_monoCatalog.IsLoaded)
        {
            if (_monoRender.Markers.Count > 0) _monoRender = MonolithRender.Empty;
            return;
        }

        var markers = new List<MonolithMarker>();
        foreach (var e in _entities)
        {
            if (e.Metadata.IndexOf("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = _live.ReadMonolith(e.Address);
            if (!m.Resolved) continue;
            if (cfg.HideCollected && m.Collected) continue;

            var offers = _monoCatalog.Offers(m.AnchorIdx, m.AnchorPos, m.HoleCount, m.IsUnique, areaLevel);
            var rewards = new List<MonolithReward>(offers.Count);
            double best = 0; var bestName = "";
            foreach (var o in offers)
            {
                var ex = o.Name.Length > 0 && _priceBook.TryByName(o.Name) is { } pr ? pr.Exalted * Math.Max(1, o.Count) : 0;
                rewards.Add(new MonolithReward(o.Name.Length > 0 ? o.Name : o.Description, o.Count, ex, o.Size, o.Runes));
                if (ex > best) { best = ex; bestName = o.Name; }
            }
            // Whole-structure value gate: below the floor, surface nothing (no icon/panel) — and, because
            // BuildNavTargets keys auto-pathing off a surviving marker, no auto-route either.
            if (cfg.MinValueEx > 0 && best < cfg.MinValueEx) continue;
            rewards.Sort((a, b) => b.Ex.CompareTo(a.Ex));

            var anchor = m.IsUnique ? "Unique" : m.AnchorIdx >= 0 ? _monoCatalog.RuneName(m.AnchorIdx) : "?";
            markers.Add(new MonolithMarker(
                e.Grid, m.HoleCount, m.IsUnique, m.Collected, anchor, best, bestName,
                MonolithColor(best, cfg.HighlightMinEx), rewards));
        }
        _monoRender = new MonolithRender(areaHash, markers);
    }

    /// <summary>Value tier for a monolith's best reward (packed 0xAARRGGBB): green at/above the threshold,
    /// yellow from 0.6×, neutral white below (or when nothing priced). Mirrors the ground-item tiers.</summary>
    private static uint MonolithColor(double bestEx, double threshold)
    {
        if (bestEx <= 0 || threshold <= 0) return 0xFFFFFFFFu;
        if (bestEx >= threshold) return 0xFF66E066u;          // green
        if (bestEx >= 0.6 * threshold) return 0xFFE6C84Du;    // amber
        return 0xFFFFFFFFu;                                   // neutral
    }

    /// <summary>Decide which monsters get an HP bar and precompute each bar's style (width + packed
    /// fill/border colours) at WORLD rate. This is the work that used to run per entity per render frame in
    /// the renderer (rarity gate + rule resolve + colour parse); doing it once per world tick — only for
    /// mobs with a live HP pool — leaves the render-frame path to just re-read position/HP and draw, which
    /// is what keeps 50–100 bars smooth without re-resolving thousands of entities every frame.</summary>
    private List<HpBarSpec> BuildHpSpecs()
    {
        var specs = new List<HpBarSpec>();
        var hb = _settings.HpBars;
        foreach (var e in _entities)
        {
            if (!e.IsAlive || e.HpMax <= 0) continue;                 // needs a live HP pool
            var on = e.Rarity switch                                   // per-rarity master toggle (Settings)
            {
                Poe2Live.Rarity.Normal => _settings.HpBarNormal,
                Poe2Live.Rarity.Magic  => _settings.HpBarMagic,
                Poe2Live.Rarity.Rare   => _settings.HpBarRare,
                Poe2Live.Rarity.Unique => _settings.HpBarUnique,
                _                      => false,
            };
            if (!on) continue;
            var rule = _displayRules.Resolve(e);
            if (rule is null || rule.Hide) continue;                   // no bars over hidden mobs
            var (bw, fillHex, borderW, borderHex) = e.Rarity switch    // geometry per rarity; fill = dot colour
            {
                Poe2Live.Rarity.Normal => (hb.WidthNormal, rule.Color, hb.BorderNormal, hb.BorderColorNormal),
                Poe2Live.Rarity.Magic  => (hb.WidthMagic,  rule.Color, hb.BorderMagic,  hb.BorderColorMagic),
                Poe2Live.Rarity.Rare   => (hb.WidthRare,   rule.Color, hb.BorderRare,   hb.BorderColorRare),
                Poe2Live.Rarity.Unique => (hb.WidthUnique, rule.Color, hb.BorderUnique, hb.BorderColorUnique),
                _                      => (0f, "#FFFFFF", 0f, "#FFFFFF"),
            };
            if (bw <= 0f) continue;
            // Capture the mob's Render/Life component addresses (resolved by the Entities() walk just now,
            // on THIS world reader) so the render thread can read live pos/HP off its own reader stack.
            if (!_live.TryBarComponents(e.Address, out var render, out var life)) continue;
            specs.Add(new HpBarSpec(e.Address, render, life, bw, PackColor(fillHex), borderW, PackColor(borderHex)));
        }
        return specs;
    }

    /// <summary>
    /// Build the priced ground-item label set (world rate): for each dropped UNIQUE (its art basename
    /// read by Poe2Live), look up the name + Exalted value in the PriceBook and emit a label at the item's
    /// world position. The label shows the resolved unique name (so UNIDENTIFIED uniques reveal what they
    /// are) + value, and flags Highlight when the value clears the configured threshold (→ border). Gated
    /// by the GroundItems setting; cheap (a dictionary lookup per drop, no memory reads here).
    /// </summary>
    private List<ItemLabelSpec> BuildItemLabels()
    {
        var labels = new List<ItemLabelSpec>();
        var cfg = _settings.GroundItems;
        if (!cfg.Enabled || !_priceBook.IsLoaded) return labels;
        // User-enabled value categories (group keys). Empty ⇒ nothing shows.
        var enabled = cfg.Categories is { Count: > 0 }
            ? new HashSet<string>(cfg.Categories, StringComparer.OrdinalIgnoreCase) : null;
        if (enabled is null) return labels;
        foreach (var e in _entities)
        {
            // Needs at least an art (uniques) or a rendered name (everything else) to resolve.
            if (e.ItemArt is not { Length: > 0 } && e.ItemName is not { Length: > 0 }) continue;

            // RESOLVE by the rendered base NAME for everything EXCEPT uniques. Currency/runes/essences share
            // one .dds art across tiers, so art mis-prices them (a plain Exalted read as Perfect's 678ex);
            // the exact name does not. UNIQUES use art — each unique has its own icon (no collision) AND an
            // unidentified unique's name is hidden by the game, so art is the only key that works for them.
            var isUnique = e.Rarity == Poe2Live.Rarity.Unique;
            // When values are anchored to the game's loot tags, the world-projected label is reserved for
            // UNIDENTIFIED uniques only — the game hides their name, so there's no tag text to match. Every
            // other priced drop (currency/runes/essences/identified uniques) is drawn on its loot tag in
            // UpdateLootTags instead, so skip it here to avoid a double label.
            if (cfg.AnchorValuesToTags && !(isUnique && !e.ItemIdentified)) continue;
            var lookup = isUnique
                ? _priceBook.TryByArt(e.ItemArt)
                : (e.ItemName is { Length: > 0 } nm ? _priceBook.TryByName(nm) : null);
            if (lookup is not { } pr) continue;
            var group = CategoryGroup(pr.Category);
            if (!enabled.Contains(group)) continue;                 // category group toggled off
            if (pr.Exalted < GroundFloor(group)) continue;          // below this bucket's value floor (Unique/Currency/Other)
            if (!_live.TryBarComponents(e.Address, out var render, out _)) continue;
            // Reveal the NAME only for an UNIDENTIFIED unique (the game's tag hides it). Everything else —
            // identified uniques, currency, runes, essences, … — draws a value-only chip over the drop.
            var showName = isUnique && !e.ItemIdentified;
            // Low-listing confidence: flag a price backed by a positive-but-sub-threshold listing count
            // with a "?" rather than hiding it (see LowConfidence — 0 volume = no data, still trusted).
            var value = _priceBook.Format(pr.Exalted);
            if (pr.LowConfidence(cfg.MinQuantity)) value += " ?";
            labels.Add(new ItemLabelSpec(render, pr.Name, value, pr.Exalted >= cfg.HighlightMinEx, ShowName: showName));
        }
        return labels;
    }

    /// <summary>Map a PriceBook category (the poe.ninja overview TYPE string — "Currency", "UniqueWeapons",
    /// "SoulCores", …) to the user-facing ground-item GROUP key (<see cref="GroundItemSettings.Categories"/>).
    /// The unique sub-types collapse to "Uniques"; the rest pass through. (Earlier this switched on poe2scout's
    /// lowercase names and so returned "Other" for every poe.ninja type — the bug that hid all non-uniques.)</summary>
    private static string CategoryGroup(string category)
    {
        if (category.StartsWith("Unique", StringComparison.OrdinalIgnoreCase)) return "Uniques";
        return category switch
        {
            "PrecursorTablets" => "Tablets",
            "Currency"   => "Currency",
            "Runes"      => "Runes",
            "SoulCores"  => "SoulCores",
            "Essences"   => "Essences",
            "Fragments"  => "Fragments",
            "UncutGems"  => "UncutGems",
            "Delirium"   => "Delirium",
            "Breach"     => "Breach",
            "Ritual"     => "Ritual",
            "Abyss"      => "Abyss",
            "Expedition" => "Expedition",
            "Verisium"   => "Verisium",
            "Idols"      => "Idols",
            "LineageSupportGems" => "Gems",
            _ => "Other",
        };
    }

    /// <summary>The Exalted value FLOOR for a group, from its threshold bucket (Uniques / Currency / Other).</summary>
    private double GroundFloor(string group) => group switch
    {
        "Uniques"  => _settings.GroundItems.UniqueMinEx,
        "Currency" => _settings.GroundItems.CurrencyMinEx,
        _          => _settings.GroundItems.OtherMinEx,
    };

    /// <summary>Parse a "#RRGGBB" hex colour to packed 0xFFRRGGBB once (opacity = 1, matching the old
    /// per-frame ParseColor(hex, 1f) for HP bars). Falls back to opaque white on a malformed string.</summary>
    private static uint PackColor(string hex)
    {
        if (hex is { Length: >= 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        return 0xFFFFFFFFu;
    }
}
