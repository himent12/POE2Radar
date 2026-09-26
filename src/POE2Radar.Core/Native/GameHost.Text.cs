using System.Runtime.InteropServices;

namespace POE2Radar.Core.Native;

/// <summary>
/// Character-level input for chat macros. Separate from the per-key <see cref="TapKey"/> path because typed
/// text must reach the game as characters (layout-independent), not as virtual keys.
/// </summary>
public static partial class GameHost
{
    /// <summary>
    /// Type <paramref name="text"/> into whatever has keyboard focus (always foreground input — callers gate
    /// on the game being focused). Returns false when any character could not be produced (control chars,
    /// Linux: no key on the active layout); the typeable characters are still sent.
    /// </summary>
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        return OperatingSystem.IsLinux() ? LinuxX11.TypeText(text) : Win32.TypeText(text);
    }

    /// <summary>Tap <paramref name="vk"/> with the given modifiers held (pressed before, released after, in
    /// reverse order). Foreground input only.</summary>
    public static bool TapChord(ushort vk, bool ctrl, bool shift, bool alt)
        => OperatingSystem.IsLinux() ? LinuxX11.TapChord(vk, ctrl, shift, alt) : Win32.TapChord(vk, ctrl, shift, alt);

    internal static partial class Win32
    {
        private const uint KEYEVENTF_UNICODE = 0x0004;

        /// <summary>One down+up <c>KEYEVENTF_UNICODE</c> pair per UTF-16 unit (surrogates included), in a
        /// single SendInput batch so no other synthesized input can interleave mid-word.</summary>
        public static bool TypeText(string text)
        {
            var inputs = new List<INPUT>(text.Length * 2);
            var ok = true;
            foreach (var c in text)
            {
                if (char.IsControl(c)) { ok = false; continue; }
                var down = new INPUT { type = INPUT_KEYBOARD };
                down.U.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE };
                var up = new INPUT { type = INPUT_KEYBOARD };
                up.U.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
                inputs.Add(down);
                inputs.Add(up);
            }
            if (inputs.Count == 0) return ok;
            var arr = inputs.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) == arr.Length && ok;
        }

        public static bool TapChord(ushort vk, bool ctrl, bool shift, bool alt)
        {
            var mods = new List<ushort>(3);
            if (ctrl) mods.Add(0x11);
            if (shift) mods.Add(0x10);
            if (alt) mods.Add(0x12);
            var seq = new List<INPUT>(mods.Count * 2 + 2);
            foreach (var m in mods) if (TryScanInput(m, true, out var i)) seq.Add(i);
            if (TryMouseFlags(vk, out var mdown, out var mup, out var data))
            {
                var d = new INPUT { type = INPUT_MOUSE };
                d.U.mi = new MOUSEINPUT { dwFlags = mdown, mouseData = data };
                var u = new INPUT { type = INPUT_MOUSE };
                u.U.mi = new MOUSEINPUT { dwFlags = mup, mouseData = data };
                seq.Add(d); seq.Add(u);
            }
            else if (TryScanInput(vk, true, out var kd) && TryScanInput(vk, false, out var ku)) { seq.Add(kd); seq.Add(ku); }
            else return false;
            for (var m = mods.Count - 1; m >= 0; m--) if (TryScanInput(mods[m], false, out var i)) seq.Add(i);
            var arr = seq.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) == arr.Length;
        }

        private static bool TryScanInput(ushort vk, bool down, out INPUT input)
        {
            input = new INPUT { type = INPUT_KEYBOARD };
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) return false;
            input.U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) };
            return true;
        }
    }
}
