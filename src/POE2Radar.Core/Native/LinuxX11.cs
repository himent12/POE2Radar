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

    [LibraryImport(X11, EntryPoint = "XQueryTree")]
    public static partial int XQueryTree(nint display, nuint w, out nuint root, out nuint parent, out nint children, out uint nChildren);

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

    [LibraryImport(X11, EntryPoint = "XSendEvent")]
    public static partial int XSendEvent(nint display, nuint window, int propagate, nint eventMask, ref XEvent evt);

    [LibraryImport(X11, EntryPoint = "XSetInputFocus")]
    public static partial int XSetInputFocus(nint display, nuint focus, int revertTo, nuint time);

    [LibraryImport(X11, EntryPoint = "XSendEvent")]
    public static partial int XSendEventClient(nint display, nuint window, int propagate, nint eventMask, ref XClientMessageEvent evt);

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XClientMessageEvent
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public nint L0, L1, L2, L3, L4;
    }

    [LibraryImport(X11, EntryPoint = "XAutoRepeatOff")]
    public static partial int XAutoRepeatOff(nint display);

    [LibraryImport(X11, EntryPoint = "XAutoRepeatOn")]
    public static partial int XAutoRepeatOn(nint display);

    [LibraryImport(X11, EntryPoint = "XGrabPointer")]
    public static partial int XGrabPointer(nint display, nuint grabWindow, int ownerEvents, uint eventMask, int pointerMode, int keyboardMode, nuint confineTo, nuint cursor, nuint time);

    [LibraryImport(X11, EntryPoint = "XUngrabPointer")]
    public static partial int XUngrabPointer(nint display, nuint time);

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
            // A different pid with the game's title = a wrapper (gamescope) hosting the game — accept it.
            if (WindowPid(w) != processId && WindowPid(w) != 0 && !IsWrapper(w)) continue;
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

    /// <summary>Live mouse-button state from the server (works even while another client — the game —
    /// holds a pointer grab, which is exactly when our window never sees ButtonPress). vk: 1=LMB 2=RMB 4=MMB.</summary>
    public static bool IsMouseButtonDown(int vk)
    {
        EnsureDisplay();
        if (_display == 0) return false;
        var root = XDefaultRootWindow(_display);
        if (XQueryPointer(_display, root, out _, out _, out _, out _, out _, out _, out var mask) == 0) return false;
        var bit = vk switch { 0x01 => 0x100u, 0x02 => 0x400u, 0x04 => 0x200u, _ => 0u }; // Button1Mask/3/2
        return (mask & bit) != 0;
    }

    /// <summary>Take the pointer for <paramref name="window"/> (so the game stops receiving clicks while our
    /// menu is open). Returns false if another client — typically the game in fullscreen — already holds it.</summary>
    public static bool GrabPointer(nint display, nuint window)
    {
        if (display == 0 || window == 0) return false;
        const uint mask = (uint)(ButtonPressMask | 0x00000008L /*ButtonReleaseMask*/ | 0x00000040L /*PointerMotionMask*/);
        const int GrabModeAsync = 1;
        var r = XGrabPointer(display, window, 0, mask, GrabModeAsync, GrabModeAsync, 0, 0, 0);
        XFlush(display);
        return r == 0; // GrabSuccess
    }

    public static void UngrabPointer(nint display)
    {
        if (display == 0) return;
        XUngrabPointer(display, 0);
        XFlush(display);
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
        if (InputDpy == 0) return;
        if (TryMouseButton(vk, out var button))
        {
            XTestFakeButtonEvent(InputDpy, button, 1, 0);
            XTestFakeButtonEvent(InputDpy, button, 0, 0);
            XFlush(InputDpy);
            return;
        }
        var keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var code = XKeysymToKeycode(InputDpy, keysym);
        if (code == 0) return;
        XTestFakeKeyEvent(InputDpy, code, 1, 0);
        XTestFakeKeyEvent(InputDpy, code, 0, 0);
        XFlush(InputDpy);
    }

    private static int _heldFakeKeys;
    private static bool _autoRepeatSuppressed;

    // ── Nested input display (gamescope / Xephyr). The game lives inside a nested X server where it is
    //    ALWAYS the focused window, so XTest there reaches it no matter what the user does on the outer
    //    desktop. "auto" scans /tmp/.X11-unix for another server that hosts a Path of Exile window. ──
    private static nint _inputDisplay;
    private static string? _inputDisplayName;
    private static DateTime _nextAutoScanUtc = DateTime.MinValue;
    public static nint InputTargetHwnd;
    private static nint InputDpy => _inputDisplay != 0 ? _inputDisplay : _display;
    public static bool HasInputDisplay => _inputDisplay != 0;
    public static string? InputDisplayResolved { get; private set; }

    public static void SetInputDisplay(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        lock (Gate)
        {
            if (name is null)
            {
                if (_inputDisplay != 0) { RestoreAutoRepeat(); XCloseDisplay(_inputDisplay); _inputDisplay = 0; }
                _inputDisplayName = null; InputDisplayResolved = null;
                return;
            }
            var isAuto = string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(name, _inputDisplayName, StringComparison.Ordinal)
                && (_inputDisplay != 0 || !isAuto || DateTime.UtcNow < _nextAutoScanUtc))
                return;
            _inputDisplayName = name;
            if (_inputDisplay != 0) { RestoreAutoRepeat(); XCloseDisplay(_inputDisplay); _inputDisplay = 0; }
            InputDisplayResolved = null;
            if (isAuto)
            {
                _nextAutoScanUtc = DateTime.UtcNow.AddSeconds(5);
                foreach (var cand in NestedDisplayCandidates())
                {
                    var d = XOpenDisplay(cand);
                    if (d == 0) continue;
                    if (HasPoeWindowOn(d))
                    {
                        _inputDisplay = d; InputDisplayResolved = cand;
                        Console.WriteLine($"\nInput: nested display {cand} hosts the game — routing input there.");
                        return;
                    }
                    XCloseDisplay(d);
                }
                return;
            }
            _inputDisplay = XOpenDisplay(name);
            InputDisplayResolved = _inputDisplay != 0 ? name : null;
            if (_inputDisplay == 0) Console.Error.WriteLine($"Input: cannot open display '{name}'.");
        }
    }

    private static IEnumerable<string> NestedDisplayCandidates()
    {
        var cur = (Environment.GetEnvironmentVariable("DISPLAY") ?? ":0").Split('.')[0];
        if (!Directory.Exists("/tmp/.X11-unix")) yield break;
        foreach (var f in Directory.GetFiles("/tmp/.X11-unix", "X*"))
        {
            var n = Path.GetFileName(f)[1..];
            if (!int.TryParse(n, out _)) continue;
            var name = ":" + n;
            if (name == cur) continue;
            yield return name;
        }
    }

    private static bool HasPoeWindowOn(nint dpy)
    {
        var root = XDefaultRootWindow(dpy);
        return AnyPoeTitled(dpy, root, 2);
    }

    private static bool AnyPoeTitled(nint dpy, nuint w, int depth)
    {
        if (XQueryTree(dpy, w, out _, out _, out var children, out var n) == 0 || children == 0) return false;
        try
        {
            for (var i = 0; i < (int)n; i++)
            {
                var c = unchecked((nuint)(nint)Marshal.ReadIntPtr(children, i * nint.Size));
                if (WindowTitleOn(dpy, c).Contains("Path of Exile", StringComparison.OrdinalIgnoreCase)) return true;
                if (depth > 0 && AnyPoeTitled(dpy, c, depth - 1)) return true;
            }
        }
        finally { XFree(children); }
        return false;
    }

    private static string WindowTitleOn(nint dpy, nuint w)
    {
        var net = XInternAtom(dpy, "_NET_WM_NAME", 1);
        var utf8 = XInternAtom(dpy, "UTF8_STRING", 1);
        if (net != 0 && utf8 != 0
            && XGetWindowProperty(dpy, w, net, 0, 4096, 0, utf8, out _, out _, out var n, out _, out var data) == 0 && data != 0)
        {
            try { if (n > 0) return Marshal.PtrToStringUTF8(data, (int)n) ?? ""; }
            finally { XFree(data); }
        }
        if (XGetWindowProperty(dpy, w, 39 /* XA_WM_NAME */, 0, 4096, 0, 0, out _, out _, out var n2, out _, out var data2) == 0 && data2 != 0)
        {
            try { if (n2 > 0) return Marshal.PtrToStringAnsi(data2, (int)n2) ?? ""; }
            finally { XFree(data2); }
        }
        return "";
    }

    // ── Background (unfocused) input: synthetic events addressed to the GAME window via XSendEvent, so
    //    the bot keeps playing while the user is alt-tabbed and nothing leaks into the focused app. ──
    private const int KeyRelease = 3, MotionNotify = 6;
    private const long KeyPressMask = 1L << 0, KeyReleaseMask = 1L << 1, PointerMotionMask = 1L << 6;
    private static int _bgX, _bgY; // last cursor position (window client coords) we told the game about

    private static bool FillCommon(nuint window, ref XEvent e, int type)
    {
        if (!TryGetWindowRect(unchecked((nint)window), out var r)) return false;
        e.Type = type;
        e.SendEvent = 1;
        e.Display = _display;
        e.Window = window;
        e.Root = XDefaultRootWindow(_display);
        e.Subwindow = 0;
        e.Time = 0; // CurrentTime
        e.X = _bgX; e.Y = _bgY;
        e.XRoot = r.Left + _bgX; e.YRoot = r.Top + _bgY;
        e.State = 0;
        e.SameScreen = 1;
        return true;
    }

    public static void SendKeyToWindow(nint hwnd, ushort vk, bool down)
    {
        EnsureDisplay();
        if (_display == 0 || hwnd == 0) return;
        var w = unchecked((nuint)hwnd);
        // Mouse buttons are deliberately NOT sent: Wine treats a synthetic ButtonPress as hardware input,
        // user32 runs WM_MOUSEACTIVATE → SetForegroundWindow → XSetInputFocus, and the game steals focus back
        // from whatever the user is doing. Keyboard events do not activate. Callers skip mouse-bound actions.
        if (TryMouseButton(vk, out _)) return;
        var keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var code = XKeysymToKeycode(_display, keysym);
        if (code == 0) return;
        var ke = new XEvent();
        if (!FillCommon(w, ref ke, down ? KeyPress : KeyRelease)) return;
        ke.Button = code; // XKeyEvent.keycode occupies the same slot as XButtonEvent.button
        XSendEvent(_display, w, 0, (nint)(down ? KeyPressMask : KeyReleaseMask), ref ke);
        XFlush(_display);
    }

    public static void SendPointerToWindow(nint hwnd, int clientX, int clientY)
    {
        EnsureDisplay();
        if (_display == 0 || hwnd == 0) return;
        var w = unchecked((nuint)hwnd);
        _bgX = clientX; _bgY = clientY;
        var me = new XEvent();
        if (!FillCommon(w, ref me, MotionNotify)) return;
        me.Button = 0; // is_hint = NotifyNormal
        XSendEvent(_display, w, 0, (nint)PointerMotionMask, ref me);
        XFlush(_display);
    }

    /// <summary>
    /// X server autorepeat applies to XTest-held keys too: a held Space would reach the game as a stream of
    /// press/release pairs ("spamming"). Turn server autorepeat off while we hold any key, back on when the
    /// last one is released.
    /// </summary>
    private static void UpdateAutoRepeat()
    {
        if (InputDpy == 0) return;
        var want = _heldFakeKeys > 0;
        if (want == _autoRepeatSuppressed) return;
        _autoRepeatSuppressed = want;
        if (want) XAutoRepeatOff(InputDpy); else XAutoRepeatOn(InputDpy);
        XFlush(InputDpy);
    }

    /// <summary>Restore autorepeat unconditionally (shutdown).</summary>
    public static void RestoreAutoRepeat()
    {
        if (InputDpy == 0 || !_autoRepeatSuppressed) return;
        _autoRepeatSuppressed = false;
        _heldFakeKeys = 0;
        XAutoRepeatOn(InputDpy);
        XFlush(InputDpy);
    }

    public static void SetKey(ushort vk, bool down)
    {
        EnsureDisplay();
        if (InputDpy == 0) return;
        if (TryMouseButton(vk, out var button))
        {
            XTestFakeButtonEvent(InputDpy, button, down ? 1 : 0, 0);
            XFlush(InputDpy);
            return;
        }
        var keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var code = XKeysymToKeycode(InputDpy, keysym);
        if (code == 0) return;
        if (down) { _heldFakeKeys++; UpdateAutoRepeat(); }
        XTestFakeKeyEvent(InputDpy, code, down ? 1 : 0, 0);
        XFlush(InputDpy);
        if (!down) { _heldFakeKeys = Math.Max(0, _heldFakeKeys - 1); UpdateAutoRepeat(); }
    }

    public static void SetCursorPos(int x, int y)
    {
        EnsureDisplay();
        if (_display == 0) return;
        if (_inputDisplay != 0)
        {
            // Nested server: its root IS the game surface (0,0 = the game's top-left) → translate outer
            // screen coords through the game's outer window rect.
            var lx = x; var ly = y;
            if (InputTargetHwnd != 0 && TryGetWindowRect(InputTargetHwnd, out var r)) { lx = x - r.Left; ly = y - r.Top; }
            var nroot = XDefaultRootWindow(_inputDisplay);
            XWarpPointer(_inputDisplay, 0, nroot, 0, 0, 0, 0, lx, ly);
            XFlush(_inputDisplay);
            return;
        }
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

    private static bool IsWrapper(nuint w)
    {
        if (XGetWindowProperty(_display, w, 67 /* XA_WM_CLASS */, 0, 256, 0, 31 /* XA_STRING */, out _, out _, out var n, out _, out var data) != 0 || data == 0)
            return false;
        try
        {
            var cls = (Marshal.PtrToStringAnsi(data, (int)n) ?? "").ToLowerInvariant();
            return cls.Contains("gamescope") || cls.Contains("xephyr");
        }
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
        return vk switch
        {
            0x08 => 0xFF08, // Backspace
            0x2D => 0xFF63, // Insert
            0x2E => 0xFFFF, // Delete
            0x24 => 0xFF50, // Home
            0x23 => 0xFF57, // End
            0x21 => 0xFF55, // Page Up
            0x22 => 0xFF56, // Page Down
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x10 => 0xFFE1, // Shift
            0x11 => 0xFFE3, // Control
            0x12 => 0xFFE9, // Alt
            0x14 => 0xFFE5, // Caps Lock
            0x60 => 0xFFB0, 0x61 => 0xFFB1, 0x62 => 0xFFB2, 0x63 => 0xFFB3, 0x64 => 0xFFB4, // Numpad 0-4
            0x65 => 0xFFB5, 0x66 => 0xFFB6, 0x67 => 0xFFB7, 0x68 => 0xFFB8, 0x69 => 0xFFB9, // Numpad 5-9
            0xC0 => 0x60,   // grave
            0xBD => 0x2D,   // minus
            0xBB => 0x3D,   // equals
            0xDB => 0x5B,   // [
            0xDD => 0x5D,   // ]
            0xBA => 0x3B,   // ;
            0xDE => 0x27,   // apostrophe
            0xBC => 0x2C,   // ,
            0xBE => 0x2E,   // .
            0xBF => 0x2F,   // /
            0xDC => 0x5C,   // backslash
            _ => 0,
        };
    }
}
