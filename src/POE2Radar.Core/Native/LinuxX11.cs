using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Native;

/// <summary>X11 / XTest / XFixes P/Invoke used by <see cref="GameHost"/> and the Linux overlay window.</summary>
public static partial class LinuxX11
{
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

    public const int CurrentTime = 0;

    public const int KeyPress = 2;

    public const int ReplayPointer = 2;

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

    public static bool ScreenToClient(nint hwnd, ref GameHost.Point pt)
    {
        if (!TryGetWindowRect(hwnd, out var r)) return false;
        pt.X -= r.Left;
        pt.Y -= r.Top;
        return true;
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
}
