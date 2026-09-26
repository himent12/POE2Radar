using System.Numerics;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// The overlay's UI kit — shared by the Insert menu, the trade panel and the price-check panel. The "grimoire" look:
/// near-black panels with hairline borders and white corner brackets, Cinzel headings with wide letter spacing,
/// small tracked caps labels, white as the only accent (a solid white fill marks the primary or selected thing),
/// diamond-knob switches, bordered keycaps and Elder Futhark ornaments. Motion is slow and ambient (spinning
/// sigils, drifting motes). Widgets that are clickable register their rect + action in <see cref="_legendRowRects"/>.
/// </summary>
public sealed partial class OverlayRenderer
{
    private static Color4 Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    // ── Palette: black, a ladder of hairline greys, parchment text, white as the accent ──
    private static readonly Color4 UPanel    = Hex(0x060606, 0.94f);  // window fill
    private static readonly Color4 UCard     = Hex(0x0A0A0A);         // card fill
    private static readonly Color4 UInset    = Hex(0x050505);         // fields, switch tracks
    private static readonly Color4 UKeyBg    = Hex(0x0D0D0D);
    private static readonly Color4 URule     = Hex(0x141414);         // row rules
    private static readonly Color4 URuleSoft = Hex(0x111111);
    private static readonly Color4 USide     = Hex(0x161616);         // sidebar divider, inner boxes
    private static readonly Color4 ULine     = Hex(0x1F1F1F);         // card / panel borders
    private static readonly Color4 UEdge     = Hex(0x2A2A2A);         // control borders
    private static readonly Color4 UBar      = Hex(0x2E2E2E);         // inactive bars
    private static readonly Color4 UFaint    = Hex(0x3A3A3A);
    private static readonly Color4 UWhite    = Hex(0xFFFFFF);
    private static readonly Color4 UText     = Hex(0xECE6D8);         // parchment
    private static readonly Color4 ULabel    = Hex(0xB5AFA3);
    private static readonly Color4 UMuted    = Hex(0x8A857A);
    private static readonly Color4 UGrey     = Hex(0x6F6A60);
    private static readonly Color4 UDim      = Hex(0x5A564E);
    private static readonly Color4 UOn       = Hex(0x7FCF8F);         // "live / good"
    private static readonly Color4 URed      = Hex(0xE06C6C);
    private static readonly Color4 UDivine   = Hex(0xECE0B8);
    private static readonly Color4 UBlue     = Hex(0x7F97D8);
    private static readonly Color4 UTeal     = Hex(0x6FB4BB);
    private static readonly Color4 ULife     = Hex(0xC9523F);
    private static readonly Color4 UMagic    = Hex(0x8888FF);         // PoE mod-line blue

    // ── Type ──
    private DrawTextFormat? _tfH1, _tfWordmark, _tfNav, _tfCardTitle, _tfStat, _tfStatUnit, _tfBtn, _tfBtnSm, _tfSeg, _tfValue, _tfDisplay;
    private DrawTextFormat? _tfLabel, _tfBody, _tfMedium, _tfBold, _tfName, _tfSmall, _tfKey, _tfKeyBig, _tfChip, _tfRune, _tfRuneSm;
    private DrawTextFormat? _tfPcName, _tfPcPrice, _tfPcPriceSm, _tfPcStamp, _tfPcReading, _tfPcConf, _tfPcUnit, _tfPcRing, _tfPcRunes;
    private DrawBrush? _bUi;

    private void EnsureInsResources()
    {
        if (_tfBody is not null) return;
        _tfH1        = _window.CreateDisplayTextFormat(30f, UiFonts.Weight.Bold);
        _tfWordmark  = _window.CreateDisplayTextFormat(22f, UiFonts.Weight.Black);
        _tfNav       = _window.CreateDisplayTextFormat(15f, UiFonts.Weight.SemiBold);
        _tfCardTitle = _window.CreateDisplayTextFormat(16f, UiFonts.Weight.Bold);
        _tfStat      = _window.CreateDisplayTextFormat(28f, UiFonts.Weight.Bold);
        _tfStatUnit  = _window.CreateDisplayTextFormat(14f, UiFonts.Weight.Bold);
        _tfBtn       = _window.CreateDisplayTextFormat(13f, UiFonts.Weight.Bold);
        _tfBtnSm     = _window.CreateDisplayTextFormat(11f, UiFonts.Weight.Bold);
        _tfSeg       = _window.CreateDisplayTextFormat(12.5f, UiFonts.Weight.Bold);
        _tfValue     = _window.CreateDisplayTextFormat(15f, UiFonts.Weight.Bold);
        _tfDisplay   = _window.CreateDisplayTextFormat(14f, UiFonts.Weight.Bold);
        _tfLabel     = _window.CreateSansTextFormat(11f, UiFonts.Weight.SemiBold);
        _tfBody      = _window.CreateSansTextFormat(14f);
        _tfMedium    = _window.CreateSansTextFormat(13.5f, UiFonts.Weight.Medium);
        _tfBold      = _window.CreateSansTextFormat(13.5f, UiFonts.Weight.SemiBold);
        _tfName      = _window.CreateSansTextFormat(15f, UiFonts.Weight.SemiBold);
        _tfSmall     = _window.CreateSansTextFormat(12.5f);
        _tfKey       = _window.CreateSansTextFormat(11f, UiFonts.Weight.SemiBold);
        _tfKeyBig    = _window.CreateSansTextFormat(13f, UiFonts.Weight.SemiBold);
        _tfChip      = _window.CreateSansTextFormat(10f, UiFonts.Weight.Bold);
        _tfRune      = _window.CreateRunicTextFormat(14f);
        _tfRuneSm    = _window.CreateRunicTextFormat(12f);
        _tfPcName    = _window.CreateDisplayTextFormat(38f, UiFonts.Weight.Bold);
        _tfPcPrice   = _window.CreateDisplayTextFormat(84f, UiFonts.Weight.Black);
        _tfPcPriceSm = _window.CreateDisplayTextFormat(60f, UiFonts.Weight.Black);
        _tfPcStamp   = _window.CreateDisplayTextFormat(21f, UiFonts.Weight.Black);
        _tfPcReading = _window.CreateDisplayTextFormat(18f, UiFonts.Weight.Regular);
        _tfPcConf    = _window.CreateDisplayTextFormat(22f, UiFonts.Weight.Bold);
        _tfPcUnit    = _window.CreateDisplayTextFormat(15f, UiFonts.Weight.SemiBold);
        _tfPcRing    = _window.CreateDisplayTextFormat(10.5f, UiFonts.Weight.SemiBold);
        _tfPcRunes   = _window.CreateRunicTextFormat(14f);
        _bUi = _window.RenderTarget.CreateSolidColorBrush(UText);
    }

