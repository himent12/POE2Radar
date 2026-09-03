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
    /// <summary>The F10 atlas route: the shortest path (through the node connection graph) from the player's
    /// current node to the picked destination. Each canvas-space (relPos) waypoint is projected with the same
    /// atlas homography as the rings, then drawn as a connected polyline (dark underlay + bright cyan line for
    /// contrast over the busy atlas), with a node dot at each hop, a green START disc and a gold GOAL ring.
    /// Off-screen segments are simply clipped by Direct2D, so the route still reads when the destination has
    /// been panned off-screen. Drawn UNDER the highlight rings.</summary>
    private void DrawAtlasRoute(DrawTarget rt, RenderContext ctx)
    {
        var start = ctx.AtlasStart; var end = ctx.AtlasEnd; var route = ctx.AtlasRoute;
        var autos = ctx.AtlasAutoRoutes; var current = ctx.AtlasCurrent;
        bool hasAuto = autos is { Count: > 0 };
        if (start is null && end is null && (route is null || route.Count == 0) && !hasAuto && current is null) return;
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        NumVec2 Proj(NumVec2 p) { var w = h6 * p.X + h7 * p.Y + 1f; if (MathF.Abs(w) < 1e-6f) w = 1f; return new NumVec2((h0 * p.X + h1 * p.Y + h2) / w, (h3 * p.X + h4 * p.Y + h5) / w); }
        // Off-screen (culled) atlas nodes report NOISY relPos, so segments touching them wobble and their
        // chevrons spin in random directions. Gate on projected screen position: draw a line only when a
        // segment is at least partly on-screen, and chevrons only when BOTH endpoints are on-screen (so the
        // arrow direction is trustworthy). W/H from the context; margins keep edge-crossing routes visible.
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        bool On(NumVec2 p, float m) => p.X >= -m && p.X <= W + m && p.Y >= -m && p.Y <= H + m;

        var dark = new Color4(0f, 0f, 0f, 0.6f);
        var bright = new Color4(0.235f, 0.86f, 1f, 0.95f);   // cyan
        var green = new Color4(0.43f, 0.91f, 0.53f, 1f);
        var gold = new Color4(0.878f, 0.702f, 0.255f, 1f);

        // ── Auto-routes (improvement 1): one polyline per tracked tile, in its rule colour, with a hop chip
        // at the target + directional chevrons (#4). Drawn UNDER the manual F10 route + the marks. ──
        if (hasAuto)
        {
            // #4 per-edge interleaving: routes from the accessible frontier share their first segments
            // constantly, so a shared edge gets each route's chevrons at a distinct phase slot — overlapping
            // colours stay visible instead of the last-drawn one overpainting. Keyed by canvas-space coords.
            var edgeRoutes = new Dictionary<(long, long, long, long), List<int>>();
            for (var ri = 0; ri < autos!.Count; ri++)
            {
                if (autos[ri].Points is not { Count: >= 2 } rp) continue;
                for (var i = 1; i < rp.Count; i++)
                {
                    var k = AtlasEdgeKey(rp[i - 1], rp[i]);
                    if (!edgeRoutes.TryGetValue(k, out var l)) edgeRoutes[k] = l = new List<int>();
                    l.Add(ri);
                }
            }
            float spacingMul = MathF.Max(1.5f, ctx.AtlasRouteArrowSpacing);
            const float chevron = 7f;
            float spacing = chevron * spacingMul;
            for (var ri = 0; ri < autos.Count; ri++)
            {
                var ar = autos[ri];
                if (ar.Points is not { Count: >= 2 } rp) continue;
                // Softer + thinner than the manual F10 route: auto-routes are ambient guides to every tracked
                // tile, so they shouldn't dominate the screen as a thick web. Lower alpha + lighter underlay.
                var col = string.IsNullOrEmpty(ar.Color) ? new Color4(0.235f, 0.86f, 1f, 0.65f) : ParseColor(ar.Color, 0.65f);
                var pts = new NumVec2[rp.Count];
                for (var i = 0; i < rp.Count; i++) pts[i] = Proj(rp[i]);
                for (var i = 1; i < pts.Length; i++)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    if (!On(a, 64f) && !On(b, 64f)) continue;   // fully off-screen segment → skip (relPos is noise)
                    _bStyle!.Color = new Color4(0f, 0f, 0f, 0.4f); rt.DrawLine(a, b, _bStyle, 3f);
                    _bStyle.Color = col; rt.DrawLine(a, b, _bStyle, 1.75f);
                    if (!On(a, 0f) || !On(b, 0f)) continue;     // chevron direction only trustworthy fully on-screen
                    var key = AtlasEdgeKey(rp[i - 1], rp[i]);
                    float phase = 0.5f;
                    if (edgeRoutes.TryGetValue(key, out var sh) && sh.Count > 0)
                    {
                        var local = sh.IndexOf(ri); if (local < 0) local = 0;
                        phase = (local + 0.5f) / sh.Count;
                    }
                    var carry = spacing * phase;   // reset per segment so the phase is honoured on every edge
                    DrawAtlasChevrons(rt, a, b, col, chevron, spacing, ref carry);
                }
                // Hop-count chip at the target end (only when the target is on-screen).
                var tgt = pts[^1];
                if (On(tgt, 0f))
                {
                    string ht = ar.Hops.ToString();
                    rt.FillRectangle(new RawRectF(tgt.X - 11f, tgt.Y - 26f, tgt.X + 11f, tgt.Y - 10f), _bPanel!);
                    rt.DrawText(ht, _tf!, new Rect(tgt.X - 9f, tgt.Y - 26f, tgt.X + 11f, tgt.Y - 10f), _bText!, DrawTextOptions.Clip);
                }
            }
        }

        if (route is { Count: >= 2 })
        {
            // Graph polyline: dark underlay then bright line (cheap outline for contrast over the atlas), hop dots.
            var pts = new NumVec2[route.Count];
            for (var i = 0; i < route.Count; i++) pts[i] = Proj(route[i]);
            float mspacing = 9f * MathF.Max(1.5f, ctx.AtlasRouteArrowSpacing);
            for (var i = 1; i < pts.Length; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                if (!On(a, 64f) && !On(b, 64f)) continue;   // fully off-screen segment → skip (relPos is noise)
                _bStyle!.Color = dark; rt.DrawLine(a, b, _bStyle, 7f);
                _bStyle.Color = bright; rt.DrawLine(a, b, _bStyle, 3.5f);
                if (!On(a, 0f) || !On(b, 0f)) continue;     // chevrons + hop dots only when fully on-screen
                if (i < pts.Length - 1) rt.DrawEllipse(new Ellipse(b, 4f, 4f), _bStyle, 2f);
                var carry = mspacing * 0.5f; DrawAtlasChevrons(rt, a, b, dark, 9f, mspacing, ref carry);
            }
        }
        else if (start is { } sa && end is { } eb)
        {
            // No graph path between the two — draw a direct dashed-ish straight line so the link is still shown.
            var a = Proj(sa); var b = Proj(eb);
            _bStyle!.Color = dark; rt.DrawLine(a, b, _bStyle, 6f);
            _bStyle.Color = gold; rt.DrawLine(a, b, _bStyle, 2.5f);
        }

        // START (green disc) + END (gold ring) markers — drawn whenever set, even before a path exists.
        if (start is { } s) { var p = Proj(s); _bStyle!.Color = green; rt.DrawEllipse(new Ellipse(p, 8f, 8f), _bStyle, 3f); rt.DrawEllipse(new Ellipse(p, 3f, 3f), _bStyle, 2f); }
        if (end is { } e) { var p = Proj(e); _bStyle!.Color = gold; rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 3f); rt.DrawEllipse(new Ellipse(p, 4f, 4f), _bStyle, 2f); }

        // "YOU ARE HERE" — the player's current atlas node (improvement 1): a cyan double-ring with a dark
        // outline + filled centre so it reads as the route origin regardless of the tile underneath.
        if (current is { } cur)
        {
            var p = Proj(cur);
            _bStyle!.Color = new Color4(0f, 0f, 0f, 0.7f); rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 4.5f);
            _bStyle.Color = new Color4(0.3f, 0.95f, 1f, 1f);
            rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 2.5f);
            rt.FillEllipse(new Ellipse(p, 3f, 3f), _bStyle);
        }
    }

    /// <summary>
    /// Atlas overlay: highlight atlas map nodes on the open Atlas screen. Each node's canvas-space
    /// position (RelativePos) is projected to screen via the atlas transform. Tracked/arrowed maps draw a
    /// ring in their rule colour; off-screen arrowed maps get an edge arrow pointing toward them.
    /// </summary>
    private void DrawAtlas(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.AtlasNodes is not { Count: > 0 } marks) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        // Homography: w = h6·x + h7·y + 1; screen = (h0·x+h1·y+h2, h3·x+h4·y+h5) / w. (shear/persp 0 ⇒ affine)
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        float ccx = W * 0.5f, ccy = H * 0.5f;
        foreach (var n in marks)
        {
            var w = h6 * n.X + h7 * n.Y + 1f;
            if (MathF.Abs(w) < 1e-6f) continue;
            var sx = (h0 * n.X + h1 * n.Y + h2) / w;
            var sy = (h3 * n.X + h4 * n.Y + h5) / w;
            var onScreen = sx >= 0 && sx <= W && sy >= 0 && sy <= H;
            var col = string.IsNullOrEmpty(n.Color) ? new Color4(0.235f, 0.86f, 1f, 1f) : ParseColor(n.Color, 1f);

            // OFF-SCREEN: if this map has the arrow rule, draw an edge arrow pointing toward it; else skip.
            if (!onScreen)
            {
                if (n.Arrow) DrawAtlasArrow(rt, sx, sy, ccx, ccy, W, H, col, n.Label);
                continue;
            }

            var c = new NumVec2(sx, sy);
            if (n.Selected || n.Arrow || n.Nav)
            {
                // ONE clean ring in the rule colour (Citadel gold / Boss red / …) + a small filled centre.
                // Biome (when enabled) is conveyed by the CENTRE-DOT colour rather than a second full ring,
                // so a tracked node reads as a single target instead of a stack of concentric rings. Primary
                // targets (ring/arrow rules) are drawn a touch larger than nav-only route endpoints.
                var r = (n.Selected || n.Arrow) ? 12f : 9f;
                _bStyle!.Color = col;
                rt.DrawEllipse(new Ellipse(c, r, r), _bStyle, 2.5f);
                _bStyle.Color = ctx.AtlasBiomeBorder ? BiomeColor(n.Biome) : col;
                rt.FillEllipse(new Ellipse(c, 3f, 3f), _bStyle);
            }
            else if (ctx.AtlasDrawAll && n.IconType > 0) // DEBUG (AtlasDrawAll): content-tag element
            {
                _bStyle!.Color = new Color4(1f, 0.9f, 0.2f, 0.9f);
                rt.DrawEllipse(new Ellipse(c, 7f, 7f), _bStyle, 2f);
            }
            else if (ctx.AtlasDrawAll && n.Visited)
            {
                _bStyle!.Color = new Color4(1f, 0.2f, 1f, 1f);
                rt.DrawEllipse(new Ellipse(c, 16f, 16f), _bStyle, 3f);
                rt.DrawEllipse(new Ellipse(c, 8f, 8f), _bStyle, 2f);
            }
            else if (ctx.AtlasDrawAll)
            {
                _bStyle!.Color = n.HasContent ? new Color4(1f, 0.62f, 0.26f, 0.95f)
                               : new Color4(0.43f, 0.91f, 0.53f, 0.85f);
                rt.DrawEllipse(new Ellipse(c, 11f, 11f), _bStyle, 2f);
            }

            // #5 content icons: drawn in a row above the node, but ONLY on FOGGED nodes (the game already
            // renders its own content icons on revealed ones, so we'd double up). Tracked rings + icons can
            // co-exist on a fogged tracked node. ring radius below mirrors the ring drawn above.
            if (ctx.AtlasContentIcons && !n.Visible && n.ContentIcons is { Count: > 0 } && _atlasIcons != null)
            {
                float ringR = (n.Selected || n.Arrow) ? 12f : (n.Nav ? 9f : (ctx.AtlasDrawAll ? 11f : 0f));
                DrawAtlasContentIcons(rt, n.ContentIcons, sx, sy - ringR, ctx.AtlasContentIconSize);
            }

            var label = n.Label ?? (ctx.AtlasDrawAll && n.IconType > 0 ? n.IconType.ToString() : null);
            if (label != null)
            {
                // Backing chip behind the label (improvement 2) so map names stay readable over the busy
                // atlas art. Width is estimated from the label length (Direct2D layout measuring is heavier
                // than it's worth here); the chip sits just right of the ring.
                float lx = sx + 24f, ly = sy - 9f;
                float lw = label.Length * 7.0f + 8f;
                rt.FillRectangle(new RawRectF(lx - 4f, ly - 1f, lx + lw, ly + 17f), _bPanel!);
                rt.DrawText(label, _tf!, new Rect(lx, ly, lx + lw + 40f, ly + 18f), _bText!, DrawTextOptions.Clip);
            }
        }
    }

    /// <summary>Draw a centered row of content icons (#5) above a node. <paramref name="cx"/> is the node's
    /// screen X; <paramref name="topY"/> is the node ring's top — icons sit just above it. Square cells of
    /// <paramref name="iconH"/> px; only basenames present in the cache draw. A dark backing keeps the row
    /// readable over the busy atlas art. Called only for FOGGED nodes (the game draws its own on revealed ones).</summary>
    private void DrawAtlasContentIcons(DrawTarget rt, IReadOnlyList<string> basenames, float cx, float topY, float iconH)
    {
        if (iconH < 6f) iconH = 6f;
        var cnt = 0;
        foreach (var bn in basenames) if (_atlasIcons!.Get(rt, bn) != null) cnt++;
        if (cnt == 0) return;
        const float gap = 3f;
        float totalW = cnt * iconH + (cnt - 1) * gap;
        float ix = cx - totalW * 0.5f;
        float iy = topY - iconH - 5f;
        rt.FillRectangle(new RawRectF(ix - 3f, iy - 2f, ix + totalW + 3f, iy + iconH + 2f), _bPanel!);
        foreach (var bn in basenames)
        {
            var bmp = _atlasIcons!.Get(rt, bn);
            if (bmp == null) continue;
            // Vortice's Rect ctor is (x, y, WIDTH, HEIGHT) — NOT (left, top, right, bottom) — and this
            // overload's rect is the DESTINATION (whole bitmap drawn as source). The old call passed an
            // LTRB-shaped rect to the source-only overload with no destination, so the icon drew at the
            // screen origin at native size while sampling a garbage region → invisible (only the panel showed).
            rt.DrawBitmap(bmp, new Rect(ix, iy, iconH, iconH), 1f, BitmapInterpolationMode.Linear, (Rect?)null);
            ix += iconH + gap;
        }
    }

    /// <summary>Lay stroked arrowhead chevrons (a row of "&gt;" pointing a→b) at <paramref name="spacing"/>
    /// intervals along the segment (#4). <paramref name="carry"/> holds the leftover distance into the next
    /// segment so spacing stays even across a multi-segment route. Ported from the GameHelper2 Atlas plugin's
    /// DrawChevrons (filled triangles → stroked chevrons here, cheaper in Direct2D and reads the same).</summary>
    private void DrawAtlasChevrons(DrawTarget rt, NumVec2 a, NumVec2 b, Color4 color, float size, float spacing, ref float carry)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) { return; }
        var dir = d / len;
        var perp = new NumVec2(-dir.Y, dir.X);
        float half = size * 0.5f;
        _bStyle!.Color = color;
        float t = carry;
        while (t < len)
        {
            var p = a + dir * t;
            var tip = p + dir * half;
            var baseMid = p - dir * half;
            rt.DrawLine(tip, baseMid + perp * half, _bStyle, 1.5f);
            rt.DrawLine(tip, baseMid - perp * half, _bStyle, 1.5f);
            t += spacing;
        }
        carry = t - len;
    }

    /// <summary>Direction-independent key for a route edge (canvas-space endpoints rounded to int), so a
    /// segment shared by two routes hashes the same regardless of which way each route walks it (#4).</summary>
    private static (long, long, long, long) AtlasEdgeKey(NumVec2 a, NumVec2 b)
    {
        long ax = (long)MathF.Round(a.X), ay = (long)MathF.Round(a.Y);
        long bx = (long)MathF.Round(b.X), by = (long)MathF.Round(b.Y);
        bool aFirst = ax < bx || (ax == bx && ay <= by);
        return aFirst ? (ax, ay, bx, by) : (bx, by, ax, ay);
    }

    // Atlas biome index (0..12) → border colour (improvement 2). Order matches the dashboard BIOMES list:
    // Grass, Sand, Swamp, Forest, Snow, Stone, Volcanic, Coast, Cave, Vaal, Water, Desert, Special.
    private static readonly Color4[] BiomeColors =
    {
        new(0.45f, 0.78f, 0.36f, 1f), // 0 Grass
        new(0.85f, 0.74f, 0.36f, 1f), // 1 Sand
        new(0.40f, 0.62f, 0.35f, 1f), // 2 Swamp
        new(0.22f, 0.55f, 0.30f, 1f), // 3 Forest
        new(0.80f, 0.86f, 0.92f, 1f), // 4 Snow
        new(0.62f, 0.60f, 0.58f, 1f), // 5 Stone
        new(0.86f, 0.40f, 0.25f, 1f), // 6 Volcanic
        new(0.38f, 0.70f, 0.82f, 1f), // 7 Coast
        new(0.55f, 0.45f, 0.65f, 1f), // 8 Cave
        new(0.80f, 0.30f, 0.40f, 1f), // 9 Vaal
        new(0.30f, 0.55f, 0.85f, 1f), // 10 Water
        new(0.88f, 0.78f, 0.45f, 1f), // 11 Desert
        new(0.75f, 0.55f, 0.85f, 1f), // 12 Special
    };

    private static Color4 BiomeColor(int b) => (b >= 0 && b < BiomeColors.Length) ? BiomeColors[b] : new Color4(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>Draw an edge arrow pointing from screen-centre toward an OFF-SCREEN atlas map (sx,sy), so
    /// you can pan toward high-value maps you can't zoom out far enough to see. Clamped to a screen-edge
    /// inset, coloured by the rule, labelled with the map/content.</summary>
    private void DrawAtlasArrow(DrawTarget rt, float sx, float sy, float cx, float cy, float W, float H, Color4 col, string? label)
    {
        float dx = sx - cx, dy = sy - cy;
        float len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1f) return;
        float ux = dx / len, uy = dy / len;
        const float margin = 46f;
        float tX = MathF.Abs(ux) > 1e-4f ? (W * 0.5f - margin) / MathF.Abs(ux) : 1e9f;
        float tY = MathF.Abs(uy) > 1e-4f ? (H * 0.5f - margin) / MathF.Abs(uy) : 1e9f;
        float t = MathF.Min(tX, tY);
        float ex = cx + ux * t, ey = cy + uy * t;     // point on the inset screen edge
        float px = -uy, py = ux;                       // perpendicular
        var tip = new NumVec2(ex + ux * 11f, ey + uy * 11f);
        var bl = new NumVec2(ex - ux * 9f + px * 10f, ey - uy * 9f + py * 10f);
        var br = new NumVec2(ex - ux * 9f - px * 10f, ey - uy * 9f - py * 10f);
        _bStyle!.Color = col;
        rt.DrawLine(tip, bl, _bStyle, 4f);
        rt.DrawLine(tip, br, _bStyle, 4f);
        rt.DrawLine(bl, br, _bStyle, 4f);              // close the arrowhead triangle
        if (label != null)
        {
            // Pull the label fully on-screen (inset from the arrow toward centre) and back it with a chip so
            // it stays readable over the map art and doesn't clip off the edge like the bare text did.
            float lw = label.Length * 7.0f + 8f;
            float lx = ex - ux * 30f - lw * 0.5f, ly = ey - uy * 22f - 9f;
            lx = Math.Clamp(lx, 2f, W - lw - 2f);
            ly = Math.Clamp(ly, 2f, H - 20f);
            rt.FillRectangle(new RawRectF(lx - 3f, ly - 1f, lx + lw, ly + 17f), _bPanel!);
            rt.DrawText(label, _tf!, new Rect(lx, ly, lx + lw + 40f, ly + 18f), _bText!, DrawTextOptions.Clip);
        }
    }
}
