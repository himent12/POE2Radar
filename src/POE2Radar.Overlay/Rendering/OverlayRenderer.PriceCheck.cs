using System.Globalization;
using System.Numerics;
using POE2Radar.Overlay.Draw;
using SkiaSharp;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// Price-check panel — "the appraisal" (hotkey on a hovered item): the item name in its rarity colour, a verdict
/// stamp, a turning rune circle with the worth counting up in its centre, the verdict read out in words, a
/// confidence gauge, a histogram of the asking prices, and the cheapest live listings. A dashed thread runs from
/// the panel to the glowing item. Sits on the opposite half of the screen from the item so it never covers the
/// game's tooltip. Buttons: <c>pc:trade</c> (open the exact search), <c>pc:refresh</c>, <c>pc:close</c>; the body
/// swallows clicks (<c>pc:panel</c>).
/// </summary>
public sealed partial class OverlayRenderer
{
    private const float PcW = 824f, PcPad = 32f, PcRing = 340f;
    private const int PcRows = 5;

    // Animation restarts: the panel "summons" when a new item opens, the worth counts up again when it changes.
    private string? _pcOpenKey, _pcValueKey;
    private float _pcOpenedAt, _pcValueAt;
    private DrawPath? _pcRays;

    /// <summary>Headless draw of just the price-check panel (preview/tests).</summary>
    internal void RenderPriceCheckPreview(RenderContext ctx, bool settle = true)
    {
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(new Color4(0.12f, 0.10f, 0.09f, 1f));
        _legendRowRects.Clear();
        _settleAnimations = settle;
        DrawPriceCheck(rt, ctx);
        _settleAnimations = false;
        rt.EndDraw();
    }

    private static string Fmt(double ex) => ex >= 100 ? $"{ex:0} ex" : ex >= 10 ? $"{ex:0.#} ex" : $"{ex:0.##} ex";

    private void DrawPriceCheck(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.PriceCheck is not { } pc) return;
        EnsureInsResources();
        BeginUiFrame(ctx);

        var openKey = $"{pc.Name}|{pc.AnchorX}|{pc.AnchorY}";
        if (openKey != _pcOpenKey) { _pcOpenKey = openKey; _pcOpenedAt = Now; _pcValueKey = null; }
        var worth = pc.Estimate ?? pc.MedianText;
        var valueKey = openKey + "|" + worth;
        if (valueKey != _pcValueKey) { _pcValueKey = valueKey; _pcValueAt = Now; }
        var tOpen = Since(_pcOpenedAt);
        var tVal = Since(_pcValueAt);

        var rows = Math.Min(pc.Rows.Count, PcRows);
        var modsH = pc.Mods.Count > 0 ? 8f + pc.Mods.Count * 17f : 0f;
        var headH = 108f + modsH;
        var listH = rows > 0 ? 18f + rows * 40f : pc.Loading ? 0f : 44f;
        var noteH = pc.Note.Length > 0 ? 26f : 0f;
        var h = PcPad + headH + 18f + PcRing + 18f + listH + noteH + 22f + 34f + PcPad * 0.5f;

        // ── Place: opposite half from the hovered item, centred vertically; scaled to fit that half ──
        var s = MathF.Max(0.5f, MathF.Min(FitScale(ctx.WindowWidth, ctx.WindowHeight, PcW, h, 12f), (ctx.WindowWidth * 0.5f - 24f) / PcW));
        var itemCx = pc.AnchorX + pc.AnchorW * 0.5f;
        var onLeft = itemCx > ctx.WindowWidth * 0.5f;
        var x = onLeft ? ctx.WindowWidth * 0.27f - PcW * s * 0.5f : ctx.WindowWidth * 0.73f - PcW * s * 0.5f;
        x = MathF.Round(Math.Clamp(x, 12f, MathF.Max(12f, ctx.WindowWidth - PcW * s - 12f)));
        var y = MathF.Round(MathF.Max(12f, (ctx.WindowHeight - h * s) * 0.5f));

        // Summon (CSS arc-summon): grows from 82 % about its centre while fading in out of a 6-px blur.
        var fade = Math.Clamp(tOpen / 0.54f, 0f, 1f);
        var grow = 0.82f + 0.18f * EaseOut(tOpen / 0.9f);
        var summoning = fade < 0.999f;
        if (summoning) rt.PushLayer(fade, 6f * (1f - fade));

