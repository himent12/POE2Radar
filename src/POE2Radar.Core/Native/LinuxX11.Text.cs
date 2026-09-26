using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Native;

public static partial class LinuxX11
{
    private const nuint XK_Shift_L = 0xFFE1, XK_Control_L = 0xFFE3, XK_Alt_L = 0xFFE9;

    /// <summary>Keysym at (group, shift level) of a keycode — tells us whether a character's key needs Shift.
    /// KeyCode is widened to <c>unsigned int</c> (NeedWidePrototypes ABI), which is safe either way.</summary>
    [LibraryImport(X11, EntryPoint = "XkbKeycodeToKeysym")]
    private static partial nuint XkbKeycodeToKeysym(nint display, uint keycode, int group, int level);

    /// <summary>
    /// Type text through XTest on the main display (chat is foreground-gated, so the focused window is the
    /// game). Each character → keysym → keycode on the CURRENT layout (group 0); Shift is added when the
    /// keysym sits on level 1. Characters with no key (or only on AltGr levels) are skipped rather than
    /// remapping the user's keymap, and the call returns false.
    /// </summary>
    public static bool TypeText(string text)
    {
        EnsureDisplay();
        var dpy = _display;
        if (dpy == 0) return false;
        var shift = XKeysymToKeycode(dpy, XK_Shift_L);
        var ok = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var keysym = KeysymForRune(rune);
            var code = keysym == 0 ? (byte)0 : XKeysymToKeycode(dpy, keysym);
            if (code == 0) { ok = false; continue; }
            bool needShift;
            if (XkbKeycodeToKeysym(dpy, code, 0, 0) == keysym) needShift = false;
            else if (XkbKeycodeToKeysym(dpy, code, 0, 1) == keysym && shift != 0) needShift = true;
            else { ok = false; continue; }
            if (needShift) XTestFakeKeyEvent(dpy, shift, 1, 0);
            XTestFakeKeyEvent(dpy, code, 1, 0);
            XTestFakeKeyEvent(dpy, code, 0, 0);
            if (needShift) XTestFakeKeyEvent(dpy, shift, 0, 0);
        }
        XFlush(dpy);
        return ok;
    }

    /// <summary>X keysym for a character: Latin-1 printables map 1:1, everything else uses the Unicode
    /// keysym range (0x01000000 | codepoint). Control characters have none.</summary>
    internal static nuint KeysymForRune(Rune rune)
    {
        var cp = rune.Value;
        if (cp is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF) return (nuint)cp;
        if (Rune.IsControl(rune)) return 0;
        return (nuint)(0x01000000 | cp);
    }

    public static bool TapChord(ushort vk, bool ctrl, bool shift, bool alt)
    {
        EnsureDisplay();
        var dpy = _display;
        if (dpy == 0) return false;
        uint button = 0, code = 0;
        if (!TryMouseButton(vk, out button))
        {
            var keysym = VkToKeysym(vk);
            code = keysym == 0 ? 0u : XKeysymToKeycode(dpy, keysym);
            if (code == 0) return false;
        }
        var mods = new List<uint>(3);
        if (ctrl) mods.Add(XKeysymToKeycode(dpy, XK_Control_L));
        if (shift) mods.Add(XKeysymToKeycode(dpy, XK_Shift_L));
        if (alt) mods.Add(XKeysymToKeycode(dpy, XK_Alt_L));
        foreach (var m in mods) if (m != 0) XTestFakeKeyEvent(dpy, m, 1, 0);
        if (button != 0) { XTestFakeButtonEvent(dpy, button, 1, 0); XTestFakeButtonEvent(dpy, button, 0, 0); }
        else { XTestFakeKeyEvent(dpy, code, 1, 0); XTestFakeKeyEvent(dpy, code, 0, 0); }
        for (var i = mods.Count - 1; i >= 0; i--) if (mods[i] != 0) XTestFakeKeyEvent(dpy, mods[i], 0, 0);
        XFlush(dpy);
        return true;
    }
}
