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
    private bool _xGrabbed;
    private LinuxX11.ShmImage _shm;          // MIT-SHM present image (Image == 0 → plain XPutImage path)
    private bool _shmLogged;

    private bool TryCreateShmLinux(int width, int height)
    {
        if (_dpy == 0 || _xwin == 0) return false;
        var ok = LinuxX11.TryCreateShmImage(_dpy, _visual, _depth, width, height, out _shm);
        if (!_shmLogged) { _shmLogged = true; Console.WriteLine(ok ? "Overlay: MIT-SHM present." : "Overlay: MIT-SHM unavailable, using XPutImage."); }
        return ok;
    }

    private void DestroyShmLinux()
    {
        if (_shm.Image == 0) return;
        LinuxX11.DestroyShmImage(_dpy, _shm);
        _shm = default;
    }

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
        // Never take keyboard focus: the overlay floats over the game, and if a click on it (the Insert menu)
        // made it the focused X client, the game would lose focus and our key taps would land on the overlay.
        var hints = new LinuxX11.XWMHints { Flags = (nint)LinuxX11.InputHint, Input = 0 };
        LinuxX11.XSetWMHints(_dpy, _xwin, ref hints);
        LinuxX11.XMapRaised(_dpy, _xwin);
        LinuxX11.XFlush(_dpy);
        Console.WriteLine("Overlay: X11 ARGB window (click-through). F9 quits.");
    }

    private void ResizeLinux(int width, int height)
    {
        if (_dpy == 0 || _xwin == 0) return;
        DestroyXImage();
        if (PixelBuffer == 0) return;
        if (_shm.Image != 0)
        {
            LinuxX11.XMoveResizeWindow(_dpy, _xwin, OriginX, OriginY, (uint)width, (uint)height);
            LinuxX11.XFlush(_dpy);
            return;
        }
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
        if (_dpy != 0 && _xwin != 0 && _shm.Image != 0)
        {
            LinuxX11.PutShmImage(_dpy, _xwin, _gc, _shm, Width, Height);
            LinuxX11.XRaiseWindow(_dpy, _xwin);
            // Round-trip: returns once the server has processed the put (copied the shared pixels), so the next
            // frame can be drawn into the segment without tearing this one.
            LinuxX11.XSync(_dpy, 0);
            return;
        }
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

    private bool CapturePointerLinux(bool value)
    {
        if (_dpy == 0 || _xwin == 0) return false;
        if (value)
        {
            if (_xGrabbed) return true;
            _xGrabbed = LinuxX11.GrabPointer(_dpy, _xwin);
            return _xGrabbed;
        }
        if (_xGrabbed) { LinuxX11.UngrabPointer(_dpy); _xGrabbed = false; }
        return false;
    }

    private bool PumpLinux()
    {
        if (_dpy == 0) return true;
        try
        {
            while (LinuxX11.XPending(_dpy) > 0)
            {
                LinuxX11.XNextEvent(_dpy, out var ev);
                if (ev.Type == LinuxX11.ButtonPress && (!_xClickThrough || _xGrabbed) && ev.Button == 1)
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
        if (_xGrabbed && _dpy != 0) { LinuxX11.UngrabPointer(_dpy); _xGrabbed = false; }
        DestroyXImage();
        DestroyShmLinux();
        if (_gc != 0 && _dpy != 0) { LinuxX11.XFreeGC(_dpy, _gc); _gc = 0; }
        if (_xwin != 0 && _dpy != 0) { LinuxX11.XDestroyWindow(_dpy, _xwin); _xwin = 0; }
        if (_dpy != 0) LinuxX11.XFlush(_dpy);
    }
}