        ItemThread(rt, pc, onLeft ? x + PcW * s : x, y + (PcPad + headH + 18f + PcRing * 0.5f) * s, onLeft);

        BeginScaled(rt, x + PcW * s * (1f - grow) * 0.5f, y + h * s * (1f - grow) * 0.5f, s * grow);
        var panel = new RawRectF(0f, 0f, PcW, h);
        Frame(rt, panel);
        _legendRowRects.Add((panel, "pc:panel"));
        var x1 = PcPad; var x2 = PcW - PcPad;

        // ── Header: label, name (glowing, rarity colour), base line, mods; the stamp and × on the right ──
        var rarity = Hex(pc.RarityRgb);
        CloseButton(rt, x2 - 20f, PcPad + 20f, "pc:close");
        var stampRight = x2 - 40f - 20f;
        Caps(rt, "The appraisal", x1, PcPad + 8f, UMuted, 0.3f);
        var nameMax = stampRight - x1 - StampWidth(rt, pc.Tier) - 24f;
        var name = Fit(rt, pc.Name, _tfPcName!, nameMax);
        rt.DrawTextGlow(name, _tfPcName!, x1, PcPad + 46f, A(rarity, 0.45f), 11f);
        T(rt, name, _tfPcName!, x1, PcPad + 46f, rarity);
        T(rt, Fit(rt, pc.BaseLine, _tfBody!, nameMax), _tfBody!, x1, PcPad + 82f, UMuted);
        var my = PcPad + 108f;
        foreach (var mod in pc.Mods)
        {
            T(rt, Fit(rt, mod, _tfSmall!, x2 - x1), _tfSmall!, x1, my + 8f, UMagic);
            my += 17f;
        }
        Stamp(rt, pc.Tier, stampRight, PcPad + 46f, tVal);

        // ── The circle ──
        var top = PcPad + headH + 18f;
        var rc = new NumVec2(x1 + PcRing * 0.5f, top + PcRing * 0.5f);
        RuneCircle(rt, pc, rc, worth, tVal);

        // ── Right column, laid out against the circle: reading (≤ 3 lines), confidence, spread, buttons ──
        var cx1 = x1 + PcRing + 28f; var cw = x2 - cx1;
        var reading = pc.Loading && pc.Rows.Count == 0 ? "Checking the market…" : pc.Verdict.Replace(" · ", " — ");
        var ly = top + 14f;
        var g = BeginRise(rt, tVal, 0.5f);
        foreach (var line in Wrap(rt, reading, _tfPcReading!, cw, 3))
        {
            T(rt, line, _tfPcReading!, cx1, ly, UText);
            ly += 26f;
        }
        EndRise(rt, g);
        g = BeginRise(rt, tVal, 0.6f);
        ConfidenceGauge(rt, pc, cx1, top + 102f, tVal);
        EndRise(rt, g);
        g = BeginRise(rt, tVal, 0.7f);
        Spread(rt, pc, cx1, top + 190f, cw, tVal);
        EndRise(rt, g);
        var by = top + PcRing - 24f;
        var reW = ButtonWidth(rt, "Re-appraise");
        var hasUrl = pc.Url is not null;
        Button(rt, "Open on trade site", cx1, by, hasUrl ? "pc:trade" : "pc:panel", primary: hasUrl, h: 48f, width: cw - reW - 10f);
        Button(rt, "Re-appraise", x2 - reW, by, "pc:refresh", primary: false, h: 48f, width: reW);