    private void DisposeUi()
    {
        foreach (var f in new[] { _tfH1, _tfWordmark, _tfNav, _tfCardTitle, _tfStat, _tfStatUnit, _tfBtn, _tfBtnSm, _tfSeg, _tfValue, _tfDisplay,
                     _tfLabel, _tfBody, _tfMedium, _tfBold, _tfName, _tfSmall, _tfKey, _tfKeyBig, _tfChip, _tfRune, _tfRuneSm,
                     _tfPcName, _tfPcPrice, _tfPcPriceSm, _tfPcStamp, _tfPcReading, _tfPcConf, _tfPcUnit, _tfPcRing, _tfPcRunes })
            f?.Dispose();
        _pcRays?.Dispose();
    }

    // ── Motion. One clock drives everything; "reduce motion" (Settings.ReduceMotion) stops it at 0 and finishes
    //    every entrance/transition instantly, so the menus draw as still frames. ──
    private static readonly System.Diagnostics.Stopwatch UiClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Set per frame from <see cref="RenderContext.ReduceMotion"/>.</summary>
    private bool _still;

    /// <summary>Headless previews draw entrance animations in their finished state.</summary>
    private bool _settleAnimations;

    /// <summary>Animation clock (seconds, monotonic); frozen at 0 when motion is reduced.</summary>
    private float Now => _still ? 0f : (float)UiClock.Elapsed.TotalSeconds;

    /// <summary>Slow 0..1..0 breathing (the CSS <c>arc-pulse</c>), period <paramref name="period"/> seconds.</summary>
    private float Pulse(float period = 3.2f) => _still ? 1f : 0.5f - 0.5f * MathF.Cos(Now / period * MathF.Tau);

