using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Native;

public static partial class LinuxX11
{
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

    private static int _bgX, _bgY;

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
            0x6A => 0xFFAA, 0x6B => 0xFFAB, 0x6C => 0xFFAC, 0x6D => 0xFFAD, 0x6E => 0xFFAE, 0x6F => 0xFFAF,
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
