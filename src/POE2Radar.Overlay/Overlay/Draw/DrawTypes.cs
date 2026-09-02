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
    public DrawBitmap(SKBitmap bitmap) => Bitmap = bitmap;
    public void Dispose() => Bitmap.Dispose();
}

public sealed class DrawTextFormat : IDisposable
{
    internal SKFont Font { get; }
    public DrawTextFormat(SKFont font) => Font = font;
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
    public void EndDraw() => _canvas?.Flush();

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
        var metrics = tf.Font.Metrics;
        var x = layout.X;
        var y = layout.Y - metrics.Ascent; // Ascent is negative in Skia
        if (options == DrawTextOptions.Clip)
        {
            _canvas.Save();
            // Treat Width/Height as either WH or as Right/Bottom (OverlayRenderer DrawText uses LTRB).
            var w = layout.Width;
            var h = layout.Height;
            if (w > layout.X && h > layout.Y) { w = layout.Width - layout.X; h = layout.Height - layout.Y; }
            if (w > 0 && h > 0) _canvas.ClipRect(new SKRect(layout.X, layout.Y, layout.X + w, layout.Y + h));
            _canvas.DrawText(text, x, y, SKTextAlign.Left, tf.Font, _fill);
            _canvas.Restore();
        }
        else
        {
            _canvas.DrawText(text, x, y, SKTextAlign.Left, tf.Font, _fill);
        }
    }

    public void DrawBitmap(DrawBitmap bmp, Rect dest, float opacity, BitmapInterpolationMode interpolation, Rect? source)
    {
        using var img = SKImage.FromBitmap(bmp.Bitmap);
        var dst = new SKRect(dest.X, dest.Y, dest.X + dest.Width, dest.Y + dest.Height);
        using var paint = opacity < 0.999f
            ? new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(opacity * 255f), 0, 255)) }
            : null;
        if (source is { } s)
        {
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

    private void ApplyTransform()
    {
        if (_canvas is null) return;
        var m = _transform;
        var sk = new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);
        _canvas.SetMatrix(sk);
    }
}