        // ── Listings ──
        var yy = top + PcRing + 18f;
        if (rows > 0)
        {
            g = BeginRise(rt, tVal, 0.8f);
            Caps(rt, "Cheapest live listings", x1, yy + 4f, UMuted, 0.2f);
            EndRise(rt, g);
            yy += 18f;
            for (var i = 0; i < rows; i++)
            {
                var r = pc.Rows[i];
                var rcy = yy + 20f;
                g = BeginRise(rt, tVal, 0.85f + i * 0.05f);
                HoverWash(rt, new RawRectF(x1, yy, x2, yy + 40f), $"pc:row:{i}");
                HLine(rt, x1, x2, yy, URule);
                var dot = new NumVec2(x1 + 10f, rcy);
                if (i == 0) { rt.FillRadialGlow(dot, 8f, A(UOn, 0.45f)); rt.FillEllipse(new Ellipse(dot, 3.5f, 3.5f), B(UOn)); }
                else rt.FillEllipse(new Ellipse(dot, 3.5f, 3.5f), B(UFaint));
                T(rt, r.Price.ToUpperInvariant(), _tfValue!, x1 + 30f, rcy, UWhite);
                if (r.Value.Length > 0) T(rt, r.Value, _tfSmall!, x1 + 34f + rt.MeasureText(r.Price.ToUpperInvariant(), _tfValue!), rcy, UDim);
                T(rt, Fit(rt, r.Seller, _tfBody!, 260f), _tfBody!, x1 + 250f, rcy, i == 0 ? UText : ULabel);
                TR(rt, r.Age, _tfSmall!, x2 - 8f, rcy, UDim);
                EndRise(rt, g);
                yy += 40f;
            }
            HLine(rt, x1, x2, yy, URule);
        }
        else if (!pc.Loading)
        {
            T(rt, pc.Error is null ? "No listings to compare." : Fit(rt, pc.Error, _tfSmall!, x2 - x1), _tfSmall!, x1, yy + 18f, pc.Error is null ? UDim : URed);
            yy += 44f;
        }
        if (pc.Note.Length > 0) { T(rt, Fit(rt, pc.Note, _tfSmall!, x2 - x1), _tfSmall!, x1, yy + 16f, UDim); yy += 26f; }

