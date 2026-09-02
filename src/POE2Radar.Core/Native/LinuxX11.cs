using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Native;

/// <summary>X11 / XTest / XFixes P/Invoke used by <see cref="GameHost"/> and the Linux overlay window.</summary>
public static partial class LinuxX11
{
    private const string X11 = "libX11.so.6";
    private const string Xtst = "libXtst.so.6";
    private const string Xfixes = "libXfixes.so.3";
    private const string Xext = "libXext.so.6";

    public const int TrueColor = 4;
    public const int ZPixmap = 2;
    public const int AllocNone = 0;
    public const int InputOutput = 1;
    public const long ExposureMask = 0x00008000;
    public const long ButtonPressMask = 0x00000004;
    public const long StructureNotifyMask = 0x00020000;
    public const int ButtonPress = 4;
    public const int ClientMessage = 33;
    public const int CWBackPixel = 1 << 1;
    public const int CWBorderPixel = 1 << 3;
    public const int CWOverrideRedirect = 1 << 9;
    public const int CWEventMask = 1 << 11;
    public const int CWColormap = 1 << 13;
    public const int ShapeInput = 2;
    public const int PropModeReplace = 0;
    public const nint XA_ATOM = 4;
    public const nint XA_WINDOW = 33;
    public const nint XA_CARDINAL = 6;
    public const nint XA_STRING = 31;
    public const int XK_F1 = 0xFFBE;
    public const int CurrentTime = 0;
    public const int KeyPress = 2;
    public const int ReplayPointer = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct XVisualInfo
    {
        public nint Visual;
        public nuint VisualId;
        public int Screen;
        public int Depth;
        public int Class;
        public nuint RedMask, GreenMask, BlueMask;
        public int ColormapSize;
        public int BitsPerRgb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public nuint BackgroundPixmap;
        public nuint BackgroundPixel;
        public nuint BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint EventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap;
        public nuint Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    // XEvent is a 24-long union (192 bytes on x86_64). Undersizing this makes XNextEvent smash the stack.
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XEvent
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint Root;
        public nuint Subwindow;
        public nuint Time;
        public int X, Y, XRoot, YRoot;
        public uint State, Button;
        public int SameScreen;
    }

    [LibraryImport(X11, EntryPoint = "XInitThreads")]
    public static partial int XInitThreads();

    [LibraryImport(X11, EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XOpenDisplay(string? name);

    [LibraryImport(X11, EntryPoint = "XCloseDisplay")]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(X11, EntryPoint = "XDefaultScreen")]
    public static partial int XDefaultScreen(nint display);

    [LibraryImport(X11, EntryPoint = "XRootWindow")]
    public static partial nuint XRootWindow(nint display, int screen);

    [LibraryImport(X11, EntryPoint = "XDefaultRootWindow")]
    public static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(X11, EntryPoint = "XMatchVisualInfo")]
    public static partial int XMatchVisualInfo(nint display, int screen, int depth, int cls, out XVisualInfo vinfo);

    [LibraryImport(X11, EntryPoint = "XCreateColormap")]
    public static partial nuint XCreateColormap(nint display, nuint w, nint visual, int alloc);

    [LibraryImport(X11, EntryPoint = "XCreateWindow")]
    public static partial nuint XCreateWindow(
        nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint cls, nint visual, nuint valuemask, ref XSetWindowAttributes attrs);

