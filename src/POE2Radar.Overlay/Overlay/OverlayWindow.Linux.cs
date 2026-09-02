using POE2Radar.Core.Native;

namespace POE2Radar.Overlay;

public sealed partial class OverlayWindow
{
    private nint _dpy;
    private nuint _xwin;
    private nint _gc;
    private nint _ximage;
    private nint _visual;
    private int _depth = 32;
    private bool _xClickThrough = true;

    private void InitLinux()
    {
        LinuxX11.EnsureDisplay();
        _dpy = LinuxX11.SharedDisplay;
        if (_dpy == 0)
            throw new InvalidOperationException("XOpenDisplay failed. Overlay needs X11/XWayland (Proton windows are X11).");

        var screen = LinuxX11.XDefaultScreen(_dpy);
        if (LinuxX11.XMatchVisualInfo(_dpy, screen, 32, LinuxX11.TrueColor, out var vinfo) == 0)
            throw new InvalidOperationException("No 32-bit ARGB visual — compositor cannot draw a transparent overlay.");

        _visual = vinfo.Visual;
        _depth = vinfo.Depth;
        var root = LinuxX11.XRootWindow(_dpy, screen);
        var cmap = LinuxX11.XCreateColormap(_dpy, root, _visual, LinuxX11.AllocNone);
        var attrs = new LinuxX11.XSetWindowAttributes
        {
            BackgroundPixel = 0,
            BorderPixel = 0,
            Colormap = cmap,
            OverrideRedirect = 1,
            EventMask = (nint)(LinuxX11.ExposureMask | LinuxX11.ButtonPressMask | LinuxX11.StructureNotifyMask),
        };
        const nuint valuemask = LinuxX11.CWBackPixel | LinuxX11.CWBorderPixel | LinuxX11.CWColormap
                              | LinuxX11.CWOverrideRedirect | LinuxX11.CWEventMask;
        _xwin = LinuxX11.XCreateWindow(_dpy, root, 0, 0, 800, 600, 0, _depth, LinuxX11.InputOutput, _visual, valuemask, ref attrs);
        if (_xwin == 0) throw new InvalidOperationException("XCreateWindow failed");
        LinuxX11.XStoreName(_dpy, _xwin, "POE2RadarOverlay");
        LinuxX11.SetAtoms(_dpy, _xwin);
        _gc = LinuxX11.XCreateGC(_dpy, _xwin, 0, 0);
        LinuxX11.SetClickThrough(_dpy, _xwin, true);
        LinuxX11.XMapRaised(_dpy, _xwin);
        LinuxX11.XFlush(_dpy);
        Console.WriteLine("Overlay: X11 ARGB window (click-through). F9 quits.");
    }

    private void ResizeLinux(int width, int height)
    {
        if (_dpy == 0 || _xwin == 0) return;
        DestroyXImage();
        if (PixelBuffer == 0) return;
        _ximage = LinuxX11.XCreateImage(_dpy, _visual, (uint)_depth, LinuxX11.ZPixmap, 0, PixelBuffer,
            (uint)width, (uint)height, 32, PixelRowBytes);
        LinuxX11.XMoveResizeWindow(_dpy, _xwin, OriginX, OriginY, (uint)width, (uint)height);
        LinuxX11.XFlush(_dpy);
    }

    private void DestroyXImage()
    {
        if (_ximage == 0) return;
        // XDestroyImage frees XImage.data — that's our Skia buffer. Null it first.
        unsafe
        {
            // XImage.data is at offset: width+height+xoffset+format = 4*4 = 16, then data pointer.
            // Safer: read XImage via a small helper struct.
            var img = MarshalXImage(_ximage);
            img.Data = 0;
            WriteXImageData(_ximage, 0);
        }
        LinuxX11.XDestroyImage(_ximage);
        _ximage = 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XImageHead
    {
        public int Width, Height, XOffset, Format;
        public nint Data;
    }

    private static XImageHead MarshalXImage(nint image)
        => System.Runtime.InteropServices.Marshal.PtrToStructure<XImageHead>(image);

    private static void WriteXImageData(nint image, nint data)
        => System.Runtime.InteropServices.Marshal.WriteIntPtr(image, 4 * sizeof(int), data);

    private void PresentLinux()
    {
        if (_dpy == 0 || _xwin == 0 || _ximage == 0) return;
        LinuxX11.XPutImage(_dpy, _xwin, _gc, _ximage, 0, 0, 0, 0, (uint)Width, (uint)Height);
        LinuxX11.XRaiseWindow(_dpy, _xwin);
        LinuxX11.XFlush(_dpy);
    }

    private void MoveLinux(int x, int y, int w, int h)
    {
        if (_dpy == 0 || _xwin == 0) return;
        LinuxX11.XMoveResizeWindow(_dpy, _xwin, x, y, (uint)Math.Max(w, 1), (uint)Math.Max(h, 1));
    }

    private void SetClickThroughLinux(bool value)
    {
        if (_dpy == 0 || _xwin == 0) return;
        _xClickThrough = value;
        LinuxX11.SetClickThrough(_dpy, _xwin, value);
    }

    private bool PumpLinux()
    {
        if (_dpy == 0) return true;
        try
        {
            while (LinuxX11.XPending(_dpy) > 0)
            {
                LinuxX11.XNextEvent(_dpy, out var ev);
                if (ev.Type == LinuxX11.ButtonPress && !_xClickThrough)
                    OnClientClick?.Invoke(ev.X, ev.Y);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"X11 event pump: {ex.Message}");
        }
        return true;
    }

    private void DisposeLinux()
    {
        DestroyXImage();
        if (_gc != 0 && _dpy != 0) { LinuxX11.XFreeGC(_dpy, _gc); _gc = 0; }
        if (_xwin != 0 && _dpy != 0) { LinuxX11.XDestroyWindow(_dpy, _xwin); _xwin = 0; }
        LinuxX11.XFlush(_dpy);
    }
}