        // ── Footer: key hints, league ──
        var fy = h - PcPad * 0.5f - 17f;
        HLine(rt, x1, x2, fy - 20f, URule);
        var fx = x1;
        foreach (var (key, what) in new[] { ("Esc", "dismiss"), (ctx.InsMenu?.Settings.HoverPrice.PriceCheckHotkey ?? "Ctrl+D", "re-appraise"), ("Ins", "menu") })
        {
            fx = Keycap(rt, key, fx, fy) + 6f;
            T(rt, what, _tfSmall!, fx, fy, UDim);
            fx += rt.MeasureText(what, _tfSmall!) + 18f;
        }
        TR(rt, "POE2 Radar · " + pc.League, _tfSmall!, x2, fy, UDim);
        EndScaled(rt);
        if (summoning) rt.PopLayer();
    }

    // ── Verdict stamp (rotated −3°, slams in) ──
    private float StampWidth(DrawTarget rt, string tier) => TrackW(rt, tier.ToUpperInvariant(), _tfPcStamp!, 0.24f) + 36f;

    private void Stamp(DrawTarget rt, string tier, float right, float cy, float t)
    {
        var text = tier.ToUpperInvariant();
        var w = StampWidth(rt, tier);
        var f = EaseOut((t - 0.7f) / 0.6f);
        if (f <= 0f) return;
        var scale = 1.8f - 0.8f * f;
        var prev = rt.Transform;
        rt.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation((-8f + 5f * f) * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(right - w * 0.5f, cy) * prev;
        var r = new RawRectF(-w * 0.5f, -22f, w * 0.5f, 22f);
        var ink = UWhite;
        switch (tier)
        {
            case "Jackpot":
                rt.FillRadialGlow(NumVec2.Zero, w * 0.75f, A(UWhite, 0.3f * f));
                rt.FillRectangle(r, B(A(UWhite, f)));
                ink = Hex(0x000000);
                break;
            case "List it": rt.DrawRectangle(r, B(A(UWhite, f)), 2f); break;
            case "Vendor": rt.DrawRectangle(r, B(A(UDim, f)), 2f); ink = UMuted; break;
            default: DashedRect(rt, r, A(UDim, f), 5f); ink = UMuted; break;
        }
        TTC(rt, text, _tfPcStamp!, 0f, 0f, A(ink, f), 0.24f);
        rt.Transform = prev;
    }

    // ── The rune circle with the worth in the middle ──
    private void RuneCircle(DrawTarget rt, PriceCheckView pc, NumVec2 c, string? worth, float t)
    {
        var k = PcRing / 420f;
        var unknown = worth is null;
        var flick = unknown && !pc.Loading ? Flicker(Now % 2.6f / 2.6f) : 1f;
        Color4 W(float a) => A(UWhite, a * flick);

        if (pc.Tier == "Jackpot")
        {
            _pcRays ??= BuildRays();
            var prev = rt.Transform;
            rt.Transform = Matrix3x2.CreateRotation(Now / 24f * MathF.Tau) * Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(c) * prev;
            rt.FillGeometry(_pcRays, B(A(UWhite, 0.22f)));
            rt.Transform = prev;
        }
        rt.FillRadialGlow(c, 205f * k, W(0.18f));
        var burst = (t - 0.15f) / 1.4f;
        if (burst is > 0f and < 1f)
        {
            var e = EaseOut(burst);
            var br = 120f * k * (0.3f + 1.4f * e);
            rt.DrawEllipse(new Ellipse(c, br, br), B(W(0.9f * (1f - e))), 2f);
        }

        // Slow outer ring: two circles + the Futhark running round.
        var spin = Now / 60f * 360f;
        rt.DrawEllipse(new Ellipse(c, 204f * k, 204f * k), B(W(0.5f)), 1f);
        rt.DrawEllipse(new Ellipse(c, 172f * k, 172f * k), B(W(0.25f)), 1f);
        TextOnCircle(rt, "ᚠᚢᚦᚨᚱᚲᚷᚹ ᚺᚾᛁᛃᛇᛈᛉᛊ ᛏᛒᛖᛗᛚᛜᛞᛟ ᚠᚢᚦᚨᚱᚲᚷᚹ ᚺᚾᛁᛃᛇᛈᛉᛊ ᛏᛒᛖᛗᛚᛜᛞᛟ ᚠᚢᚦᚨᚱ", _tfPcRunes!, c, 188f * k, 180f + spin,
            6f, W(0.75f));
        // Counter-rotating ring: dotted circle + the listing stats.
        var counter = -Now / 38f * 360f;
        rt.DrawDashedCircle(c, 160f * k, W(0.35f), 1f, 1f, 9f, Now * 4f);
        TextOnCircle(rt, RingText(rt, pc, 2f * MathF.PI * 150f * k), _tfPcRing!, c, 150f * k, 180f + counter, 5f, A(UMuted, flick));
        Hexagram(rt, c, 132f * k, Now / 18f * 360f, W(0.55f));

        rt.FillEllipse(new Ellipse(c, 112f * k, 112f * k), B(Hex(0x000000)));
        rt.DrawEllipse(new Ellipse(c, 112f * k, 112f * k), B(W(0.6f)), 1f);
        rt.DrawArc(c, 112f * k, -90f, 360f * EaseOut(t / 1.8f), W(1f), 3f, roundCap: false);

        // Centre: WORTH / count-up / unit / source.
        var (num, unit) = worth is null ? ("?", "") : SplitAmount(worth);
        var shown = CountUp(num, EaseOut(t / 1.4f));
        var tf = shown.Length > 3 ? _tfPcPriceSm! : _tfPcPrice!;
        var sk = k / (380f / 420f);
        TTC(rt, "WORTH", _tfLabel!, c.X, c.Y - 58f * sk, UMuted, 0.35f);
        var pw = rt.MeasureText(shown, tf);
        rt.DrawTextGlow(shown, tf, c.X - pw * 0.5f, c.Y - 4f * sk, A(UWhite, 0.35f + 0.25f * Pulse(3f)), 12f);
        T(rt, shown, tf, c.X - pw * 0.5f, c.Y - 4f * sk, UWhite);
        var (unitWord, unitCol) = unit switch
        {
            "div" => ("DIVINE", UDivine), "ex" => ("EXALTED", Hex(0xB9B9B9)), "chaos" => ("CHAOS", Hex(0xB9B9B9)),
            "" => (pc.Loading ? "READING" : "UNREAD", UGrey), _ => (unit.ToUpperInvariant(), ULabel),
        };
        TTC(rt, unitWord, _tfPcUnit!, c.X, c.Y + 46f * sk, unitCol, 0.3f);
        var sub = pc.Estimate is not null ? "poe.ninja reference" : pc.MedianText is not null ? "median live ask" : pc.Loading ? "searching…" : "too few listings";
        TC(rt, sub, _tfSmall!, c.X, c.Y + 68f * sk, UMuted);
    }

    /// <summary>CSS <c>arc-flicker</c>: an unsteady opacity for the "can't read this" circle.</summary>
    private static float Flicker(float f) => f switch
    {
        < 0.40f => 1f, < 0.42f => 0.35f, < 0.70f => 1f, < 0.72f => 0.6f, _ => 1f,
    };

    /// <summary>"13.3" shown at <paramref name="f"/> of the way from 0, keeping the source's decimals (the
    /// number was formatted in the current culture, so it is parsed back the same way).</summary>
    private static string CountUp(string num, float f)
    {
        if (f >= 1f || !double.TryParse(num, NumberStyles.Number, CultureInfo.CurrentCulture, out var v)) return num;
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var dec = num.Contains(sep, StringComparison.Ordinal) ? num.Length - num.IndexOf(sep, StringComparison.Ordinal) - sep.Length : 0;
        return (v * f).ToString("F" + dec, CultureInfo.CurrentCulture);
    }

    /// <summary>The listing stats repeated to fill a ring of <paramref name="circumference"/> pixels.</summary>
    private string RingText(DrawTarget rt, PriceCheckView pc, float circumference)
    {
        var unit = pc.MinText is null
            ? "NOT ENOUGH LISTINGS TO READ · "
            : $"{pc.MinText} LOW · {pc.MedianText ?? "?"} MEDIAN · {Fmt(pc.Points.Count > 0 ? pc.Points[^1] : 0)} HIGH · {pc.Points.Count} LISTINGS · ";
        unit = unit.ToUpperInvariant();
        var w = TrackW(rt, unit, _tfPcRing!, 5f / _tfPcRing!.Font.Size) + 5f;
        var n = Math.Max(1, (int)(circumference / MathF.Max(w, 1f)));
        return string.Concat(Enumerable.Repeat(unit, n));
    }

    /// <summary>Lay <paramref name="text"/> clockwise along a circle starting at <paramref name="startDeg"/>, each
    /// glyph upright relative to the ring (SVG <c>textPath</c> on a circle), <paramref name="spacing"/> px apart.</summary>
    private void TextOnCircle(DrawTarget rt, string text, DrawTextFormat tf, NumVec2 c, float r, float startDeg, float spacing, Color4 col)
    {
        var prev = rt.Transform;
        var a = startDeg * MathF.PI / 180f;
        foreach (var ch in text)
        {
            var g = Ch(ch);
            var gw = rt.MeasureText(g, tf);
            var mid = a + gw * 0.5f / r;
            var p = c + new NumVec2(MathF.Cos(mid), MathF.Sin(mid)) * r;
            rt.Transform = Matrix3x2.CreateRotation(mid + MathF.PI / 2f) * Matrix3x2.CreateTranslation(p) * prev;
            if (ch != ' ') T(rt, g, tf, -gw * 0.5f, 0f, col);
            a += (gw + spacing) / r;
            if (a - startDeg * MathF.PI / 180f > MathF.Tau) break;
        }
        rt.Transform = prev;
    }

    private static DrawPath BuildRays()
    {
        var path = new SKPath();
        for (var i = 0; i < 24; i++)
        {
            var a = i / 24f * MathF.Tau; var b = a + 0.06f;
            SKPoint P(float ang, float r) => new(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
            path.MoveTo(P(a, 200f)); path.LineTo(P(a - 0.025f, 250f)); path.LineTo(P(b + 0.025f, 250f)); path.LineTo(P(b, 200f)); path.Close();
        }
        return new DrawPath(path);
    }

    // ── Confidence: a half-dial filled by how much the listings agree ──
    private void ConfidenceGauge(DrawTarget rt, PriceCheckView pc, float x, float y, float t)
    {
        var (word, frac) = pc.Loading && pc.Points.Count == 0 ? ("Reading", 0f) : Confidence(pc.Points);
        var c = new NumVec2(x + 60f, y + 62f);
        rt.DrawArc(c, 50f, 180f, 180f, ULine, 8f);
        rt.DrawArc(c, 50f, 180f, 180f * frac * EaseOut((t - 0.6f) / 1.8f), UWhite, 8f);
        T(rt, word, _tfPcConf!, x + 136f, y + 26f, UWhite);
        T(rt, pc.Status, _tfSmall!, x + 136f, y + 50f, pc.Error is null ? UMuted : URed);
    }

    /// <summary>How far to trust the price: few listings → Low; a wide spread → Scattered; many close together → High.</summary>
    internal static (string Word, float Fraction) Confidence(IReadOnlyList<double> points)
    {
        if (points.Count < 3) return (points.Count == 0 ? "None" : "Low", points.Count == 0 ? 0.04f : 0.18f);
        var spread = points[^1] / Math.Max(points[0], 1e-6);
        if (spread > 5) return ("Scattered", 0.4f);
        return points.Count >= 6 && spread <= 2.5 ? ("High", 0.85f) : ("Good", 0.65f);
    }

    // ── Spread of asking prices: 12 log-spaced bins, the median's bin lit ──
    private void Spread(DrawTarget rt, PriceCheckView pc, float x, float y, float w, float t)
    {
        Caps(rt, "Spread of asking prices", x, y, UMuted, 0.2f);
        const int bins = 12;
        const float barsH = 56f, gap = 4f;
        var counts = new int[bins];
        var peak = -1;
        if (pc.Points.Count > 0)
        {
            var lo = Math.Log(Math.Max(pc.Points[0], 1e-6)); var hi = Math.Log(Math.Max(pc.Points[^1], 1e-6));
            int Bin(double v) => hi - lo < 1e-9 ? bins / 2 : Math.Clamp((int)((Math.Log(Math.Max(v, 1e-6)) - lo) / (hi - lo) * bins), 0, bins - 1);
            foreach (var v in pc.Points) counts[Bin(v)]++;
            peak = Bin(pc.Points[pc.Points.Count / 2]);
        }
        var max = Math.Max(1, counts.Max());
        var bw = (w - gap * (bins - 1)) / bins;
        var bottom = y + 14f + barsH;
        for (var i = 0; i < bins; i++)
        {
            var grow = EaseOut((t - 0.7f - i * 0.05f) / 0.6f);
            var bh = MathF.Max(3f, barsH * counts[i] / max) * grow;
            var bx = x + i * (bw + gap);
            var r = new RawRectF(bx, bottom - bh, bx + bw, bottom);
            if (i == peak) rt.FillRadialGlow(new NumVec2(bx + bw * 0.5f, bottom - bh * 0.5f), bw * 1.4f, A(UWhite, 0.3f * grow));
            rt.FillRectangle(r, B(A(i == peak ? UWhite : UBar, grow)));
        }
        if (pc.Points.Count == 0) return;
        var ty = bottom + 12f;
        T(rt, pc.MinText ?? Fmt(pc.Points[0]), _tfSmall!, x, ty, UMuted);
        if (pc.MedianText is { } md) TC(rt, "▲ " + md, _tfSmall!, x + w * 0.5f, ty, UWhite);
        TR(rt, Fmt(pc.Points[^1]), _tfSmall!, x + w, ty, UMuted);
    }

    // ── The dashed thread from the panel to the item, and the item's glow ──
    private void ItemThread(DrawTarget rt, PriceCheckView pc, float px, float py, bool panelOnLeft)
    {
        if (pc.AnchorW <= 0f || pc.AnchorH <= 0f) return;
        var box = new RawRectF(pc.AnchorX, pc.AnchorY, pc.AnchorX + pc.AnchorW, pc.AnchorY + pc.AnchorH);
        var glow = Pulse(2.4f);
        rt.DrawRectangle(new RawRectF(box.Left - 3f, box.Top - 3f, box.Right + 3f, box.Bottom + 3f), B(A(UWhite, 0.12f + 0.12f * glow)), 5f);
        rt.DrawRectangle(new RawRectF(box.Left - 1f, box.Top - 1f, box.Right + 1f, box.Bottom + 1f), B(UWhite), 2f);

        var end = new NumVec2(panelOnLeft ? box.Left : box.Right, box.Top + MathF.Min(18f, box.Height * 0.5f));
        var start = new NumVec2(px, py);
        var dir = panelOnLeft ? 1f : -1f;
        var c1 = start + new NumVec2(80f * dir, 10f);
        var c2 = end + new NumVec2(-30f * dir, -60f);
        NumVec2 Bez(float u)
        {
            var v = 1f - u;
            return v * v * v * start + 3f * v * v * u * c1 + 3f * v * u * u * c2 + u * u * u * end;
        }
        var phase = Now / 1.2f * 16f;
        var prev = start;
        var travelled = 0f;
        for (var i = 1; i <= 48; i++)
        {
            var p = Bez(i / 48f);
            var seg = NumVec2.Distance(prev, p);
            if ((travelled + phase) % 16f < 6f) rt.DrawLine(prev, p, B(A(UWhite, 0.6f)), 1.5f);
            travelled += seg;
            prev = p;
        }
        rt.FillRadialGlow(end, 9f, A(UWhite, 0.4f + 0.3f * Pulse()));
        rt.FillEllipse(new Ellipse(end, 3.5f, 3.5f), B(UWhite));
    }
}
