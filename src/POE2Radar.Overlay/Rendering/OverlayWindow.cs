using POE2Radar.Overlay.Draw;
using SkiaSharp;

namespace POE2Radar.Overlay;

/// <summary>
/// Transparent, click-through, always-on-top overlay. Skia renders into a premultiplied-BGRA
/// buffer; a platform backend presents it (Win32 UpdateLayeredWindow, or an X11 ARGB window).
/// </summary>
public sealed partial class OverlayWindow : IDisposable
{
    public static readonly Color4 ChromaKey = new(0f, 0f, 0f, 0f);

    private SKSurface? _surface;
    private nint _pixels;
    private int _rowBytes;
    private DrawTarget _target = new();
    private bool _clickThrough = true;
    private bool _disposed;
    private bool _headless;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public bool IsValid => !_disposed && _surface is not null
        && (_headless || (OperatingSystem.IsLinux() ? _xwin != 0 : _hwnd != 0));
    public nint Handle => OperatingSystem.IsLinux() ? (nint)_xwin : _hwnd;
    public DrawTarget RenderTarget => _target;

    /// <summary>
    /// Raised on a left click in overlay client coordinates, only while click-through is off.
    /// </summary>
    public Action<int, int>? OnClientClick;

    /// <summary>Surface-only window (no platform window) for headless rendering — previews/tests.</summary>
    internal static OverlayWindow CreateHeadless(int width, int height)
    {
        var ow = new OverlayWindow { _headless = true };
        ow.AllocateSurface(width, height);
        return ow;
    }

