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

    // All synthesized input goes to the main display (the one the game window lives on).
    private static nint InputDpy => _display;

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