    [LibraryImport(X11, EntryPoint = "XDestroyWindow")]
    public static partial int XDestroyWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XMapRaised")]
    public static partial int XMapRaised(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XUnmapWindow")]
    public static partial int XUnmapWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XMoveResizeWindow")]
    public static partial int XMoveResizeWindow(nint display, nuint w, int x, int y, uint width, uint height);

    [LibraryImport(X11, EntryPoint = "XRaiseWindow")]
    public static partial int XRaiseWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XClearWindow")]
    public static partial int XClearWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XFlush")]
    public static partial int XFlush(nint display);

    [LibraryImport(X11, EntryPoint = "XSync")]
    public static partial int XSync(nint display, int discard);

    [LibraryImport(X11, EntryPoint = "XPending")]
    public static partial int XPending(nint display);

    [LibraryImport(X11, EntryPoint = "XNextEvent")]
    public static partial int XNextEvent(nint display, out XEvent evt);

    [LibraryImport(X11, EntryPoint = "XSelectInput")]
    public static partial int XSelectInput(nint display, nuint w, nint mask);

    [LibraryImport(X11, EntryPoint = "XCreateGC")]
    public static partial nint XCreateGC(nint display, nuint d, nuint valuemask, nint values);

    [LibraryImport(X11, EntryPoint = "XFreeGC")]
    public static partial int XFreeGC(nint display, nint gc);

    [LibraryImport(X11, EntryPoint = "XPutImage")]
    public static partial int XPutImage(nint display, nuint d, nint gc, nint image, int srcX, int srcY, int dstX, int dstY, uint w, uint h);

    [LibraryImport(X11, EntryPoint = "XCreateImage")]
    public static partial nint XCreateImage(nint display, nint visual, uint depth, int format, int offset, nint data, uint width, uint height, int bitmapPad, int bytesPerLine);

    [LibraryImport(X11, EntryPoint = "XDestroyImage")]
    public static partial int XDestroyImage(nint image);

    [LibraryImport(X11, EntryPoint = "XStoreName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int XStoreName(nint display, nuint w, string name);

    [LibraryImport(X11, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(X11, EntryPoint = "XChangeProperty")]
    public static partial int XChangeProperty(nint display, nuint w, nint property, nint type, int format, int mode, nint data, int nElements);

    [LibraryImport(X11, EntryPoint = "XGetWindowProperty")]
    public static partial int XGetWindowProperty(
        nint display, nuint w, nint property, nint offset, nint length, int delete, nint reqType,
        out nint actualType, out int actualFormat, out nuint nItems, out nuint bytesAfter, out nint prop);

    [LibraryImport(X11, EntryPoint = "XFree")]
    public static partial int XFree(nint data);

    [LibraryImport(X11, EntryPoint = "XGetWindowAttributes")]
    public static partial int XGetWindowAttributes(nint display, nuint w, out XWindowAttributes attrs);

    [LibraryImport(X11, EntryPoint = "XTranslateCoordinates")]
    public static partial int XTranslateCoordinates(nint display, nuint src, nuint dest, int srcX, int srcY, out int destX, out int destY, out nuint child);

    [LibraryImport(X11, EntryPoint = "XQueryPointer")]
    public static partial int XQueryPointer(
        nint display, nuint w, out nuint root, out nuint child, out int rootX, out int rootY,
        out int winX, out int winY, out uint mask);

    [LibraryImport(X11, EntryPoint = "XQueryKeymap")]
    public static unsafe partial int XQueryKeymap(nint display, byte* keys);

    [LibraryImport(X11, EntryPoint = "XKeysymToKeycode")]
    public static partial byte XKeysymToKeycode(nint display, nuint keysym);

    [LibraryImport(X11, EntryPoint = "XStringToKeysym", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XStringToKeysym(string s);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeKeyEvent")]
    public static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeButtonEvent")]
    public static partial int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);

    [LibraryImport(X11, EntryPoint = "XWarpPointer")]
    public static partial int XWarpPointer(nint display, nuint srcW, nuint destW,
        int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);

    [LibraryImport(Xfixes, EntryPoint = "XFixesCreateRegion")]
    public static partial nint XFixesCreateRegion(nint display, nint rectangles, int nRectangles);

    [LibraryImport(Xfixes, EntryPoint = "XFixesDestroyRegion")]
    public static partial int XFixesDestroyRegion(nint display, nint region);

    [LibraryImport(Xfixes, EntryPoint = "XFixesSetWindowShapeRegion")]
    public static partial int XFixesSetWindowShapeRegion(nint display, nuint window, int shapeKind, int xOff, int yOff, nint region);

    [LibraryImport(X11, EntryPoint = "XGetInputFocus")]
    public static partial int XGetInputFocus(nint display, out nuint focus, out int revert);

    private static readonly object Gate = new();
    private static nint _display;
    private static bool _initTried;

    public static nint SharedDisplay
    {
        get
        {
            EnsureDisplay();
            return _display;
        }
    }

    public static void EnsureDisplay()
    {
        lock (Gate)
        {
            if (_display != 0) return;
            if (_initTried && _display == 0) return;
            _initTried = true;
            try { XInitThreads(); } catch { /* optional */ }
            _display = XOpenDisplay(null);
        }
    }

    public static nint FindWindowForProcess(int processId)
    {
        EnsureDisplay();
        if (_display == 0) return 0;
        var root = XDefaultRootWindow(_display);
        nuint best = 0;
        long bestArea = 0;

        foreach (var w in ClientList(root))
        {
            if (WindowPid(w) != processId && !TitleLooksLikePoe(w)) continue;
            if (WindowPid(w) != processId && WindowPid(w) != 0) continue;
            if (!TryGetWindowRect(unchecked((nint)w), out var r)) continue;
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { bestArea = area; best = w; }
        }

        if (best == 0)
        {
            // Title-only fallback (some Wine builds skip _NET_WM_PID).
            foreach (var w in ClientList(root))
            {
                if (!TitleLooksLikePoe(w)) continue;
                if (!TryGetWindowRect(unchecked((nint)w), out var r)) continue;
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; best = w; }
            }
        }
        return unchecked((nint)best);
    }

    public static bool TryGetWindowRect(nint hwnd, out GameHost.Rect rect)
    {
        rect = default;
        EnsureDisplay();
        if (_display == 0 || hwnd == 0) return false;
        var w = unchecked((nuint)hwnd);
        if (XGetWindowAttributes(_display, w, out var attrs) == 0) return false;
        var root = XDefaultRootWindow(_display);
        XTranslateCoordinates(_display, w, root, 0, 0, out var x, out var y, out _);
        rect = new GameHost.Rect { Left = x, Top = y, Right = x + attrs.Width, Bottom = y + attrs.Height };
        return attrs.Width > 0 && attrs.Height > 0;
    }

    public static nint GetForegroundWindow()
    {
        EnsureDisplay();
        if (_display == 0) return 0;
        var atom = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 1);
        if (atom == 0)
        {
            XGetInputFocus(_display, out var focus, out _);
            return unchecked((nint)focus);
        }
        if (!TryGetProperty(XDefaultRootWindow(_display), atom, XA_WINDOW, out var data, out var nItems) || nItems == 0 || data == 0)
            return 0;
        try
        {
            var w = Marshal.ReadIntPtr(data);
            return w;
        }
        finally { XFree(data); }
    }

    public static bool GetCursorPos(out GameHost.Point pt)
    {
        pt = default;
        EnsureDisplay();
        if (_display == 0) return false;
        var root = XDefaultRootWindow(_display);
        if (XQueryPointer(_display, root, out _, out _, out var rx, out var ry, out _, out _, out _) == 0)
            return false;
        pt = new GameHost.Point { X = rx, Y = ry };
        return true;
    }

    public static bool ScreenToClient(nint hwnd, ref GameHost.Point pt)
    {
        if (!TryGetWindowRect(hwnd, out var r)) return false;
        pt.X -= r.Left;
        pt.Y -= r.Top;
        return true;
    }

    public static unsafe short GetAsyncKeyState(int vKey)
    {
        EnsureDisplay();
        if (_display == 0) return 0;
        var keysym = VkToKeysym(vKey);
        if (keysym == 0) return 0;
        var code = XKeysymToKeycode(_display, keysym);
        if (code == 0) return 0;
        byte* map = stackalloc byte[32];
        XQueryKeymap(_display, map);
        var down = (map[code / 8] & (1 << (code % 8))) != 0;
        return down ? unchecked((short)0x8000) : (short)0;
    }

    public static void TapKey(ushort vk)
    {
        EnsureDisplay();
        if (_display == 0) return;
        if (TryMouseButton(vk, out var button))
        {
            XTestFakeButtonEvent(_display, button, 1, 0);
            XTestFakeButtonEvent(_display, button, 0, 0);
            XFlush(_display);
            return;
        }
        var keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var code = XKeysymToKeycode(_display, keysym);
        if (code == 0) return;
        XTestFakeKeyEvent(_display, code, 1, 0);
        XTestFakeKeyEvent(_display, code, 0, 0);
        XFlush(_display);
    }

    public static void SetCursorPos(int x, int y)
    {
        EnsureDisplay();
        if (_display == 0) return;
        var root = XDefaultRootWindow(_display);
        XWarpPointer(_display, 0, root, 0, 0, 0, 0, x, y);
        XFlush(_display);
    }

    private static bool TryMouseButton(ushort vk, out uint button)
    {
        button = vk switch
        {
            0x01 => 1u, // VK_LBUTTON
            0x02 => 3u, // VK_RBUTTON
            0x04 => 2u, // VK_MBUTTON
            0x05 => 8u, // VK_XBUTTON1
            0x06 => 9u, // VK_XBUTTON2
            _ => 0u,
        };
        return button != 0;
    }

    public static int ScreenWidth()
    {
        EnsureDisplay();
        if (_display == 0) return 0;
        var root = XDefaultRootWindow(_display);
        if (XGetWindowAttributes(_display, root, out var attrs) == 0) return 0;
        return attrs.Width;
    }

    public static int ScreenHeight()
    {
        EnsureDisplay();
        if (_display == 0) return 0;
        var root = XDefaultRootWindow(_display);
        if (XGetWindowAttributes(_display, root, out var attrs) == 0) return 0;
        return attrs.Height;
    }

    public static int DetectMonitorHz()
    {
        // Best-effort: parse the first current mode from `xrandr` if present.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("xrandr", "--current")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(500);
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains('*')) continue;
                var star = line.IndexOf('*');
                var i = star - 1;
                while (i >= 0 && (char.IsDigit(line[i]) || line[i] == '.')) i--;
                var token = line[(i + 1)..star].Trim();
                if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var hz)
                    && hz >= 30)
                    return (int)Math.Round(hz);
            }
        }
        catch { /* optional */ }
        return 0;
    }

    public static void SetClickThrough(nint display, nuint window, bool clickThrough)
    {
        if (display == 0 || window == 0) return;
        try
        {
            if (clickThrough)
            {
                var empty = XFixesCreateRegion(display, 0, 0);
                XFixesSetWindowShapeRegion(display, window, ShapeInput, 0, 0, empty);
                XFixesDestroyRegion(display, empty);
            }
            else
            {
                XFixesSetWindowShapeRegion(display, window, ShapeInput, 0, 0, 0);
            }
            XFlush(display);
        }
        catch (DllNotFoundException)
        {
            // XFixes missing — overlay stays click-through via override-redirect only.
        }
    }

    public static unsafe void SetAtoms(nint display, nuint window)
    {
        var type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", 0);
        var tooltip = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", 0);
        var t = tooltip;
        XChangeProperty(display, window, type, XA_ATOM, 32, PropModeReplace, (nint)(&t), 1);

        var state = XInternAtom(display, "_NET_WM_STATE", 0);
        var above = XInternAtom(display, "_NET_WM_STATE_ABOVE", 0);
        var skipTask = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", 0);
        var skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", 0);
        var states = stackalloc nint[3];
        states[0] = above; states[1] = skipTask; states[2] = skipPager;
        XChangeProperty(display, window, state, XA_ATOM, 32, PropModeReplace, (nint)states, 3);
    }

    private static IEnumerable<nuint> ClientList(nuint root)
    {
        var atom = XInternAtom(_display, "_NET_CLIENT_LIST", 1);
        if (atom == 0 || !TryGetProperty(root, atom, XA_WINDOW, out var data, out var n) || data == 0)
            yield break;
        try
        {
            for (nuint i = 0; i < n; i++)
                yield return unchecked((nuint)(nint)Marshal.ReadIntPtr(data, (int)i * nint.Size));
        }
        finally { XFree(data); }
    }

    private static int WindowPid(nuint w)
    {
        var atom = XInternAtom(_display, "_NET_WM_PID", 1);
        if (atom == 0 || !TryGetProperty(w, atom, XA_CARDINAL, out var data, out var n) || n == 0 || data == 0)
            return 0;
        try { return Marshal.ReadInt32(data); }
        finally { XFree(data); }
    }

    private static bool TitleLooksLikePoe(nuint w)
    {
        var title = WindowTitle(w);
        return title.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase);
    }

    private static string WindowTitle(nuint w)
    {
        var net = XInternAtom(_display, "_NET_WM_NAME", 1);
        var utf8 = XInternAtom(_display, "UTF8_STRING", 1);
        if (net != 0 && TryGetProperty(w, net, utf8, out var data, out var n) && data != 0 && n > 0)
        {
            try { return Marshal.PtrToStringUTF8(data, (int)n) ?? ""; }
            finally { XFree(data); }
        }
        if (TryGetProperty(w, XA_STRING == 0 ? XInternAtom(_display, "WM_NAME", 1) : 39 /* XA_WM_NAME */, 0, out data, out n)
            && data != 0 && n > 0)
        {
            try { return Marshal.PtrToStringAnsi(data) ?? ""; }
            finally { XFree(data); }
        }
        return "";
    }

    private static bool TryGetProperty(nuint w, nint atom, nint type, out nint data, out nuint nItems)
    {
        data = 0; nItems = 0;
        if (atom == 0) return false;
        var req = type == 0 ? 0 : type;
        var rc = XGetWindowProperty(_display, w, atom, 0, 4096, 0, req,
            out _, out _, out nItems, out _, out data);
        return rc == 0 && data != 0;
    }

    private static nuint VkToKeysym(int vk)
    {
        // F1..F12 = 0x70..0x7B
        if (vk is >= 0x70 and <= 0x7B) return (nuint)(XK_F1 + (vk - 0x70));
        if (vk is >= 0x30 and <= 0x39) return (nuint)vk; // ASCII digits
        if (vk is >= 0x41 and <= 0x5A) return (nuint)(vk + 0x20); // XK lowercase
        if (vk == 0x20) return 0x20;
        if (vk == 0x1B) return 0xFF1B;
        if (vk == 0x09) return 0xFF09;
        if (vk == 0x0D) return 0xFF0D;
        return 0;
    }
}
