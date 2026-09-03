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
    private TerrainBitmap? _terrain;

    // Per-icon-name geometry, built lazily from the SVG IconLibrary and cached for the renderer's
    // lifetime. A name that can't be resolved/parsed is mapped to the Circle geometry so something
    // always draws; the cache may therefore point several keys at one instance (deduped on Dispose).
    private readonly Dictionary<string, DrawPath?> _geoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Draw-only guidance routes rendered on the WORLD GROUND, shown when the big map is CLOSED.
    /// Each selected target's smoothed grid waypoints are converted to world space
    /// (grid × <see cref="GridConstants.GridToWorld"/>, at the player's world-Z plane) and projected
    /// via the camera WorldToScreen matrix — the same projection used for nameplates. Lines connect
    /// consecutive waypoints; a marker dot sits on each. Z is approximated by the player's height, so
    /// the line sits at the player's feet plane (it can float/sink on steep slopes — height TBD).
    /// </summary>
    private void DrawPathsWorld(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.SelectedPaths.Count == 0) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;

        // Ground plane height = the LIVE player feet Z (read this frame, not from the world-rate entity
        // list — the local player is filtered out of that). Paths sit at the player's feet.
        var z = ctx.PlayerWorld?.Z ?? 0f;

        // Project a ground-plane world point to screen; null when it's behind the camera.
        NumVec2? Proj(float wx, float wy)
        {
            var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
            if (cw <= 0.0001f) return null;
            var cxp = wx * m[0] + wy * m[4] + z * m[8] + m[12];
            var cyp = wx * m[1] + wy * m[5] + z * m[9] + m[13];
            return new NumVec2((cxp / cw / 2f + 0.5f) * W, (0.5f - cyp / cw / 2f) * H);
        }

        // The line head is pinned to the player's LIVE world position every frame, so the first segment is
        // always (you → next waypoint) and tracks you smoothly between world-rate path updates.
        NumVec2? anchor = ctx.PlayerWorld is { } pw ? Proj(pw.X, pw.Y) : null;

        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            _bPath!.Color = PathColor(path.ColorSlot);

            NumVec2? prev = anchor;
            foreach (var (gx, gy) in path.Points)
            {
                float wx = gx * GridConstants.GridToWorld, wy = gy * GridConstants.GridToWorld;
                if (Proj(wx, wy) is not { } p) { prev = null; continue; } // waypoint behind camera — break the line
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 3f);
                rt.FillEllipse(new Ellipse(p, 4f, 4f), _bPath);
                prev = p;
            }
        }
    }

    /// <summary>
    /// The unit-space (≈[-1,1], centered) path geometry for a named library icon, built once and cached.
    /// Each library path's <c>d</c> is parsed (<see cref="SvgPath"/>) and normalized from its viewBox into
    /// the unit space the old hardcoded geometries used, so <see cref="DrawIcon"/> can stamp it with the
    /// same scale+translate transform. Unknown/unparseable names fall back to "Circle".
    /// </summary>
    private DrawPath? GetGeometry(string? name)
    {
        name ??= "Circle";
        if (_geoCache.TryGetValue(name, out var cached)) return cached;

        var built = BuildGeometry(name);
        if (built is null && !name.Equals("Circle", StringComparison.OrdinalIgnoreCase))
            built = GetGeometry("Circle"); // shared fallback instance (deduped on Dispose)
        _geoCache[name] = built;
        return built;
    }

    private DrawPath? BuildGeometry(string name)
    {
        if (!IconLibrary.Map.TryGetValue(name, out var def)) return null;

        // viewBox → unit space: center on the viewBox, uniform-scale so the larger half-extent maps to 1
        // (aspect-preserving; a square 0 0 24 24 box puts an edge point at ±1, matching the old shapes).
        float cx = def.VbX + def.VbW / 2f, cy = def.VbY + def.VbH / 2f;
        float scale = 2f / MathF.Max(def.VbW, def.VbH);
        NumVec2 N(NumVec2 p) => new((p.X - cx) * scale, (p.Y - cy) * scale);

        var path = new SkiaSharp.SKPath { FillType = SkiaSharp.SKPathFillType.EvenOdd };
        bool any = false;
        foreach (var d in def.Paths)
            foreach (var fig in SvgPath.Parse(d))
            {
                var s = N(fig.Start);
                path.MoveTo(s.X, s.Y);
                foreach (var seg in fig.Segs)
                {
                    var e = N(seg.End);
                    switch (seg.Kind)
                    {
                        case SvgPath.SegKind.Line:
                            path.LineTo(e.X, e.Y);
                            break;
                        case SvgPath.SegKind.Cubic:
                            var c1 = N(seg.C1); var c2 = N(seg.C2);
                            path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, e.X, e.Y);
                            break;
                        case SvgPath.SegKind.Quad:
                            var q = N(seg.C1);
                            path.QuadTo(q.X, q.Y, e.X, e.Y);
                            break;
                    }
                }
                if (fig.Closed) path.Close();
                any = true;
            }
        if (any) return new DrawPath(path);
        path.Dispose();
        return null;
    }

    /// <summary>Draw a named library icon at screen point p with radius r, by stamping its cached unit
    /// geometry via a per-call scale+translate transform.</summary>
    private void DrawIcon(DrawTarget rt, string shape, NumVec2 p, float r, DrawBrush brush, bool filled)
    {
        var geo = GetGeometry(shape);
        if (geo is null) return;
        var prev = rt.Transform;
        rt.Transform = new Matrix3x2(r, 0f, 0f, r, p.X, p.Y);
        if (filled) rt.FillGeometry(geo, brush); else rt.DrawGeometry(geo, brush, 1.5f / r);
        rt.Transform = prev;
    }

    /// <summary>Parse a <c>#RRGGBB</c> color + 0..1 opacity into a Color4 (falls back to opaque white).</summary>
    private static Color4 ParseColor(string hex, float opacity)
    {
        var a = Math.Clamp(opacity, 0f, 1f);
        if (hex is { Length: >= 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new Color4(r / 255f, g / 255f, b / 255f, a);
        return new Color4(1f, 1f, 1f, a);
    }

    /// <summary>Clamp a 0..1 channel to a 0..255 byte (rounded).</summary>
    private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    private void DrawMap(DrawTarget rt, RenderContext ctx)
    {
        // MapCenter = window center + DefaultShift(0,-20) + Shift + manual offset.
        var center = new NumVec2(
            ctx.WindowWidth  * 0.5f + ctx.Map.ShiftX + ctx.OffsetX,
            ctx.WindowHeight * 0.5f + ctx.Map.ShiftY - 20f + ctx.OffsetY);
        var scale = ctx.Map.Zoom * (ctx.WindowHeight / 677f) * ctx.ScaleMul;
        var player = ctx.PlayerGrid;

        // Terrain bitmap, projected via the same affine grid→screen transform.
        if (ctx.ShowTerrain && ctx.Terrain is { } t)
        {
            _terrain ??= new TerrainBitmap(rt);
            var ci = ParseColor(ctx.TerrainStyle.InteriorColor, ctx.TerrainStyle.InteriorOpacity);
            var ce = ParseColor(ctx.TerrainStyle.EdgeColor, ctx.TerrainStyle.EdgeOpacity);
            var terrainStyle = new TerrainBitmap.TerrainStyle(
                ToByte(ci.B), ToByte(ci.G), ToByte(ci.R), ToByte(ci.A),
                ToByte(ce.B), ToByte(ce.G), ToByte(ce.R), ToByte(ce.A));
            _terrain.EnsureBuiltRaw(t.Walkable, t.Width, t.Height, ctx.AreaHash, inTransition: false, terrainStyle);
            if (_terrain.Bitmap is { } bmp)
            {
                var p00 = Project(new NumVec2(0, 0), player, center, scale);
                var p10 = Project(new NumVec2(t.Width, 0), player, center, scale);
                var p01 = Project(new NumVec2(0, t.Height), player, center, scale);
                var ex = (p10 - p00) / t.Width;
                var ey = (p01 - p00) / t.Height;
                var prev = rt.Transform;
                rt.Transform = new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
                rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, t.Width, t.Height));
                rt.Transform = prev;
            }
        }

        // Entity dots, decided by the UNIFIED display ruleset (single source of truth). Resolve picks
        // the first enabled rule that matches the entity (top-down, explicit precedence); null or a
        // Hide rule → not drawn; otherwise draw the rule's shape/color/size + optional label. (Junk is
        // still a pre-filter in Phase 1; the API serves every entity regardless for troubleshooting.)
        foreach (var e in ctx.Entities)
        {
            if (ctx.HideJunk && JunkFilter.IsJunk(e.Metadata)) continue;

            var rule = ctx.Resolve?.Invoke(e);
            if (rule is null || rule.Hide) continue;

            var p = Project(new NumVec2(e.Grid.X, e.Grid.Y), player, center, scale);
            _bStyle!.Color = ParseColor(rule.Color, rule.Opacity);
            DrawIcon(rt, rule.Shape, p, rule.Size, _bStyle, filled: true);
            if (!string.IsNullOrEmpty(rule.Label))
                rt.DrawText(rule.Label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bStyle, DrawTextOptions.Clip);
        }

        // Static tile landmarks (boss arena, treasure, …). Each is styled by its matching "Tile"
        // display rule (unified ruleset) when one applies — shape/color/size/label, or hidden; with no
        // matching tile rule it falls back to the default Landmark style. The layer draws when the
        // default style is enabled OR a tile resolver is wired (so tile rules work even if the default
        // landmark icon is turned off).
        var lmStyle = ctx.Styles.Landmark;
        if (lmStyle.Enabled || ctx.ResolveTile != null)
        {
            var defColor = ParseColor(lmStyle.Color, lmStyle.Opacity);
            foreach (var lm in ctx.Landmarks)
            {
                var tr = ctx.ResolveTile?.Invoke(lm.Path);
                if (tr is { Hide: true }) continue;                 // a tile rule hides this landmark
                if (tr is null && !lmStyle.Enabled) continue;       // no rule + default layer off → skip
                var shape = tr?.Shape ?? lmStyle.Shape;
                var color = tr != null ? ParseColor(tr.Color, tr.Opacity) : defColor;
                var size  = tr?.Size ?? lmStyle.Size;
                var p = Project(new NumVec2(lm.Center.X, lm.Center.Y), player, center, scale);
                _bStyle!.Color = color;
                DrawIcon(rt, shape, p, size, _bStyle, filled: true);
                // Rule label wins; else curated friendly label (if enabled); else the derived name.
                var label = tr?.Label is { Length: > 0 } rl ? rl
                          : (ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name);
                rt.DrawText(label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bStyle, DrawTextOptions.Clip);
            }
        }

        // Draw-only guidance routes: one full smoothed A* polyline per selected landmark, each in its
        // own legend color (precomputed by RadarApp). Drawn whenever a target is selected (selecting =
        // intent to navigate); not gated on ShowPath.
        DrawPaths(rt, ctx, player, center, scale);

        // Runeshape monoliths: value-coloured ring + N badge + value/reward label.
        DrawMonoliths(rt, ctx, player, center, scale);

        // Player blip on top (toggleable — some prefer no self-marker).
        if (ctx.ShowPlayerBlip)
            rt.FillEllipse(new Ellipse(center, 5f, 5f), _bPlayer!);
    }

    /// <summary>
    /// Draw-only guidance routes. Each selected landmark gets its own smoothed walkable A* polyline
    /// (grid → screen), drawn in that landmark's legend color (<see cref="PathColor"/>). Routes are
    /// precomputed per-target by RadarApp; here we just project and stroke them.
    /// </summary>
    private void DrawPaths(DrawTarget rt, RenderContext ctx, NumVec2 player, NumVec2 center, float scale)
    {
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 1) continue;
            _bPath!.Color = PathColor(path.ColorSlot);
            // Anchor the line head at the live player marker (center) so the route stays attached to the
            // player every frame, even between world-rate cursor updates / replans.
            NumVec2? prev = center;
            foreach (var (gx, gy) in path.Points)
            {
                var p = Project(new NumVec2(gx, gy), player, center, scale);
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 2.4f);
                prev = p;
            }
        }
    }

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }
}
