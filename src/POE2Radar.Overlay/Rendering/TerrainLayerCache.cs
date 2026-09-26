using System.Numerics;
using POE2Radar.Overlay.Draw;

namespace POE2Radar.Overlay;

/// <summary>
/// Caches the terrain bitmap already rasterized through the grid→screen affine transform, so the steady-state
/// per-frame cost is one integer-offset blend instead of an affine resample of the whole (e.g. 1500×1500)
/// terrain bitmap — the resample was ~90% of a map-open frame.
///
/// <para>The layer is the screen plus <see cref="Margin"/> px on every side. It stays valid while the bitmap,
/// the transform's LINEAR part (map zoom / scale) and the window size are unchanged; player movement only
/// changes the translation, which is applied as a whole-pixel blit offset until it drifts past the margin
/// (then the layer is re-rasterized at the new position). Sub-pixel translation is snapped to the nearest
/// pixel (≤0.5 px), which is visually identical to the direct nearest-neighbour draw.</para>
///
/// <para>While the linear part is changing frame-to-frame (zooming, resizing) the caller draws directly, so a
/// zoom animation never pays the larger-than-screen rebuild every frame.</para>
/// </summary>
internal sealed class TerrainLayerCache : IDisposable
{
    public const int Margin = 256;

    private readonly DrawLayer _layer = new();
    private DrawBitmap? _builtBmp;
    private float _a, _b, _c, _d;           // linear part the layer was rasterized with
    private float _tx, _ty;                 // translation the layer was rasterized with
    private int _w, _h;                     // screen size the layer was built for
    private float _pa, _pb, _pc, _pd;       // previous frame's linear part (zoom-settle detection)
    private bool _hasPrev;

    /// <summary>Number of times the layer was (re)rasterized — for tests/diagnostics.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Number of frames drawn from the layer (rebuild frames included).</summary>
    public int Hits { get; private set; }

    /// <summary>Draw <paramref name="bmp"/> through <paramref name="m"/> via the cached layer. Returns false
    /// when the caller should draw directly instead (linear part not yet stable, or layer allocation failed).</summary>
    public bool TryDraw(DrawTarget rt, DrawBitmap bmp, Matrix3x2 m, int screenW, int screenH)
    {
        var stable = _hasPrev && m.M11 == _pa && m.M12 == _pb && m.M21 == _pc && m.M22 == _pd;
        _pa = m.M11; _pb = m.M12; _pc = m.M21; _pd = m.M22; _hasPrev = true;
        if (!stable || screenW <= 0 || screenH <= 0) return false;

        int dx = 0, dy = 0;
        var valid = _layer.HasContent && ReferenceEquals(bmp, _builtBmp)
            && m.M11 == _a && m.M12 == _b && m.M21 == _c && m.M22 == _d && screenW == _w && screenH == _h;
        if (valid)
        {
            var fx = MathF.Round(m.M31 - _tx);
            var fy = MathF.Round(m.M32 - _ty);
            if (!(MathF.Abs(fx) <= Margin && MathF.Abs(fy) <= Margin)) valid = false; // also rejects NaN
            else { dx = (int)fx; dy = (int)fy; }
        }
        if (!valid)
        {
            var lm = m;
            lm.M31 += Margin;
            lm.M32 += Margin;
            _layer.RenderBitmap(screenW + 2 * Margin, screenH + 2 * Margin, bmp, lm);
            if (!_layer.HasContent) { _builtBmp = null; return false; }
            _builtBmp = bmp;
            _a = m.M11; _b = m.M12; _c = m.M21; _d = m.M22;
            _tx = m.M31; _ty = m.M32; _w = screenW; _h = screenH;
            Rebuilds++;
        }
        rt.DrawLayer(_layer, dx - Margin, dy - Margin);
        Hits++;
        return true;
    }

    /// <summary>Drop the cached layer (e.g. the map closed) so it doesn't pin a stale bitmap.</summary>
    public void Invalidate()
    {
        _layer.Invalidate();
        _builtBmp = null;
        _hasPrev = false;
    }

    public void Dispose() => _layer.Dispose();
}