    private static float EaseOut(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    /// <summary>Seconds since <paramref name="t0"/> (a <see cref="Now"/> stamp) — entrance-animation time.</summary>
    private float Since(float t0) => _settleAnimations || _still ? 60f : Now - t0;

    private static Color4 Mix(Color4 a, Color4 b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    // Two-state transitions (switch knobs, hover fades), keyed by the control's action string.
    private readonly Dictionary<string, (bool State, float At, float From)> _anims = new();

    /// <summary>0..1 progress of <paramref name="key"/> towards <paramref name="state"/> (1 = true), easing over
    /// <paramref name="duration"/> seconds from wherever it was when the state last flipped. First sight = settled.</summary>
    private float Anim(string key, bool state, float duration)
    {
        var target = state ? 1f : 0f;
        if (_still || _settleAnimations) { _anims[key] = (state, -1e6f, target); return target; }
        var now = Now;
        if (!_anims.TryGetValue(key, out var a))
        {
            if (_anims.Count >= 512) _anims.Clear();   // keys are control actions; stale ones (old trade lines) are shed
            _anims[key] = (state, -1e6f, target);
            return target;
        }
        float Value((bool State, float At, float From) s) => s.From + ((s.State ? 1f : 0f) - s.From) * EaseOut((now - s.At) / duration);
        if (a.State != state) { a = (state, now, Value(a)); _anims[key] = a; }
        return Value(a);
    }

    /// <summary>Per-frame UI state from the context: motion setting and cursor.</summary>
    private void BeginUiFrame(RenderContext ctx)
    {
        _still = ctx.ReduceMotion;
        _mouse = new NumVec2(ctx.MouseX, ctx.MouseY);
    }

    // ── Hover: the cursor (client px) mapped into the current panel's design units ──
    private NumVec2 _mouse = new(-1f, -1f);
    private bool _uiScaled;

    private bool Hovered(RawRectF r)
    {
        if (_mouse.X < 0f) return false;
        var p = _mouse;
        if (_uiScaled && Matrix3x2.Invert(_uiScale, out var inv)) p = NumVec2.Transform(p, inv);
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    /// <summary>The design's row hover: a faint white wash fading in over 0.2 s. Returns the hover amount.</summary>
    private float HoverWash(DrawTarget rt, RawRectF r, string key, float alpha = 0.035f)
    {
        var f = Anim("hv:" + key, Hovered(r), 0.2f);
        if (f > 0.01f) rt.FillRectangle(r, B(A(UWhite, alpha * f)));
        return f;
    }

    // ── Rise: the CSS arc-rise entrance — a group fades in while sliding up 14 px, 0.6 s after its delay ──
    private readonly record struct RiseScope(Matrix3x2 Prev, bool Layered);

    private RiseScope BeginRise(DrawTarget rt, float t, float delay)
    {
        var prev = rt.Transform;
        var e = EaseOut((t - delay) / 0.6f);
        if (e >= 0.999f) return new(prev, false);
        rt.PushLayer(e);
        rt.Transform = Matrix3x2.CreateTranslation(0f, 14f * (1f - e)) * prev;
        return new(prev, true);
    }

    private void EndRise(DrawTarget rt, RiseScope s)
    {
        rt.Transform = s.Prev;
        if (s.Layered) rt.PopLayer();
    }

    // ── Primitives ──
    private DrawBrush B(Color4 c) { _bUi!.Color = c; return _bUi; }
    private void T(DrawTarget rt, string s, DrawTextFormat tf, float x, float cy, Color4 c) => rt.DrawTextVCentered(s, tf, x, cy, B(c));
    private void TR(DrawTarget rt, string s, DrawTextFormat tf, float right, float cy, Color4 c) => T(rt, s, tf, right - rt.MeasureText(s, tf), cy, c);
    private void TC(DrawTarget rt, string s, DrawTextFormat tf, float cx, float cy, Color4 c) => T(rt, s, tf, cx - rt.MeasureText(s, tf) * 0.5f, cy, c);
    private void HLine(DrawTarget rt, float x1, float x2, float y, Color4 c) => rt.DrawLine(new NumVec2(x1, y), new NumVec2(x2, y), B(c), 1f);
    private void VLine(DrawTarget rt, float x, float y1, float y2, Color4 c) => rt.DrawLine(new NumVec2(x, y1), new NumVec2(x, y2), B(c), 1f);
    private static Color4 A(Color4 c, float a) => new(c.R, c.G, c.B, a);

    private static readonly string[] AsciiChars = Enumerable.Range(0, 128).Select(i => ((char)i).ToString()).ToArray();
    private static string Ch(char c) => c < 128 ? AsciiChars[c] : c.ToString();

    /// <summary>Width of <paramref name="s"/> drawn with CSS-style letter spacing of <paramref name="em"/> × font size
    /// between glyphs (the trailing gap is not counted).</summary>
    private static float TrackW(DrawTarget rt, string s, DrawTextFormat tf, float em)
    {
        if (s.Length == 0) return 0f;
        var w = 0f;
        foreach (var c in s) w += rt.MeasureText(Ch(c), tf);
        return w + em * tf.Font.Size * (s.Length - 1);
    }

    /// <summary>Letter-spaced text (caps labels, Cinzel headings). Returns the right edge.</summary>
    private float TT(DrawTarget rt, string s, DrawTextFormat tf, float x, float cy, Color4 c, float em)
    {
        var gap = em * tf.Font.Size;
        foreach (var ch in s)
        {
            var g = Ch(ch);
            T(rt, g, tf, x, cy, c);
            x += rt.MeasureText(g, tf) + gap;
        }
        return x - gap;
    }

    private void TTC(DrawTarget rt, string s, DrawTextFormat tf, float cx, float cy, Color4 c, float em)
        => TT(rt, s, tf, cx - TrackW(rt, s, tf, em) * 0.5f, cy, c, em);

    private void TTR(DrawTarget rt, string s, DrawTextFormat tf, float right, float cy, Color4 c, float em)
        => TT(rt, s, tf, right - TrackW(rt, s, tf, em), cy, c, em);

    /// <summary>Small tracked caps label (the design's 11-px "SECTION" labels).</summary>
    private float Caps(DrawTarget rt, string s, float x, float cy, Color4? c = null, float em = 0.16f)
        => TT(rt, s.ToUpperInvariant(), _tfLabel!, x, cy, c ?? UMuted, em);

    /// <summary>Truncate <paramref name="s"/> with an ellipsis so it fits <paramref name="maxW"/> pixels.</summary>
    private static string Fit(DrawTarget rt, string s, DrawTextFormat tf, float maxW)
    {
        if (rt.MeasureText(s, tf) <= maxW) return s;
        var lo = 0; var hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (rt.MeasureText(s[..mid] + "…", tf) <= maxW) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? "…" : s[..lo] + "…";
    }

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/> lines (the last one ellipsized).</summary>
    private static List<string> Wrap(DrawTarget rt, string s, DrawTextFormat tf, float maxW, int maxLines)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = "";
        for (var i = 0; i < words.Length; i++)
        {
            var next = line.Length == 0 ? words[i] : line + " " + words[i];
            if (line.Length == 0 || rt.MeasureText(next, tf) <= maxW) { line = next; continue; }
            if (lines.Count == maxLines - 1)
            {
                lines.Add(Fit(rt, line + " " + string.Join(' ', words[i..]), tf, maxW));
                return lines;
            }
            lines.Add(line);
            line = words[i];
        }
        if (line.Length > 0) lines.Add(Fit(rt, line, tf, maxW));
        return lines;
    }

    private void Icon(DrawTarget rt, string name, float x, float y, float size, Color4 c, float stroke = 1.8f)
    {
        if (UiIcons.Get(name) is { } p) rt.DrawIconPath(p, x, y, size, c, stroke);
    }

    private void Diamond(DrawTarget rt, float cx, float cy, float r, Color4 fill)
    {
        var prev = rt.Transform;
        rt.Transform = Matrix3x2.CreateRotation(MathF.PI / 4f) * Matrix3x2.CreateTranslation(cx, cy) * prev;
        rt.FillRectangle(new RawRectF(-r, -r, r, r), B(fill));
        rt.Transform = prev;
    }

    /// <summary>The list bullet: an outlined diamond with a dot (the design's rarity-coloured row marker).</summary>
    private void DiamondMark(DrawTarget rt, float cx, float cy, Color4 c)
    {
        var p = new[] { new NumVec2(cx, cy - 6f), new NumVec2(cx + 6f, cy), new NumVec2(cx, cy + 6f), new NumVec2(cx - 6f, cy) };
        for (var i = 0; i < 4; i++) rt.DrawLine(p[i], p[(i + 1) % 4], B(c), 1f);
        rt.FillEllipse(new Ellipse(new NumVec2(cx, cy), 1.6f, 1.6f), B(c));
    }

    /// <summary>Stroke a closed polygon.</summary>
    private void Poly(DrawTarget rt, ReadOnlySpan<NumVec2> p, Color4 c, float w = 1f)
    {
        for (var i = 0; i < p.Length; i++) rt.DrawLine(p[i], p[(i + 1) % p.Length], B(c), w);
    }

    /// <summary>Two interlocked triangles (the hexagram of the sigils), rotated by <paramref name="deg"/>.</summary>
    private void Hexagram(DrawTarget rt, NumVec2 c, float r, float deg, Color4 col, float w = 1f)
    {
        Span<NumVec2> tri = stackalloc NumVec2[3];
        for (var k = 0; k < 2; k++)
        {
            for (var i = 0; i < 3; i++)
            {
                var a = (deg - 90f + k * 180f + i * 120f) * MathF.PI / 180f;
                tri[i] = new NumVec2(c.X + MathF.Cos(a) * r, c.Y + MathF.Sin(a) * r);
            }
            Poly(rt, tri, col, w);
        }
    }

    /// <summary>The spinning logo sigil: dashed ring, counter-rotating hexagram, breathing core.</summary>
    private void Sigil(DrawTarget rt, float cx, float cy, float size)
    {
        var s = size / 56f;
        var c = new NumVec2(cx, cy);
        rt.DrawDashedCircle(c, 26f * s, A(UWhite, 0.9f), 0.8f * s, 3f * s, 5f * s, Now / 18f * 163f * s);
        Hexagram(rt, c, 22f * s, -Now / 38f * 360f, UWhite, 1f);
        var p = Pulse();
        rt.FillRadialGlow(c, 9f * s, A(UWhite, 0.25f + 0.25f * p));
        rt.FillEllipse(new Ellipse(c, 4.5f * s * (1f + 0.08f * p), 4.5f * s * (1f + 0.08f * p)), B(A(UWhite, 0.55f + 0.45f * p)));
    }

    /// <summary>
    /// A grimoire window: deep shadow, near-black fill, a hairline border inside a black edge, and white corner
    /// brackets (an outer L, an inner L and a dot) at each corner.
    /// </summary>
    private void Frame(DrawTarget rt, RawRectF r, bool shadow = true)
    {
        if (shadow) rt.DrawShadow(r, 0f, 40f, 0.9f);
        rt.FillRectangle(r, B(UPanel));
        rt.DrawRectangle(new RawRectF(r.Left - 1f, r.Top - 1f, r.Right + 1f, r.Bottom + 1f), B(Hex(0x000000)), 1f);
        rt.DrawRectangle(r, B(ULine), 1f);
        var ink = B(UWhite);
        foreach (var (x, y, sx, sy) in new[] { (r.Left - 1f, r.Top - 1f, 1f, 1f), (r.Right + 1f, r.Top - 1f, -1f, 1f),
                     (r.Left - 1f, r.Bottom + 1f, 1f, -1f), (r.Right + 1f, r.Bottom + 1f, -1f, -1f) })
        {
            NumVec2 P(float px, float py) => new(x + px * sx, y + py * sy);
            rt.DrawLine(P(1f, 30f), P(1f, 1f), ink, 1.2f);
            rt.DrawLine(P(1f, 1f), P(30f, 1f), ink, 1.2f);
            rt.DrawLine(P(7f, 18f), P(7f, 7f), ink, 1.2f);
            rt.DrawLine(P(7f, 7f), P(18f, 7f), ink, 1.2f);
            rt.FillEllipse(new Ellipse(P(7f, 7f), 1.8f, 1.8f), ink);
        }
    }

    /// <summary>A content card: #0A0A0A with a hairline border.</summary>
    private void CardBox(DrawTarget rt, RawRectF r)
    {
        rt.FillRectangle(r, B(UCard));
        rt.DrawRectangle(r, B(ULine), 1f);
    }

    /// <summary>A card with a tracked Cinzel title. Returns the y where its rows start.</summary>
    private float Card(DrawTarget rt, RawRectF r, string title)
    {
        CardBox(rt, r);
        TT(rt, title.ToUpperInvariant(), _tfCardTitle!, r.Left + CardPad, r.Top + CardPad + 8f, UWhite, 0.16f);
        return r.Top + CardPad + CardHead;
    }

    private const float CardPad = 22f, CardHead = 32f;

    /// <summary>
    /// The dimmed game behind a modal: a black veil, a huge faint sigil turning once a minute, and motes of light
    /// drifting up from the bottom edge.
    /// </summary>
    private void Backdrop(DrawTarget rt, float w, float h, float veil = 0.62f)
    {
        rt.FillRectangle(new RawRectF(0f, 0f, w, h), B(Hex(0x000000, veil)));
        var c = new NumVec2(w * 0.67f, h * 0.5f);
        var k = h / 900f * 1.07f;
        var ink = A(UWhite, 0.06f);
        var rot = Now / 60f * 360f;
        rt.DrawEllipse(new Ellipse(c, 680f * k, 680f * k), B(ink), 1f);
        rt.DrawDashedCircle(c, 640f * k, ink, 1f, 2f * k, 14f * k, rot * 6f);
        rt.DrawEllipse(new Ellipse(c, 380f * k, 380f * k), B(ink), 1f);
        Hexagram(rt, c, 640f * k, rot, ink);
        Motes(rt, w, h, 30, 11);
    }

    private void Motes(DrawTarget rt, float w, float h, int count, int seed)
    {
        float Rnd() { seed = (seed * 9301 + 49297) % 233280; return seed / 233280f; }
        var k = h / 900f;
        for (var i = 0; i < count; i++)
        {
            var x0 = Rnd() * w;
            var y0 = (820f + Rnd() * 120f) * k;
            var size = 1f + Rnd() * 2.5f;
            var period = 9f + Rnd() * 10f;
            var delay = Rnd() * 18f;
            var f = (Now + delay) % period / period;
            var alpha = f < 0.12f ? f / 0.12f * 0.9f : 0.9f * (1f - (f - 0.12f) / 0.88f);
            var p = new NumVec2(x0 + 40f * k * f, y0 - 760f * k * f);
            rt.FillRadialGlow(p, size * 2.5f, A(UWhite, alpha * 0.5f));
            rt.FillEllipse(new Ellipse(p, size * 0.5f, size * 0.5f), B(A(UWhite, alpha)));
        }
    }

    // ── Uniform scaling of a whole panel (layouts are authored at design size; the window may be smaller or 4K) ──
    private Matrix3x2 _uiPrevTransform, _uiScale;
    private int _uiRectStart;

    /// <summary>Scale that fits a <paramref name="w"/>×<paramref name="h"/> design into the window with a margin,
    /// never below 0.6; grows past 1 only on tall (1440p+) windows.</summary>
    private static float FitScale(float winW, float winH, float w, float h, float margin = 24f)
        => MathF.Max(0.6f, MathF.Min(MathF.Min((winW - margin * 2f) / w, (winH - margin * 2f) / h), MathF.Max(1f, winH / 1200f)));

    /// <summary>Draw subsequent panel content in design units: (0,0) maps to (<paramref name="x"/>,<paramref name="y"/>)
    /// on screen, scaled by <paramref name="s"/>. Pair with <see cref="EndScaled"/>, which maps the click rects
    /// registered in between back to screen pixels.</summary>
    private void BeginScaled(DrawTarget rt, float x, float y, float s)
    {
        _uiPrevTransform = rt.Transform;
        _uiScale = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(x, y);
        rt.Transform = _uiScale * _uiPrevTransform;
        _uiRectStart = _legendRowRects.Count;
        _uiScaled = true;
    }

    private void EndScaled(DrawTarget rt)
    {
        rt.Transform = _uiPrevTransform;
        _uiScaled = false;
        for (var i = _uiRectStart; i < _legendRowRects.Count; i++)
        {
            var (r, action) = _legendRowRects[i];
            var a = NumVec2.Transform(new NumVec2(r.Left, r.Top), _uiScale);
            var b = NumVec2.Transform(new NumVec2(r.Right, r.Bottom), _uiScale);
            _legendRowRects[i] = (new RawRectF(a.X, a.Y, b.X, b.Y), action);
        }
    }

    // ── Widgets ──

    /// <summary>Key binding in a bordered box. <paramref name="big"/> = the settings keycap (thick bottom edge).
    /// Returns its right edge.</summary>
    private float Keycap(DrawTarget rt, string label, float x, float cy, bool big = false, bool listening = false)
    {
        var tf = big ? _tfKeyBig! : _tfKey!;
        var w = KeycapWidth(rt, label, big);
        var h = big ? 32f : 20f;
        var r = new RawRectF(x, cy - h * 0.5f, x + w, cy + h * 0.5f);
        rt.FillRectangle(r, B(big ? UKeyBg : UInset));
        rt.DrawRectangle(r, B(listening ? UWhite : UEdge), 1f);
        if (big) rt.FillRectangle(new RawRectF(r.Left, r.Bottom - 3f, r.Right, r.Bottom), B(listening ? UWhite : UEdge));
        TC(rt, label, tf, (r.Left + r.Right) * 0.5f, cy - (big ? 1f : 0f), UWhite);
        return r.Right;
    }

    private float KeycapWidth(DrawTarget rt, string label, bool big = false)
        => big ? MathF.Max(64f, rt.MeasureText(label, _tfKeyBig!) + 24f) : MathF.Max(22f, rt.MeasureText(label, _tfKey!) + 12f);

    private const float SwitchW = 52f, SwitchH = 26f;

    /// <summary>The design's switch: a bordered track with a diamond knob that slides right (0.25 s) and lights up
    /// when on. <paramref name="right"/> is its right edge; <paramref name="key"/> (the control's action) animates it.</summary>
    private void Switch(DrawTarget rt, float right, float cy, bool on, string? key = null)
    {
        var f = key is null ? (on ? 1f : 0f) : Anim("sw:" + key, on, 0.25f);
        var r = new RawRectF(right - SwitchW, cy - SwitchH * 0.5f, right, cy + SwitchH * 0.5f);
        rt.FillRectangle(r, B(Mix(UInset, Hex(0x1A1A1A), f)));
        rt.DrawRectangle(r, B(Mix(UEdge, UWhite, f)), 1f);
        var kx = r.Left + 13f + (SwitchW - 26f) * f;
        if (f > 0.01f) rt.FillRadialGlow(new NumVec2(kx, cy), 11f, A(UWhite, 0.45f * f));
        Diamond(rt, kx, cy, 4.3f, Mix(UFaint, UWhite, f));
    }

    /// <summary>Verdict/status chip. Styles: solid (white, glowing), outline, dim, dashed. Returns its right edge.</summary>
    private float Chip(DrawTarget rt, string text, float x, float cy, ChipStyle style)
    {
        var t = text.ToUpperInvariant();
        var w = ChipWidth(rt, t);
        var r = new RawRectF(x, cy - 10f, x + w, cy + 10f);
        switch (style)
        {
            case ChipStyle.Solid:
                rt.FillRadialGlow(new NumVec2((r.Left + r.Right) * 0.5f, cy), w * 0.7f, A(UWhite, 0.18f));
                rt.FillRectangle(r, B(UWhite));
                break;
            case ChipStyle.Outline: rt.DrawRectangle(r, B(UDim), 1f); break;
            case ChipStyle.Dim: rt.DrawRectangle(r, B(UEdge), 1f); break;
            case ChipStyle.Live: rt.DrawRectangle(r, B(A(UOn, 0.55f)), 1f); break;
            default: DashedRect(rt, r, UFaint); break;
        }
        var col = style switch { ChipStyle.Solid => Hex(0x000000), ChipStyle.Outline => UText, ChipStyle.Live => UOn, _ => UGrey };
        TT(rt, t, _tfChip!, r.Left + 8f, cy, col, 0.16f);
        return r.Right;
    }

    private float ChipWidth(DrawTarget rt, string upper) => TrackW(rt, upper, _tfChip!, 0.16f) + 16f;

    private enum ChipStyle { Solid, Outline, Dim, Dashed, Live }

    private void DashedRect(DrawTarget rt, RawRectF r, Color4 c, float dash = 3f)
    {
        void Run(NumVec2 a, NumVec2 b)
        {
            var len = NumVec2.Distance(a, b);
            var dir = (b - a) / MathF.Max(len, 1e-3f);
            for (var d = 0f; d < len; d += dash * 2f)
                rt.DrawLine(a + dir * d, a + dir * MathF.Min(len, d + dash), B(c), 1f);
        }
        Run(new(r.Left, r.Top), new(r.Right, r.Top));
        Run(new(r.Right, r.Top), new(r.Right, r.Bottom));
        Run(new(r.Right, r.Bottom), new(r.Left, r.Bottom));
        Run(new(r.Left, r.Bottom), new(r.Left, r.Top));
    }

    /// <summary>Rectangular button with a tracked Cinzel label: primary = solid white with black text, secondary =
    /// hairline border. Registers <paramref name="action"/>.</summary>
    private RawRectF Button(DrawTarget rt, string label, float x, float cy, string action, bool primary = true, string? icon = null, float h = 40f, float? width = null, bool small = false)
    {
        var text = label.ToUpperInvariant();
        var tf = small ? _tfBtnSm! : _tfBtn!;
        var em = small ? 0.1f : 0.16f;
        var w = width ?? ButtonWidth(rt, label, icon, small);
        var r = new RawRectF(x, cy - h * 0.5f, x + w, cy + h * 0.5f);
        if (primary)
        {
            rt.FillRectangle(r, B(UWhite));
            // The sheen that sweeps across the primary button every few seconds.
            var f = Now % 3.6f / 3.6f;
            if (f < 0.55f)
            {
                var bw = w * 0.3f;
                var sx = r.Left - bw * 1.6f + (w + bw * 3f) * (f / 0.55f);
                rt.PushClip(r);
                rt.FillGradient(new RawRectF(sx, r.Top, sx + bw, r.Bottom), 0f, true, A(Hex(0xD8D2C4), 0f), A(Hex(0xD8D2C4), 0.55f), A(Hex(0xD8D2C4), 0f));
                rt.PopClip();
            }
        }
        else rt.DrawRectangle(r, B(UEdge), 1f);
        var ink = primary ? Hex(0x000000) : UText;
        var tw = TrackW(rt, text, tf, em) + (icon is null ? 0f : 20f);
        var tx = (r.Left + r.Right - tw) * 0.5f;
        if (icon is not null) { Icon(rt, icon, tx, cy - 7f, 14f, ink, 2f); tx += 20f; }
        TT(rt, text, tf, tx, cy, ink, em);
        _legendRowRects.Add((r, action));
        return r;
    }

    private float ButtonWidth(DrawTarget rt, string label, string? icon = null, bool small = false)
        => small ? TrackW(rt, label.ToUpperInvariant(), _tfBtnSm!, 0.1f) + 20f
            : TrackW(rt, label.ToUpperInvariant(), _tfBtn!, 0.16f) + 36f + (icon is null ? 0f : 20f);

    /// <summary>The bordered square × button registering <paramref name="action"/>.</summary>
    private void CloseButton(DrawTarget rt, float cx, float cy, string action, float size = 40f)
    {
        var h = size * 0.5f;
        var r = new RawRectF(cx - h, cy - h, cx + h, cy + h);
        rt.DrawRectangle(r, B(UEdge), 1f);
        rt.DrawLine(new NumVec2(cx - 5f, cy - 5f), new NumVec2(cx + 5f, cy + 5f), B(UText), 1.3f);
        rt.DrawLine(new NumVec2(cx + 5f, cy - 5f), new NumVec2(cx - 5f, cy + 5f), B(UText), 1.3f);
        _legendRowRects.Add((r, action));
    }

    // ── Rows: label (+ optional help) left, control right, hairline below ──
    private const float Row = 46f;

    private float RowBase(DrawTarget rt, float x, float y, float w, string label, string? help, float h = Row)
    {
        var cy = y + h * 0.5f;
        if (string.IsNullOrEmpty(help)) T(rt, label, _tfBody!, x, cy, ULabel);
        else
        {
            T(rt, label, _tfBody!, x, cy - 8f, ULabel);
            T(rt, help, _tfSmall!, x, cy + 10f, UDim);
        }
        HLine(rt, x, x + w, y + h - 0.5f, URule);
        return cy;
    }

    private float Toggle(DrawTarget rt, float x, float y, float w, string label, string? help, bool on, string action, string? hotkey = null)
    {
        HoverWash(rt, new RawRectF(x - 8f, y, x + w + 8f, y + Row), action);
        var cy = RowBase(rt, x, y, w, label, help);
        if (hotkey is { Length: > 0 }) Keycap(rt, hotkey, x + w - SwitchW - 12f - KeycapWidth(rt, hotkey), cy);
        Switch(rt, x + w, cy, on, action);
        _legendRowRects.Add((new RawRectF(x, y, x + w, y + Row), action));
        return y + Row;
    }

    /// <summary>Slider row: label left, value right in Cinzel, a hairline track underneath with a glowing diamond
    /// handle (click anywhere on the track). Small − / + nudges sit at the track ends.</summary>
    private float Slider(DrawTarget rt, float x, float y, float w, string label, string? help, float value, string key)
    {
        const float h = 58f;
        var spec = InsSliderSpec.All[key];
        var top = y + 16f;
        T(rt, label, _tfBody!, x, top, ULabel);
        if (help is not null) T(rt, help, _tfSmall!, x + rt.MeasureText(label, _tfBody!) + 8f, top + 0.5f, UDim);
        TR(rt, spec.Format(value), _tfValue!, x + w, top, UWhite);

        var ty = y + 38f;
        var minus = new RawRectF(x - 2f, ty - 10f, x + 12f, ty + 10f);
        var plus = new RawRectF(x + w - 12f, ty - 10f, x + w + 2f, ty + 10f);
        TC(rt, "−", _tfBody!, (minus.Left + minus.Right) * 0.5f, ty, UDim);
        TC(rt, "+", _tfBody!, (plus.Left + plus.Right) * 0.5f, ty, UDim);
        var track = new RawRectF(minus.Right + 8f, ty - 1f, plus.Left - 8f, ty + 1f);
        rt.FillRectangle(track, B(ULine));
        var f = spec.Fraction(value);
        var fx = track.Left + track.Width * f;
        if (f > 0f) rt.FillRectangle(new RawRectF(track.Left, track.Top, fx, track.Bottom), B(A(UWhite, 0.85f)));
        rt.FillRadialGlow(new NumVec2(fx, ty), 12f, A(UWhite, 0.35f));
        Diamond(rt, fx, ty, 5.5f, Hex(0x000000));
        Diamond(rt, fx, ty, 4.5f, UWhite);
        HLine(rt, x, x + w, y + h - 0.5f, URule);

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _legendRowRects.Add((minus, $"ins:adj:{key}:{(-spec.Step).ToString(ci)}"));
        _legendRowRects.Add((plus, $"ins:adj:{key}:{spec.Step.ToString(ci)}"));
        _legendRowRects.Add((new RawRectF(track.Left, ty - 10f, track.Right, ty + 10f), "ins:slider:" + key));
        return y + h;
    }

    /// <summary>Option choice: a bordered segmented group, the selected option filled white.</summary>
    private float Choice(DrawTarget rt, float x, float y, float w, string label, string? help, string current, string key, (string Value, string Label)[] options)
    {
        var cy = RowBase(rt, x, y, w, label, help);
        var widths = options.Select(o => TrackW(rt, o.Label.ToUpperInvariant(), _tfSeg!, 0.12f) + 24f).ToArray();
        var total = widths.Sum() + 3f * 2f + 2f * (options.Length - 1);
        var group = new RawRectF(x + w - total, cy - 16f, x + w, cy + 16f);
        rt.DrawRectangle(group, B(ULine), 1f);
        var ox = group.Left + 3f;
        for (var i = 0; i < options.Length; i++)
        {
            var (value, text) = options[i];
            var r = new RawRectF(ox, group.Top + 3f, ox + widths[i], group.Bottom - 3f);
            var sel = string.Equals(current, value, StringComparison.OrdinalIgnoreCase);
            if (sel) rt.FillRectangle(r, B(UWhite));
            TTC(rt, text.ToUpperInvariant(), _tfSeg!, (r.Left + r.Right) * 0.5f, cy, sel ? Hex(0x000000) : ULabel, 0.12f);
            _legendRowRects.Add((r, $"ins:set:{key}:{value}"));
            ox = r.Right + 2f;
        }
        return y + Row;
    }

    /// <summary>"◂ [key] ▸" picker.</summary>
    private float KeyPicker(DrawTarget rt, float x, float y, float w, string label, string? help, int vk, string key)
    {
        var cy = RowBase(rt, x, y, w, label, help);
        KeyStepper(rt, x + w, cy, vk, $"ins:key:{key}:-1", $"ins:key:{key}:1");
        return y + Row;
    }

    private void Arrow(DrawTarget rt, float cx, float cy, bool left, Color4 c)
    {
        var d = left ? -1f : 1f;
        rt.DrawLine(new NumVec2(cx - 2.5f * d, cy - 5f), new NumVec2(cx + 2.5f * d, cy), B(c), 1.4f);
        rt.DrawLine(new NumVec2(cx + 2.5f * d, cy), new NumVec2(cx - 2.5f * d, cy + 5f), B(c), 1.4f);
    }

    /// <summary>Right-aligned "◂ [key] ▸" ending at <paramref name="right"/>, the key in a settings keycap.
    /// Returns its left edge.</summary>
    private float KeyStepper(DrawTarget rt, float right, float cy, int vk, string prevAction, string nextAction)
    {
        var label = KeyLabel(vk);
        var kw = KeycapWidth(rt, label, big: true);
        var next = new RawRectF(right - 18f, cy - 14f, right, cy + 14f);
        var cap = next.Left - 4f - kw;
        var prev = new RawRectF(cap - 22f, cy - 14f, cap - 4f, cy + 14f);
        Arrow(rt, (prev.Left + prev.Right) * 0.5f, cy, true, UMuted);
        Arrow(rt, (next.Left + next.Right) * 0.5f, cy, false, UMuted);
        Keycap(rt, label, cap, cy, big: true);
        _legendRowRects.Add((prev, prevAction));
        _legendRowRects.Add((next, nextAction));
        return prev.Left;
    }

    private float InfoRow(DrawTarget rt, float x, float y, float w, string label, string value, Color4 col)
    {
        var cy = RowBase(rt, x, y, w, label, null);
        TR(rt, value, _tfBold!, x + w, cy, col);
        return y + Row;
    }

    /// <summary>A lit sphere like the design's currency orbs: radial gradient from a white highlight through
    /// <paramref name="col"/> to a dark rim, inside a faint breathing ring, a pale cross turning on its face.</summary>
    private void Orb(DrawTarget rt, float cx, float cy, float r, Color4 col)
    {
        var c = new NumVec2(cx, cy);
        var p = Pulse();
        rt.DrawEllipse(new Ellipse(c, r * 1.43f * (1f + 0.04f * p), r * 1.43f * (1f + 0.04f * p)), B(A(col, 0.2f + 0.15f * p)), 1f);
        var dark = new Color4(col.R * 0.35f, col.G * 0.35f, col.B * 0.35f, 1f);
        rt.FillRadialGradient(c, r, new NumVec2(cx - r * 0.24f, cy - r * 0.32f), r * 1.4f, UWhite, col, dark);
        var ang = Now / 18f * MathF.Tau;
        var d = new NumVec2(MathF.Cos(ang), MathF.Sin(ang)) * r * 0.64f;
        var n = new NumVec2(-d.Y, d.X);
        var ink = B(A(Hex(0xFFFAF0), 0.7f));
        rt.DrawLine(c - d, c + d, ink, 1.3f);
        rt.DrawLine(c - n, c + n, ink, 1.3f);
    }

    /// <summary>Transient message (hotkey feedback) in a small framed plate near the top of the screen.</summary>
    private void DrawToast(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.Toast is not { Length: > 0 } text) return;
        EnsureInsResources();
        BeginUiFrame(ctx);
        var w = MathF.Min(ctx.WindowWidth - 40f, rt.MeasureText(text, _tfMedium!) + 56f);
        var x = MathF.Round((ctx.WindowWidth - w) * 0.5f);
        var r = new RawRectF(x, 90f, x + w, 132f);
        rt.DrawShadow(r, 0f, 20f, 0.8f);
        rt.FillRectangle(r, B(UPanel));
        rt.DrawRectangle(r, B(ULine), 1f);
        Diamond(rt, x + 18f, 111f, 3f, UWhite);
        T(rt, Fit(rt, text, _tfMedium!, w - 46f), _tfMedium!, x + 32f, 111f, UText);
    }

    internal static string KeyLabel(int vk) => vk switch
    {
        0x01 => "LMB", 0x02 => "RMB", 0x04 => "MMB", 0x05 => "M4", 0x06 => "M5",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        0x20 => "Space",
        0 => "—",
        _ => $"0x{vk:X2}",
    };
}
