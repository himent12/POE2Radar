using System.Numerics;
using SkiaSharp;

namespace POE2Radar.Overlay.Draw;

/// <summary>Float RGBA 0..1, matching the old Vortice.Mathematics.Color4 layout (R,G,B,A).</summary>
public readonly struct Color4
{
    public readonly float R, G, B, A;
    public Color4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    public SKColor ToSk() => new(ToByte(R), ToByte(G), ToByte(B), ToByte(A));
    private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
}

/// <summary>Left/Top/Right/Bottom rectangle (Vortice.RawRectF).</summary>
public readonly struct RawRectF
{
    public readonly float Left, Top, Right, Bottom;
    public RawRectF(float left, float top, float right, float bottom)
    { Left = left; Top = top; Right = right; Bottom = bottom; }
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>Vortice.Mathematics.Rect: (X, Y, Width, Height). OverlayRenderer's DrawText calls often
/// pass left/top/right/bottom through this ctor; DrawTarget.DrawText treats X/Y as the origin.</summary>
public readonly struct Rect
{
    public readonly float X, Y, Width, Height;
    public Rect(float x, float y, float width, float height) { X = x; Y = y; Width = width; Height = height; }
}

public readonly struct Ellipse
{
    public readonly Vector2 Point;
    public readonly float RadiusX, RadiusY;
    public Ellipse(Vector2 point, float radiusX, float radiusY) { Point = point; RadiusX = radiusX; RadiusY = radiusY; }
}

public enum DrawTextOptions { None = 0, Clip = 1 }
public enum BitmapInterpolationMode { NearestNeighbor = 0, Linear = 1 }

public sealed class DrawBrush : IDisposable
{
    public Color4 Color;
    public DrawBrush(Color4 color) => Color = color;
    public void Dispose() { }
}

public sealed class DrawPath : IDisposable
{
    internal SKPath Path { get; }
    public DrawPath(SKPath path) => Path = path;
    public void Dispose() => Path.Dispose();
}

public sealed class DrawBitmap : IDisposable
{
    internal SKBitmap Bitmap { get; }
    private SKImage? _image;
    public DrawBitmap(SKBitmap bitmap) => Bitmap = bitmap;

    /// <summary>The bitmap as an <see cref="SKImage"/>, built once. Every DrawBitmap owner fills its pixels
    /// up front and never mutates them afterwards, so the bitmap is marked immutable and the image SHARES its
    /// pixels. (Calling <c>SKImage.FromBitmap</c> per draw on a mutable bitmap deep-copied the whole pixel
    /// buffer every frame — ~9 MB for a 1500×1500 terrain bake.)</summary>
    internal SKImage Image
    {
        get
        {
            if (_image is null)
            {
                Bitmap.SetImmutable();
                _image = SKImage.FromBitmap(Bitmap);
            }
            return _image;
        }
    }

    public void Dispose() { _image?.Dispose(); _image = null; Bitmap.Dispose(); }
}

/// <summary>An offscreen premultiplied-BGRA raster layer: a bitmap is rasterized into it ONCE (through an
/// arbitrary affine transform) and later blitted to the target with a plain integer-offset copy. Used to
/// cache the transformed terrain so the per-frame cost is a straight blend instead of a full affine resample.</summary>
public sealed class DrawLayer : IDisposable
{
    private SKSurface? _surface;
    private SKImage? _snapshot;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasContent => _snapshot is not null;
    internal SKImage? Snapshot => _snapshot;

    /// <summary>Clear the layer to transparent and draw <paramref name="bmp"/> (whole image at its origin)
    /// through <paramref name="m"/> — exactly what <see cref="DrawTarget.DrawBitmap(DrawBitmap, float,
    /// BitmapInterpolationMode, Rect)"/> does on the main canvas, just into this layer.</summary>
    public void RenderBitmap(int width, int height, DrawBitmap bmp, Matrix3x2 m)
    {
        // Drop our snapshot BEFORE drawing: the surface then owns its pixels uniquely and Skia skips the
        // copy-on-write it would otherwise do to keep the old snapshot intact.
        _snapshot?.Dispose();
        _snapshot = null;
        if (_surface is null || width != Width || height != Height)
        {
            _surface?.Dispose();
            _surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            Width = width; Height = height;
            if (_surface is null) return;
        }
        var c = _surface.Canvas;
        c.ResetMatrix();
        c.Clear(SKColors.Transparent);
        c.SetMatrix(DrawTarget.ToSk(m));
        c.DrawImage(bmp.Image, 0, 0);
        c.ResetMatrix();
        _snapshot = _surface.Snapshot();
    }

    public void Invalidate() { _snapshot?.Dispose(); _snapshot = null; }

    public void Dispose()
    {
        _snapshot?.Dispose(); _snapshot = null;
        _surface?.Dispose(); _surface = null;
    }
}

public sealed class DrawTextFormat : IDisposable
{
    internal SKFont Font { get; }
    private SKFontMetrics? _metrics;
    public DrawTextFormat(SKFont font) => Font = font;
    /// <summary>The font's metrics, read once (the font is never mutated after construction).</summary>
    internal SKFontMetrics Metrics => _metrics ??= Font.Metrics;
    public void Dispose() => Font.Dispose();
}

/// <summary>Skia canvas with the Direct2D-shaped calls OverlayRenderer already uses.</summary>
public sealed class DrawTarget
{
    private SKCanvas _canvas = null!;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    public void Bind(SKCanvas canvas)
    {
        _canvas = canvas;
        _transform = Matrix3x2.Identity;
        ApplyTransform();
    }

    public Matrix3x2 Transform
    {
        get => _transform;
        set { _transform = value; ApplyTransform(); }
    }

    public DrawBrush CreateSolidColorBrush(Color4 color) => new(color);

    public void BeginDraw() { }

    public void EndDraw()
    {
        _canvas?.Flush();
        if (++_frame % BlobSweepEvery == 0) SweepBlobs();
    }

    // ── Text-blob cache ──
    // SKCanvas.DrawText(string, …) shapes the string into a fresh SKTextBlob on EVERY call (glyph lookup +
    // a native blob + a managed SKObject wrapper registered in SkiaSharp's handle table), then draws that
    // blob. The overlay redraws the same few hundred strings (labels, legend rows, prices) every frame, so
    // the blob is cached per (text, font) and drawn directly — the identical draw call, minus the rebuild.
    // Entries unused for a sweep period are disposed; the cache is hard-capped.
    private const int BlobSweepEvery = 240;
    private const int BlobCap = 4096;
    private sealed class BlobEntry { public SKTextBlob? Blob; public SKRect Bounds; public long LastUsed; }
    private readonly Dictionary<(string, SKFont), BlobEntry> _blobs = new();
    private readonly List<(string, SKFont)> _blobSweep = new();
    private long _frame;

    private BlobEntry GetBlob(string text, SKFont font)
    {
        if (!_blobs.TryGetValue((text, font), out var e))
        {
            if (_blobs.Count >= BlobCap) ClearBlobs();
            var blob = font.ContainsGlyphs(text) ? SKTextBlob.Create(text, font) : CreateFallbackBlob(text, font);
            e = new BlobEntry { Blob = blob, Bounds = blob?.Bounds ?? SKRect.Empty };
            _blobs[(text, font)] = e;
        }
        e.LastUsed = _frame;
        return e;
    }

    // ── Glyph fallback: the embedded UI fonts are Latin-only, so symbols like → ≈ ∞ ⚠ would draw as boxes.
    //    Such strings are split into runs, each shaped with the first face that has the glyph (the font's own,
    //    else the system's match for that character). Only strings with a missing glyph pay for this, once. ──
    private static readonly Dictionary<int, SKTypeface?> FallbackFaces = new();
    private static readonly Dictionary<(SKTypeface, float), SKFont> FallbackFonts = new();

    // Shared by every renderer (static), so guarded: the overlay draws on one thread, but headless renderers
    // (tests, previews) can run side by side.
    private static readonly object FallbackGate = new();

    private static SKFont FontFor(int codepoint, SKFont font)
    {
        if (font.Typeface is { } own && own.ContainsGlyph(codepoint)) return font;
        lock (FallbackGate)
        {
            if (!FallbackFaces.TryGetValue(codepoint, out var face))
                FallbackFaces[codepoint] = face = SKFontManager.Default.MatchCharacter(codepoint);
            if (face is null) return font;
            if (!FallbackFonts.TryGetValue((face, font.Size), out var f))
                FallbackFonts[(face, font.Size)] = f = new SKFont(face, font.Size) { Subpixel = font.Subpixel, Edging = font.Edging };
            return f;
        }
    }

    private static IEnumerable<(string Run, SKFont Font)> Runs(string text, SKFont font)
    {
        var sb = new System.Text.StringBuilder();
        SKFont? current = null;
        for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var cp = char.ConvertToUtf32(text, i);
            var f = FontFor(cp, font);
            if (current is not null && !ReferenceEquals(f, current)) { yield return (sb.ToString(), current); sb.Clear(); }
            current = f;
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (current is not null && sb.Length > 0) yield return (sb.ToString(), current);
    }

    private static SKTextBlob? CreateFallbackBlob(string text, SKFont font)
    {
        using var builder = new SKTextBlobBuilder();
        var x = 0f;
        foreach (var (run, f) in Runs(text, font))
        {
            var glyphs = f.GetGlyphs(run);
            builder.AddRun(glyphs, f, new SKPoint(x, 0));
            x += f.MeasureText(run);
        }
        return builder.Build();
    }

    /// <summary>Width of <paramref name="text"/>, honouring glyph fallback (matches what DrawText renders).</summary>
    private static float Measure(string text, SKFont font)
    {
        if (font.ContainsGlyphs(text)) return font.MeasureText(text);
        var w = 0f;
        foreach (var (run, f) in Runs(text, font)) w += f.MeasureText(run);
        return w;
    }

    private void SweepBlobs()
    {
        foreach (var (k, e) in _blobs)
            if (_frame - e.LastUsed > BlobSweepEvery) _blobSweep.Add(k);
        foreach (var k in _blobSweep)
        {
            _blobs[k].Blob?.Dispose();
            _blobs.Remove(k);
        }
        _blobSweep.Clear();
    }

    private void ClearBlobs()
    {
        foreach (var e in _blobs.Values) e.Blob?.Dispose();
        _blobs.Clear();
    }

    /// <summary>Number of cached text blobs (tests/diagnostics).</summary>
    internal int CachedTextBlobs => _blobs.Count;

    /// <summary>Draw <paramref name="text"/> with its baseline origin at (x, y) — same result as
    /// <c>SKCanvas.DrawText(text, x, y, SKTextAlign.Left, font, paint)</c>, via the blob cache.</summary>
    private void DrawTextAt(string text, SKFont font, float x, float y)
    {
        var e = GetBlob(text, font);
        if (e.Blob is { } blob) _canvas.DrawText(blob, x, y, _fill);
    }

    public void Clear(Color4 color) => _canvas.Clear(color.ToSk());

    public int TextAntialiasMode { set { /* Skia paints already antialias. */ } }

    public void FillRectangle(RawRectF r, DrawBrush brush)
    {
        _fill.Color = brush.Color.ToSk();
        _canvas.DrawRect(r.Left, r.Top, r.Width, r.Height, _fill);
    }

    public void DrawRectangle(RawRectF r, DrawBrush brush, float strokeWidth)
    {
        _stroke.Color = brush.Color.ToSk();
        _stroke.StrokeWidth = strokeWidth;
        _canvas.DrawRect(r.Left, r.Top, r.Width, r.Height, _stroke);
    }

    public void FillRoundedRectangle(RawRectF r, float radius, DrawBrush brush)
    {
        _fill.Color = brush.Color.ToSk();
        _fill.Shader = null;
        _canvas.DrawRoundRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), radius, radius, _fill);
    }

    public void DrawRoundedRectangle(RawRectF r, float radius, DrawBrush brush, float strokeWidth)
    {
        _stroke.Color = brush.Color.ToSk();
        _stroke.StrokeWidth = strokeWidth;
        _canvas.DrawRoundRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), radius, radius, _stroke);
    }

    /// <summary>Rounded rect filled with a linear gradient (vertical when <paramref name="horizontal"/> is false).</summary>
    public void FillRoundedRectangleGradient(RawRectF r, float radius, Color4 from, Color4 to, bool horizontal = false)
    {
        var a = new SKPoint(r.Left, r.Top);
        var b = horizontal ? new SKPoint(r.Right, r.Top) : new SKPoint(r.Left, r.Bottom);
        using var shader = SKShader.CreateLinearGradient(a, b, new[] { from.ToSk(), to.ToSk() }, null, SKShaderTileMode.Clamp);
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
        _canvas.DrawRoundRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), radius, radius, _fill);
        _fill.Shader = null;
    }

    /// <summary>Soft drop shadow under a rounded rect (blurred dark fill, offset down).</summary>
    public void DrawShadow(RawRectF r, float radius, float blur, float alpha)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, (byte)Math.Clamp(alpha * 255f, 0f, 255f)) };
        p.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
        _canvas.DrawRoundRect(new SKRect(r.Left, r.Top + blur * 0.6f, r.Right, r.Bottom + blur * 0.6f), radius, radius, p);
    }

    /// <summary>Text with its cap-height centre on <paramref name="cy"/> (uses real font metrics, not a guess).</summary>
    public void DrawTextVCentered(string text, DrawTextFormat tf, float x, float cy, DrawBrush brush)
    {
        if (string.IsNullOrEmpty(text)) return;
        _fill.Color = brush.Color.ToSk();
        _fill.Shader = null;
        var m = tf.Metrics;
        var cap = m.CapHeight > 0 ? m.CapHeight : -m.Ascent * 0.7f;
        DrawTextAt(text, tf.Font, x, cy + cap * 0.5f);
    }

    /// <summary>Distance between successive baselines for <paramref name="tf"/> (descent − ascent + leading).</summary>
    public static float LineHeight(DrawTextFormat tf)
    {
        var m = tf.Metrics;
        return m.Descent - m.Ascent + m.Leading;
    }

    public float MeasureText(string text, DrawTextFormat tf)
        => string.IsNullOrEmpty(text) ? 0f : Measure(text, tf.Font);

    // ── UI kit helpers (menus / panels) ──

    /// <summary>Stroke a circular arc (degrees, 0 = 3 o'clock, clockwise) — ring gauges.</summary>
    public void DrawArc(Vector2 center, float radius, float startDeg, float sweepDeg, Color4 color, float width, bool roundCap = true)
    {
        if (Math.Abs(sweepDeg) < 0.01f) return;
        _stroke.Color = color.ToSk();
        _stroke.StrokeWidth = width;
        _stroke.StrokeCap = roundCap ? SKStrokeCap.Round : SKStrokeCap.Butt;
        _canvas.DrawArc(new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius), startDeg, sweepDeg, false, _stroke);
        _stroke.StrokeCap = SKStrokeCap.Round;
    }

    /// <summary>Stroke a dashed circle (<paramref name="on"/>/<paramref name="off"/> dash lengths in pixels), the dash
    /// pattern starting at <paramref name="phase"/> — rotating it animates the ring.</summary>
    public void DrawDashedCircle(Vector2 center, float radius, Color4 color, float width, float on, float off, float phase = 0f)
    {
        using var dash = SKPathEffect.CreateDash(new[] { on, off }, phase);
        _stroke.Color = color.ToSk();
        _stroke.StrokeWidth = width;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.PathEffect = dash;
        _canvas.DrawCircle(center.X, center.Y, radius, _stroke);
        _stroke.PathEffect = null;
        _stroke.StrokeCap = SKStrokeCap.Round;
    }

    /// <summary>Draw everything until <see cref="PopLayer"/> into an offscreen layer, then composite it at
    /// <paramref name="opacity"/>, blurred by <paramref name="blur"/> (sigma, px) — fade/blur-in of a whole group.
    /// Restores the transform in effect at the push.</summary>
    public void PushLayer(float opacity, float blur = 0f)
    {
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Clamp(opacity * 255f, 0f, 255f)) };
        if (blur > 0.05f) paint.ImageFilter = SKImageFilter.CreateBlur(blur, blur);
        _canvas.SaveLayer(paint);
        _layerTransforms.Push(_transform);
    }

    public void PopLayer()
    {
        _canvas.Restore();
        _transform = _layerTransforms.Pop();
        ApplyTransform();
    }

    private readonly Stack<Matrix3x2> _layerTransforms = new();

    /// <summary>Fill a circle with a radial gradient whose centre sits at <paramref name="focus"/> (lit-sphere orbs):
    /// <paramref name="colors"/> spread evenly from the focus out to <paramref name="gradRadius"/>.</summary>
    public void FillRadialGradient(Vector2 center, float radius, Vector2 focus, float gradRadius, params Color4[] colors)
    {
        var sk = new SKColor[colors.Length];
        for (var i = 0; i < colors.Length; i++) sk[i] = colors[i].ToSk();
        using var shader = SKShader.CreateRadialGradient(new SKPoint(focus.X, focus.Y), gradRadius, sk, null, SKShaderTileMode.Clamp);
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
        _canvas.DrawCircle(center.X, center.Y, radius, _fill);
        _fill.Shader = null;
    }

    /// <summary>Soft glow behind text: <see cref="DrawTextVCentered"/> through a Gaussian blur of <paramref name="sigma"/>.
    /// Draw the crisp text over it afterwards.</summary>
    public void DrawTextGlow(string text, DrawTextFormat tf, float x, float cy, Color4 color, float sigma)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
        _fill.MaskFilter = blur;
        DrawTextVCentered(text, tf, x, cy, new DrawBrush(color));
        _fill.MaskFilter = null;
    }

    /// <summary>Rounded rect filled with an evenly spaced multi-stop linear gradient (ornamental dividers,
    /// active-tab glows, sheen on panels).</summary>
    public void FillGradient(RawRectF r, float radius, bool horizontal, params Color4[] stops)
    {
        if (stops.Length == 0) return;
        var a = new SKPoint(r.Left, r.Top);
        var b = horizontal ? new SKPoint(r.Right, r.Top) : new SKPoint(r.Left, r.Bottom);
        var colors = new SKColor[stops.Length];
        for (var i = 0; i < stops.Length; i++) colors[i] = stops[i].ToSk();
        using var shader = SKShader.CreateLinearGradient(a, b, colors, null, SKShaderTileMode.Clamp);
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
        _canvas.DrawRoundRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), radius, radius, _fill);
        _fill.Shader = null;
    }

    /// <summary>Soft radial glow (color at the centre fading to transparent at <paramref name="radius"/>).</summary>
    public void FillRadialGlow(Vector2 center, float radius, Color4 color)
    {
        using var shader = SKShader.CreateRadialGradient(new SKPoint(center.X, center.Y), radius,
            new[] { color.ToSk(), new SKColor(color.ToSk().Red, color.ToSk().Green, color.ToSk().Blue, 0) }, null, SKShaderTileMode.Clamp);
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
        _canvas.DrawCircle(center.X, center.Y, radius, _fill);
        _fill.Shader = null;
    }

    /// <summary>Stroke a path authored in a 24×24 box, scaled to <paramref name="size"/> with its top-left at
    /// (x, y) — line icons in the Lucide style.</summary>
    public void DrawIconPath(DrawPath path, float x, float y, float size, Color4 color, float strokeWidth = 1.8f)
    {
        var s = size / 24f;
        _canvas.Save();
        _canvas.Translate(x, y);
        _canvas.Scale(s);
        _stroke.Color = color.ToSk();
        _stroke.StrokeWidth = strokeWidth / s;
        _canvas.DrawPath(path.Path, _stroke);
        _canvas.Restore();
    }

    /// <summary>Clip subsequent drawing to a rect until <see cref="PopClip"/> (scroll areas, list viewports).</summary>
    public void PushClip(RawRectF r) { _canvas.Save(); _canvas.ClipRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom)); }

    public void PopClip() => _canvas.Restore();

    /// <summary>Clip to a circle (anti-aliased) until <see cref="PopClip"/> — liquid globes.</summary>
    public void PushClipCircle(Vector2 center, float radius)
    {
        _canvas.Save();
        using var p = new SKPath();
        p.AddCircle(center.X, center.Y, radius);
        _canvas.ClipPath(p, SKClipOperation.Intersect, true);
    }

    public void FillEllipse(Ellipse e, DrawBrush brush)
    {
        _fill.Color = brush.Color.ToSk();
        _canvas.DrawOval(e.Point.X, e.Point.Y, e.RadiusX, e.RadiusY, _fill);
    }

    public void DrawEllipse(Ellipse e, DrawBrush brush, float strokeWidth)
    {
        _stroke.Color = brush.Color.ToSk();
        _stroke.StrokeWidth = strokeWidth;
        _canvas.DrawOval(e.Point.X, e.Point.Y, e.RadiusX, e.RadiusY, _stroke);
    }

    public void DrawLine(Vector2 a, Vector2 b, DrawBrush brush, float strokeWidth)
    {
        _stroke.Color = brush.Color.ToSk();
        _stroke.StrokeWidth = strokeWidth;
        _canvas.DrawLine(a.X, a.Y, b.X, b.Y, _stroke);
    }

    public void DrawText(string text, DrawTextFormat tf, Rect layout, DrawBrush brush, DrawTextOptions options = DrawTextOptions.None)
    {
        if (string.IsNullOrEmpty(text)) return;
        _fill.Color = brush.Color.ToSk();
        // DWrite DrawText origin is the top-left of the layout rect. SKFont.DrawText uses baseline.
        var metrics = tf.Metrics;
        var x = layout.X;
        var y = layout.Y - metrics.Ascent; // Ascent is negative in Skia
        var e = GetBlob(text, tf.Font);
        if (e.Blob is not { } blob) return;
        if (options == DrawTextOptions.Clip)
        {
            // Treat Width/Height as either WH or as Right/Bottom (OverlayRenderer DrawText uses LTRB).
            var w = layout.Width;
            var h = layout.Height;
            if (w > layout.X && h > layout.Y) { w = layout.Width - layout.X; h = layout.Height - layout.Y; }
            if (w > 0 && h > 0)
            {
                var clip = new SKRect(layout.X, layout.Y, layout.X + w, layout.Y + h);
                // The blob's (conservative) glyph bounds at this origin, grown by a pixel for AA coverage. When
                // that already lies inside the clip, the clip can't remove anything → skip the save/clip/restore.
                var b = e.Bounds;
                if (!(b.Left + x - 1f >= clip.Left && b.Right + x + 1f <= clip.Right
                      && b.Top + y - 1f >= clip.Top && b.Bottom + y + 1f <= clip.Bottom))
                {
                    _canvas.Save();
                    _canvas.ClipRect(clip);
                    _canvas.DrawText(blob, x, y, _fill);
                    _canvas.Restore();
                    return;
                }
            }
        }
        _canvas.DrawText(blob, x, y, _fill);
    }

    public void DrawBitmap(DrawBitmap bmp, Rect dest, float opacity, BitmapInterpolationMode interpolation, Rect? source)
    {
        var img = bmp.Image;
        var dst = new SKRect(dest.X, dest.Y, dest.X + dest.Width, dest.Y + dest.Height);
        using var paint = opacity < 0.999f
            ? new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(opacity * 255f), 0, 255)) }
            : null;
        if (source is { } full && full.X == 0 && full.Y == 0 && full.Width == img.Width && full.Height == img.Height
            && dest.X == 0 && dest.Y == 0 && dest.Width == img.Width && dest.Height == img.Height)
        {
            _canvas.DrawImage(img, 0, 0, paint);
        }
        else if (source is { } s2)
        {
            var s = s2;
            var src = new SKRect(s.X, s.Y, s.X + s.Width, s.Y + s.Height);
            _canvas.DrawImage(img, src, dst, paint);
        }
        else
        {
            _canvas.DrawImage(img, dst, paint);
        }
    }

    public void DrawBitmap(DrawBitmap bmp, float opacity, BitmapInterpolationMode interpolation, Rect source)
    {
        // D2D: draw the source rect of the bitmap at the current transform origin, size = source size.
        DrawBitmap(bmp, new Rect(0, 0, source.Width, source.Height), opacity, interpolation, source);
    }

    public void FillGeometry(DrawPath geo, DrawBrush brush)
    {
        _fill.Color = brush.Color.ToSk();
        _canvas.DrawPath(geo.Path, _fill);
    }

    public void DrawGeometry(DrawPath geo, DrawBrush brush, float strokeWidth)
    {
        _stroke.Color = brush.Color.ToSk();
        _stroke.StrokeWidth = strokeWidth;
        _canvas.DrawPath(geo.Path, _stroke);
    }

    public static DrawBitmap CreateBitmap(int width, int height, byte[] bgraPremul)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = bmp.GetPixels();
        MarshalCopy(bgraPremul, pixels, width * height * 4);
        bmp.NotifyPixelsChanged();
        return new DrawBitmap(bmp);
    }

    private static unsafe void MarshalCopy(byte[] src, nint dest, int count)
    {
        if (dest == 0 || count <= 0) return;
        var n = Math.Min(src.Length, count);
        System.Runtime.InteropServices.Marshal.Copy(src, 0, dest, n);
    }

    /// <summary>Blit a <see cref="DrawLayer"/> 1:1 with its top-left at the integer device pixel (x, y),
    /// ignoring the current transform (source-over, like every other draw).</summary>
    public void DrawLayer(DrawLayer layer, int x, int y)
    {
        if (layer.Snapshot is not { } img) return;
        _canvas.ResetMatrix();
        _canvas.DrawImage(img, x, y);
        ApplyTransform();
    }

    internal static SKMatrix ToSk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    private void ApplyTransform()
    {
        if (_canvas is null) return;
        _canvas.SetMatrix(ToSk(_transform));
    }
}
