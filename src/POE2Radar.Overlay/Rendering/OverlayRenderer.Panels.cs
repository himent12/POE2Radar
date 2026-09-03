using System.Globalization;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

public sealed partial class OverlayRenderer
{
    /// <summary>
    /// World-space HP bars over monsters, projected via the camera WorldToScreen matrix. Drawn whether
    /// or not the big map is open (it's a heads-up combat overlay). HP bars are a MONSTER-ONLY concept,
    /// gated entirely by the per-rarity on/off toggles in Settings (HpBarNormal/Magic/Rare/Unique) — they
    /// are NOT a display-rule concern. The resolved rule is still consulted for two things: it must not be
    /// a Hide rule (no bars over hidden mobs), and the bar FILL follows the mob's dot color. Bar GEOMETRY
    /// (width/border/offset) is per-rarity from HpBars.
    /// </summary>
    private void DrawNameplates(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.HpBarTargets is not { Count: > 0 } bars) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var hb = ctx.HpBars;
        var bh = hb.Height;
        // All the expensive per-entity decisions (rarity gate, rule resolve, colour parse) were done at
        // world rate in RadarApp.BuildHpSpecs; here we only project the LIVE position (refreshed this frame
        // so the bar tracks the moving mob) and fill. fill/border are pre-packed 0xAARRGGBB.
        foreach (var t in bars)
        {
            var w = t.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var bw = t.Width;
            var bx = sx - bw / 2f + hb.OffsetX;
            var by = sy + hb.OffsetY; // OffsetY is relative to the mob (negative = above)
            var barRect = new RawRectF(bx, by, bx + bw, by + bh);
            rt.FillRectangle(barRect, _bPanel!);
            _bStyle!.Color = t.Frac < 0.3f ? ColLowHp : ColorFromU(t.Fill);
            rt.FillRectangle(new RawRectF(bx, by, bx + bw * t.Frac, by + bh), _bStyle);
            if (t.BorderWidth > 0f)
            {
                _bStyle.Color = ColorFromU(t.Border);
                rt.DrawRectangle(barRect, _bStyle, t.BorderWidth);
            }
        }
    }

    /// <summary>
    /// Priced unique ground-item labels drawn over their in-world loot icons (projected via the camera
    /// WorldToScreen matrix, same as HP bars). Each label shows the resolved unique NAME (revealing
    /// unidentified uniques) + value on a backing panel; items above the value threshold get a gold
    /// border. Drawn whether the big map is open or not — it's a heads-up loot overlay.
    /// </summary>
    private void DrawItemLabels(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.ItemLabels is not { Count: > 0 } labels) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        foreach (var it in labels)
        {
            var w = it.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;                       // behind the camera
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            if (it.ShowName)
            {
                // UNIDENTIFIED unique: two stacked lines — resolved NAME over VALUE — on a backing panel
                // (the game hides the unID name, so we reveal it). Border when high-value.
                var text = $"{it.Name}\n{it.Value}";
                var halfW = MathF.Max(48f, 4.5f * MathF.Max(it.Name.Length, it.Value.Length + 3));
                const float halfH = 19f;
                var panel = new RawRectF(sx - halfW, sy - halfH, sx + halfW, sy + halfH);
                rt.FillRectangle(panel, _bPanel!);
                if (it.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(panel, _bStyle, 2.5f); }
                _bStyle!.Color = it.Highlight ? ColItemHi : ColItemText;
                rt.DrawText(text, _tf!, new Rect(sx - halfW + 4f, sy - halfH + 2f, sx + halfW - 2f, sy + halfH - 1f),
                    _bStyle, DrawTextOptions.Clip);
            }
            else
            {
                // Identified uniques + runes/essences/currency/…: VALUE-only compact chip (the game already
                // shows the item's name on its loot tag). Border when high-value.
                var halfW = MathF.Max(26f, 4.5f * (it.Value.Length + 1));
                const float halfH = 11f;
                var panel = new RawRectF(sx - halfW, sy - halfH, sx + halfW, sy + halfH);
                rt.FillRectangle(panel, _bPanel!);
                if (it.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(panel, _bStyle, 2f); }
                _bStyle!.Color = it.Highlight ? ColItemHi : ColItemText;
                rt.DrawText(it.Value, _tf!, new Rect(sx - halfW + 3f, sy - halfH + 1f, sx + halfW - 2f, sy + halfH),
                    _bStyle, DrawTextOptions.Clip);
            }
        }
    }

    /// <summary>Rune-crafting reward prices: a small value box just outside the right edge of each visible
    /// reward row in the "Runeshape Combinations" panel. Rects are screen-space (already scaled in
    /// Poe2Runeforge); text + tier color are precomputed in RadarApp. Screen-space, so no world projection.</summary>
    private void DrawRuneforge(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.RuneLabels is not { Count: > 0 } labels) return;
        const float gap = 8f, boxW = 96f, boxH = 22f;
        foreach (var r in labels)
        {
            var lx = r.X + r.W + gap;             // just past the row's right edge
            var cy = r.Y + r.H * 0.5f;            // vertically centered on the row
            var box = new RawRectF(lx, cy - boxH * 0.5f, lx + boxW, cy + boxH * 0.5f);
            rt.FillRectangle(box, _bPanel!);
            _bStyle!.Color = ColorFromU(r.Color);
            rt.DrawText(r.Text, _tf!, new Rect(lx + 5f, cy - boxH * 0.5f + 2f, lx + boxW - 2f, cy + boxH * 0.5f - 1f),
                _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>Ritual tribute-shop reward values: a value chip centered on the bottom edge of each reward
    /// tile in the open shop. Rects are screen-space (already scaled in Poe2Live.ReadRitualRewards); text +
    /// tier color are precomputed in RadarApp. High-value rewards get a gold border. No world projection.</summary>
    private void DrawRitualRewards(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.RitualRewards is not { Count: > 0 } labels) return;
        const float boxH = 20f;
        foreach (var r in labels)
        {
            var boxW = MathF.Max(44f, 7.5f * (r.Text.Length + 1));
            var cx = r.X + r.W * 0.5f;
            var top = r.Y + r.H - boxH;            // sit on the tile's bottom edge
            var box = new RawRectF(cx - boxW * 0.5f, top, cx + boxW * 0.5f, top + boxH);
            rt.FillRectangle(box, _bPanel!);
            if (r.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(box, _bStyle, 2f); }
            _bStyle!.Color = ColorFromU(r.Color);
            rt.DrawText(r.Text, _tf!, new Rect(box.Left + 3f, top + 1f, box.Right - 2f, top + boxH - 1f),
                _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>Value chips drawn ON the game's own loot tags. Each rect is the tag's LIVE screen rect
    /// (from Poe2Live.TryUiElementRect, re-read per frame in RadarApp) — game-computed, so the chip tracks
    /// the tag exactly with no world projection and no jitter. Covers items the game already names (currency,
    /// runes, essences, fragments, identified uniques); unidentified uniques use the world-projected reveal
    /// in DrawItemLabels. High-value matches get a gold border (same palette as the item labels).</summary>
    private void DrawLootTags(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.LootTags is not { Count: > 0 } labels) return;
        const float gap = 6f, boxH = 18f;
        foreach (var t in labels)
        {
            var lx = t.X + t.W + gap;             // just past the tag's right edge
            var cy = t.Y + t.H * 0.5f;            // vertically centered on the tag
            var boxW = MathF.Max(40f, 7.5f * (t.Value.Length + 1));
            var box = new RawRectF(lx, cy - boxH * 0.5f, lx + boxW, cy + boxH * 0.5f);
            rt.FillRectangle(box, _bPanel!);
            if (t.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(box, _bStyle, 2f); }
            _bStyle!.Color = t.Highlight ? ColItemHi : ColItemText;
            rt.DrawText(t.Value, _tf!, new Rect(lx + 4f, cy - boxH * 0.5f + 1f, lx + boxW - 2f, cy + boxH * 0.5f - 1f),
                _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>Price bar for the item under the cursor: a single-line styled bar aligned to the game
    /// tooltip's content box (same left + width), drawn just below the tooltip. If the tooltip sits near the
    /// bottom of the screen the bar snaps to just ABOVE its top edge instead. Border/emphasis when highlighted.</summary>
    private void DrawHoverPrice(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.HoverPrice is not { } hp) return;
        const float rowH = 17f, gap = 3f, pad = 7f, charW = 7.3f;  // charW ≈ Consolas 12px advance
        var twoRow = !string.IsNullOrEmpty(hp.Sub);
        var chipH = (twoRow ? 2f * rowH : rowH) + 6f;
        // Chip width fits the longer line (monospace ⇒ width ≈ char count); wide enough for all the text.
        var maxLen = Math.Max(hp.Text.Length, hp.Sub.Length);
        var w = MathF.Max(56f, maxLen * charW + 2f * pad);

        // Anchor to the item icon: centre the chip on the slot, just below it; flip above if no room below,
        // and clamp inside the screen so it never runs off an edge.
        var x = hp.X + hp.W * 0.5f - w * 0.5f;
        x = Math.Clamp(x, 2f, ctx.WindowWidth - w - 2f);
        var y = hp.Y + hp.H + gap;
        if (y + chipH > ctx.WindowHeight - 2f) y = hp.Y - chipH - gap;
        if (y < 2f) y = 2f;

        var box = new RawRectF(x, y, x + w, y + chipH);
        rt.FillRectangle(box, _bPanel!);
        _bStyle!.Color = hp.Highlight ? ColItemHi : ColItemText;
        rt.DrawRectangle(box, _bStyle, hp.Highlight ? 2f : 1f);
        // Row 1: stack total (emphasis colour). Row 2 (stacks only): per-unit, in dimmer white.
        rt.DrawText(hp.Text, _tf!, new Rect(x + pad, y + 3f, x + w - pad, y + rowH + 3f), _bStyle, DrawTextOptions.Clip);
        if (twoRow)
            rt.DrawText(hp.Sub, _tf!, new Rect(x + pad, y + rowH + 3f, x + w - pad, y + chipH - 2f), _bText!, DrawTextOptions.Clip);
    }

    /// <summary>Unpack a 0xAARRGGBB color (precomputed in RadarApp.BuildHpSpecs) to a Color4 — no string
    /// parse or allocation, runs per bar per frame.</summary>
    private static Color4 ColorFromU(uint u)
        => new(((u >> 16) & 0xFF) / 255f, ((u >> 8) & 0xFF) / 255f, (u & 0xFF) / 255f, ((u >> 24) & 0xFF) / 255f);

    /// <summary>Runeshape-monolith map markers: a value-coloured ring with the hole count N inside, and a
    /// "{best} ex · {reward}" label to the right. Drawn on the big map (grid → screen via the same
    /// projection as entity dots / landmarks). Augments the generic POI dot with the monolith's value.</summary>
    private void DrawMonoliths(DrawTarget rt, RenderContext ctx, NumVec2 player, NumVec2 center, float scale)
    {
        if (ctx.Monoliths is not { Count: > 0 } monos) return;
        foreach (var m in monos)
        {
            var p = Project(new NumVec2(m.Grid.X, m.Grid.Y), player, center, scale);
            _bStyle!.Color = ColorFromU(m.Color);
            rt.DrawEllipse(new Ellipse(p, 9f, 9f), _bStyle, 2.4f);          // value-coloured ring
            rt.DrawText(m.Holes.ToString(), _tf!,                           // N badge (white) inside the ring
                new Rect(p.X - 4f, p.Y - 8f, p.X + 10f, p.Y + 8f), _bText!, DrawTextOptions.Clip);
            var label = m.BestEx > 0 ? $"{m.BestEx:F0}ex · {m.BestName}" : $"{m.AnchorName} {m.Holes}h";
            rt.DrawText(label, _tf!, new Rect(p.X + 13f, p.Y - 8f, p.X + 340f, p.Y + 9f), _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>The nearby-monolith reward panel: a screen-space list (top-right) of the area's monoliths
    /// sorted by best value, each with its anchor + N + top priced rewards. Draws even with the big map
    /// closed (the values are read area-wide off the persistent devices).</summary>
    private void DrawMonolithPanel(DrawTarget rt, RenderContext ctx)
    {
        if (!ctx.ShowMonolithPanel || ctx.Monoliths is not { Count: > 0 } monos) return;
        const float w = 248f, pad = 6f, lineH = 15f, headH = 17f, titleH = 18f;
        float x = ctx.WindowWidth - w - 10f, y = 90f;

        // Title bar doubles as a click target ("> "/"v " caret like the nav menu) — clicking it toggles
        // the collapsed state (persisted by RadarApp). Registered after DrawNavMenu's rect clear, so it
        // survives and is hit-tested for both clicks and the click-through gate.
        var collapsed = ctx.MonolithPanelCollapsed;
        var caret = collapsed ? "> " : "v ";

        if (collapsed)
        {
            // Collapsed: just the clickable header bar, keeping the count visible for quick reference.
            float ch = pad * 2f + titleH;
            rt.FillRectangle(new RawRectF(x, y, x + w, y + ch), _bPanel!);
            rt.DrawText($"{caret}Monoliths ({monos.Count})", _tf!, new Rect(x + pad, y + pad, x + w - pad, y + pad + titleH), _bText!, DrawTextOptions.Clip);
            _legendRowRects.Add((new RawRectF(x, y, x + w, y + ch), "mono-collapse"));
            return;
        }

        var list = monos.OrderByDescending(m => m.BestEx).Take(6).ToList();

        float h = pad * 2f + titleH;
        foreach (var m in list)
        {
            var rows = 0; foreach (var r in m.Rewards) if (r.Ex > 0 && rows < 3) rows++;
            h += headH + lineH * rows;
        }
        rt.FillRectangle(new RawRectF(x, y, x + w, y + h), _bPanel!);

        float cy = y + pad;
        rt.DrawText($"{caret}Monoliths ({monos.Count})", _tf!, new Rect(x + pad, cy, x + w - pad, cy + titleH), _bText!, DrawTextOptions.Clip);
        _legendRowRects.Add((new RawRectF(x, cy, x + w, cy + titleH), "mono-collapse"));
        cy += titleH;
        foreach (var m in list)
        {
            _bStyle!.Color = ColorFromU(m.Color);
            var hdr = m.BestEx > 0 ? $"{m.BestEx:F0}ex · {m.AnchorName} {m.Holes}h" : $"{m.AnchorName} {m.Holes}h";
            rt.DrawText(hdr, _tf!, new Rect(x + pad, cy, x + w - pad, cy + headH), _bStyle, DrawTextOptions.Clip);
            cy += headH;
            var shown = 0;
            foreach (var r in m.Rewards)
            {
                if (r.Ex <= 0 || shown >= 3) continue;
                rt.DrawText($"  {r.Ex,4:F0}  {r.Name}", _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH), _bText!, DrawTextOptions.Clip);
                cy += lineH; shown++;
            }
        }
    }

    /// <summary>The Currency Exchange "Sell Guide": a TOP-RIGHT panel (only while the exchange is open) that
    /// answers "what ratio do I list at to get the best return for what I'm selling?". The WANTED side is the
    /// RECEIVE ladder (your counterparties — what you get per unit sold); rows are best-ratio-first, shown as
    /// "list ≥ {ratio} → move up to {cumulative}". The best ratio (thin top — only a little fills there) is
    /// green; the deepest single tier (best ratio you can actually move size at) is amber. A one-line note
    /// shows the competition (other sellers, the OFFERED side's best). Screen-space; mirrors DrawRuneforge's
    /// brush/text reuse. NOTE: volume units (raw ListedCount vs ×Give/Get) pending in-game validation.</summary>
    private void DrawCurrencyExchange(DrawTarget rt, RenderContext ctx)
    {
        if (!ctx.ExchangeOpen) return;
        // OFFERED = orders giving your have-item for your want-item = your actual SELL ladder (the ≤market
        // rows the game shows). WANTED = the opposite (buy) side. Both already in SELL units (RadarApp).
        var sell = ctx.ExchangeOffered ?? (IReadOnlyList<ExchangeRow>)Array.Empty<ExchangeRow>();
        var buy = ctx.ExchangeWanted ?? (IReadOnlyList<ExchangeRow>)Array.Empty<ExchangeRow>();
        if (sell.Count == 0) return;

        const float pad = 10f, rowH = 16f, titleH = 17f, subH = 14f, heroH = 18f, headH = 14f, sepH = 6f;
        const float w = 320f, tabW = 122f, tabH = 22f;
        // Pin to the exchange window: the card/tab sits just LEFT of the panel, top-aligned (fall back to the
        // top-right corner when the panel screen rect isn't available).
        bool pinned = ctx.ExchangePanelX > 1f;
        float anchorY = pinned ? ctx.ExchangePanelY : 10f;

        // Collapsed → a small "expand" tab; clicking it reopens (action "exchange-collapse").
        if (ctx.ExchangeCollapsed)
        {
            float tx = pinned ? Math.Max(4f, ctx.ExchangePanelX - tabW - 8f) : ctx.WindowWidth - tabW - 10f;
            var tab = new RawRectF(tx, anchorY, tx + tabW, anchorY + tabH);
            rt.FillRectangle(tab, _bPanel!);
            _bStyle!.Color = ColExchangeGold;
            rt.FillRectangle(new RawRectF(tx, anchorY + tabH - 2f, tx + tabW, anchorY + tabH), _bStyle);  // gold underline
            _bStyle.Color = ColExchangeFill;
            rt.DrawText("+ Sell Guide", _tf!, new Rect(tx + 7f, anchorY + 2f, tx + tabW - 4f, anchorY + tabH), _bStyle, DrawTextOptions.Clip);
            _legendRowRects.Add((tab, "exchange-collapse"));
            return;
        }

        const int maxRows = 6;
        var rowsShown = Math.Min(maxRows, sell.Count);
        var hasFill = ctx.ExchangeHaveQty > 0 && !string.IsNullOrEmpty(ctx.ExchangeFillNote);
        var top = sell[0];
        // The deepest tier (max marginal volume) gets the amber bar; scale all bars to the largest tier.
        var deepIdx = 0; long maxStock = 1;
        for (var i = 0; i < rowsShown; i++) if (sell[i].Stock > maxStock) { maxStock = sell[i].Stock; deepIdx = i; }

        float h = pad * 2f + titleH + subH + (hasFill ? heroH + sepH : 0f) + sepH + headH + rowH * rowsShown;
        float x = pinned ? Math.Max(4f, ctx.ExchangePanelX - w - 8f) : ctx.WindowWidth - w - 10f;
        float y = anchorY;
        rt.FillRectangle(new RawRectF(x, y, x + w, y + h), _bPanel!);
        float lx = x + pad, rx = x + w - pad, cy = y + pad;

        // X close button (collapses to the tab), top-right of the card.
        float bx = x + w - 17f;
        _bStyle!.Color = ColExchangeDim;
        rt.DrawText("X", _tf!, new Rect(bx, y + 2f, x + w, y + 18f), _bStyle, DrawTextOptions.Clip);
        _legendRowRects.Add((new RawRectF(bx - 3f, y + 2f, x + w, y + 19f), "exchange-collapse"));

        // Title + Kalguur-gold accent rule (the signature PoE touch); leave room for the X.
        rt.DrawText("Currency Exchange", _tf!, new Rect(lx, cy, bx - 4f, cy + titleH), _bText!, DrawTextOptions.Clip);
        cy += titleH;
        _bStyle.Color = ColExchangeGold;
        rt.FillRectangle(new RawRectF(lx, cy + 1f, rx, cy + 2.5f), _bStyle);
        cy += 4f;

        // Subtitle: best sell/buy ratios + spread, at a glance (warm grey).
        _bStyle.Color = ColExchangeDim;
        string sub;
        if (buy.Count > 0 && buy[0].Ratio > 0 && top.Ratio > 0)
        {
            var spreadPct = Math.Abs(top.Ratio - buy[0].Ratio) / Math.Min(top.Ratio, buy[0].Ratio) * 100.0;
            sub = $"buy @ {top.Ratio:0.##}:1  ·  bid {buy[0].Ratio:0.##}:1  ·  spread {spreadPct:0.#}%";
        }
        else sub = $"buy @ {top.Ratio:0.##}:1";
        rt.DrawText(sub, _tf!, new Rect(lx, cy, rx, cy + subH), _bStyle, DrawTextOptions.Clip);
        cy += subH;

        // Hero — the recommended sale ratio for the user's quantity (cyan, the one bold element).
        if (hasFill)
        {
            cy += sepH;
            _bStyle.Color = ColExchangeFill;
            rt.DrawText(ctx.ExchangeFillNote!, _tf!, new Rect(lx, cy, rx, cy + heroH), _bStyle, DrawTextOptions.Clip);
            cy += heroH;
        }

        // Depth ladder header + bars (the signature). ratio | bar∝volume | cumulative.
        cy += sepH;
        _bStyle.Color = ColExchangeDim;
        rt.DrawText("rate", _tf!, new Rect(lx, cy, lx + 52f, cy + headH), _bStyle, DrawTextOptions.Clip);
        rt.DrawText("depth →", _tf!, new Rect(rx - 96f, cy, rx, cy + headH), _bStyle, DrawTextOptions.Clip);
        cy += headH;

        const float ratioW = 52f, cumW = 52f, barGap = 6f;
        float barX = lx + ratioW + barGap, barMaxW = rx - cumW - barGap - barX;
        for (var i = 0; i < rowsShown; i++)
        {
            var r = sell[i];
            var rowCol = i == 0 ? ColExchangeRec : i == deepIdx ? ColExchangeVol : ColText;
            // bar ∝ this tier's marginal volume; best=green, deepest=amber, else bronze.
            var bw = Math.Max(2f, (float)r.Stock / maxStock * barMaxW);
            _bStyle.Color = i == 0 ? ColExchangeRec : i == deepIdx ? ColExchangeVol : ColExchangeBar;
            rt.FillRectangle(new RawRectF(barX, cy + 2f, barX + bw, cy + rowH - 2f), _bStyle);
            _bStyle.Color = rowCol;
            rt.DrawText($"{r.Ratio:0.##}:1", _tf!, new Rect(lx, cy, lx + ratioW, cy + rowH), _bStyle, DrawTextOptions.Clip);
            rt.DrawText(r.CumStock.ToString("N0"), _tf!, new Rect(rx - cumW, cy, rx, cy + rowH), _bStyle, DrawTextOptions.Clip);
            cy += rowH;
        }
    }
}