    /// <summary>Encode the current surface as PNG (headless preview / tests).</summary>
    internal byte[] SnapshotPng()
    {
        _surface!.Flush();
        using var img = _surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static OverlayWindow Create()
    {
        var ow = new OverlayWindow();
        if (OperatingSystem.IsWindows()) ow.InitWindows();
        else if (OperatingSystem.IsLinux()) ow.InitLinux();
        else throw new PlatformNotSupportedException("POE2Radar overlay supports Windows and Linux.");
        ow.AllocateSurface(800, 600);
        return ow;
    }

    /// <summary>Embedded Manrope at a given weight — the UI face for every panel and menu.</summary>
    internal DrawTextFormat CreateSansTextFormat(float size, UiFonts.Weight weight = UiFonts.Weight.Regular)
        => UiFonts.Sans(weight) is { } tf
            ? new DrawTextFormat(new SKFont(tf, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias })
            : CreateUiTextFormat(size, bold: weight >= UiFonts.Weight.SemiBold, serif: false, embedded: false);

    /// <summary>Embedded Cinzel display serif (titles, big numbers).</summary>
    internal DrawTextFormat CreateDisplayTextFormat(float size, UiFonts.Weight weight = UiFonts.Weight.SemiBold)
        => UiFonts.Display(weight) is { } tf
            ? new DrawTextFormat(new SKFont(tf, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias })
            : CreateUiTextFormat(size, weight >= UiFonts.Weight.Bold, serif: true, embedded: false);

    /// <summary>Embedded Noto Sans Runic (Elder Futhark ornaments); falls back to the sans face without it.</summary>
    internal DrawTextFormat CreateRunicTextFormat(float size)
        => UiFonts.Runic is { } tf
            ? new DrawTextFormat(new SKFont(tf, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias })
            : CreateSansTextFormat(size);

    /// <summary>Proportional UI font (bold optional). Uses the embedded Manrope / Cinzel faces when available, else
    /// cross-platform system fallbacks.</summary>
    public DrawTextFormat CreateUiTextFormat(float size, bool bold = false, bool serif = false, bool embedded = true)
    {
        if (embedded)
        {
            var face = serif ? UiFonts.Display(bold ? UiFonts.Weight.Bold : UiFonts.Weight.SemiBold) : UiFonts.Sans(bold ? UiFonts.Weight.SemiBold : UiFonts.Weight.Regular);
            if (face is not null) return new DrawTextFormat(new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias });
        }
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        SKTypeface? tf = null;
        var families = serif
            ? new[] { "Palatino Linotype", "Georgia", "Cambria", "DejaVu Serif", "Liberation Serif", "Noto Serif", "serif" }
            : new[] { "Segoe UI", "Inter", "Roboto", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Cantarell", "sans-serif" };
        foreach (var fam in families)
        {
            tf = SKTypeface.FromFamilyName(fam, style);
            if (tf is not null && !string.Equals(tf.FamilyName, SKTypeface.Default.FamilyName, StringComparison.OrdinalIgnoreCase)) break;
        }
        tf ??= SKTypeface.Default;
        return new DrawTextFormat(new SKFont(tf, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias });
    }

    public DrawTextFormat CreateTextFormat(string family, float size)
    {
        var typeface = SKTypeface.FromFamilyName(family)
                    ?? SKTypeface.FromFamilyName("DejaVu Sans Mono")
                    ?? SKTypeface.FromFamilyName("Liberation Mono")
                    ?? SKTypeface.FromFamilyName("Noto Sans Mono")
                    ?? SKTypeface.FromFamilyName("monospace")
                    ?? SKTypeface.Default;
        return new DrawTextFormat(new SKFont(typeface, size));
    }

    public void Present()
    {
        _surface?.Flush();
        if (OperatingSystem.IsLinux()) PresentLinux();
        else PresentWindows();
    }

    public bool TrackGameWindow(nint gameHwnd)
    {
        if (gameHwnd == 0) return false;
        if (!POE2Radar.Core.Native.GameHost.TryGetWindowRect(gameHwnd, out var rect)) return false;
        var w = rect.Width;
        var h = rect.Height;
        if (w <= 0 || h <= 0) return false;

        if (rect.Left != OriginX || rect.Top != OriginY || w != Width || h != Height)
        {
            if (w != Width || h != Height) AllocateSurface(w, h);
            OriginX = rect.Left;
            OriginY = rect.Top;
            if (OperatingSystem.IsLinux()) MoveLinux(OriginX, OriginY, Width, Height);
        }
        return true;
    }

    public void SetClickThrough(bool value)
    {
        if (value == _clickThrough) return;
        _clickThrough = value;
        if (OperatingSystem.IsLinux()) SetClickThroughLinux(value);
        else SetClickThroughWindows(value);
    }

    public bool PumpMessages() => OperatingSystem.IsLinux() ? PumpLinux() : PumpWindows();

    /// <summary>Exclusive pointer capture while a modal overlay menu is open: clicks land on the overlay and
    /// NOT on the game underneath. No-op on Windows (the non-transparent layered window already owns them).
    /// Returns whether the capture is held.</summary>
    public bool CapturePointer(bool value)
    {
        if (!OperatingSystem.IsLinux()) return value;
        return CapturePointerLinux(value);
    }

    private unsafe void AllocateSurface(int width, int height)
    {
        if (OperatingSystem.IsLinux()) { DestroyXImage(); DestroyShmLinux(); }
        FreeSurface();   // before ResizeWindows: the old surface may point into the DIB it frees
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        _rowBytes = width * 4;
        Width = width;
        Height = height;

        // Windows: create the layered-window DIB first and let Skia render STRAIGHT into it (a top-down
        // 32bpp DIB is exactly a premultiplied-BGRA buffer with stride width*4), so Present no longer copies
        // the whole frame (8 MB at 1080p, 33 MB at 4K) into it every frame. Falls back to an owned buffer +
        // per-frame copy if the DIB can't be created (and for headless surfaces).
        if (!OperatingSystem.IsLinux()) ResizeWindows(width, height);
        if (!OperatingSystem.IsLinux() && !_headless && _dibBits != 0)
        {
            _pixels = _dibBits;
            _ownsPixels = false;
        }
        else if (OperatingSystem.IsLinux() && !_headless && TryCreateShmLinux(width, height))
        {
            // Linux: likewise render straight into an MIT-SHM segment the X server reads (no socket copy).
            _pixels = _shm.Pixels;
            _rowBytes = _shm.RowBytes;
            _ownsPixels = false;
        }
        else
        {
            _pixels = (nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(_rowBytes * height));
            _ownsPixels = true;
        }
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _pixels, _rowBytes);
        _target.Bind(_surface.Canvas);
        if (OperatingSystem.IsLinux()) ResizeLinux(width, height);
    }

    private bool _ownsPixels;

    private unsafe void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_pixels != 0 && _ownsPixels)
            System.Runtime.InteropServices.NativeMemory.Free((void*)_pixels);
        _pixels = 0;
        _ownsPixels = false;
    }

    internal nint PixelBuffer => _pixels;
    internal int PixelRowBytes => _rowBytes;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FreeSurface();   // first: the surface may be backed by the DIB / shm segment the platform dispose frees
        if (OperatingSystem.IsLinux()) DisposeLinux();
        else DisposeWindows();
    }
}
