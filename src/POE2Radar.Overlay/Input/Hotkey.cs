namespace POE2Radar.Overlay.Input;

/// <summary>
/// A user-bindable key combo stored as text in settings ("Ctrl+F5", "Alt+W", "Mouse4"). <see cref="Vk"/> is a
/// Win32 virtual-key; <c>default</c> (Vk 0) is "unbound". Parse is case-insensitive and accepts modifiers in
/// any order; <see cref="ToString"/> emits the canonical "Ctrl+Shift+Alt+Key" form, which parses back.
/// </summary>
public readonly record struct Hotkey(int Vk, bool Ctrl, bool Shift, bool Alt)
{
    public const int VkCtrl = 0x11, VkShift = 0x10, VkAlt = 0x12;

    public bool IsBound => Vk != 0;

    private static readonly (string Name, int Vk)[] Named =
    [
        ("Mouse3", 0x04), ("Mouse4", 0x05), ("Mouse5", 0x06),
        ("Backspace", 0x08), ("Tab", 0x09), ("Enter", 0x0D), ("Pause", 0x13), ("Esc", 0x1B), ("Space", 0x20),
        ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Insert", 0x2D), ("Delete", 0x2E),
        ("Num*", 0x6A), ("Num+", 0x6B), ("Num-", 0x6D), ("Num.", 0x6E), ("Num/", 0x6F),
        ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), (";", 0xBA), ("'", 0xDE),
        (",", 0xBC), (".", 0xBE), ("/", 0xBF), ("\\", 0xDC),
    ];

    private static readonly (string Name, int Vk)[] Aliases =
    [
        ("Escape", 0x1B), ("Return", 0x0D), ("Ins", 0x2D), ("Del", 0x2E), ("PgUp", 0x21), ("PgDn", 0x22),
        ("XButton1", 0x05), ("XButton2", 0x06), ("MMB", 0x04), ("Middle", 0x04),
    ];

    /// <summary>Empty / "None" parse to the unbound hotkey (true). A lone modifier or an unknown key → false.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        var s = text?.Trim() ?? "";
        if (s.Length == 0 || s.Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
        bool ctrl = false, shift = false, alt = false;
        // Peel "Mod+" prefixes rather than splitting on '+', so keys like "Num+" survive.
        while (true)
        {
            var plus = s.IndexOf('+');
            if (plus <= 0 || plus == s.Length - 1) break;
            var mod = s[..plus].Trim();
            if (mod.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || mod.Equals("Control", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (mod.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (mod.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else break;
            s = s[(plus + 1)..].Trim();
        }
        var vk = KeyVk(s);
        if (vk == 0) return false;
        hotkey = new Hotkey(vk, ctrl, shift, alt);
        return true;
    }

    /// <summary>Parse or unbound — for settings strings that may have been hand-edited into garbage.</summary>
    public static Hotkey ParseOrNone(string? text) => TryParse(text, out var hk) ? hk : default;

    private static int KeyVk(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        if (key.Length == 4 && key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && key[3] is >= '0' and <= '9')
            return 0x60 + (key[3] - '0');
        foreach (var (name, vk) in Named) if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return vk;
        foreach (var (name, vk) in Aliases) if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return vk;
        return 0;
    }

    private static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        foreach (var (name, v) in Named) if (v == vk) return name;
        return $"VK{vk:X2}";
    }

    /// <summary>Canonical text; unbound → "" (what settings store for "no hotkey").</summary>
    public override string ToString()
    {
        if (!IsBound) return "";
        var prefix = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
        return prefix + KeyName(Vk);
    }
}

/// <summary>
/// Rising-edge detector for polled hotkeys. Fires on the MAIN key's press edge only when the held modifiers
/// match exactly, so "F5" and "Ctrl+F5" never trigger each other and releasing a modifier while the key is
/// held does not re-fire. Call <see cref="Pressed"/> every poll for every bound hotkey.
/// </summary>
public sealed class HotkeyWatcher
{
    private readonly Func<long> _nowMs;
    private readonly int _debounceMs;
    private readonly Dictionary<Hotkey, bool> _wasDown = new();
    private readonly Dictionary<Hotkey, long> _lastFire = new();

    public HotkeyWatcher(int debounceMs = 150, Func<long>? nowMs = null)
    {
        _debounceMs = debounceMs;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// True once per press of <paramref name="hk"/>. The first observation of a hotkey only records its state,
    /// so a key already held when a binding is created/changed does not fire it.
    /// </summary>
    public bool Pressed(Hotkey hk, Func<int, bool> isDown)
    {
        if (!hk.IsBound) return false;
        var down = isDown(hk.Vk);
        var seen = _wasDown.TryGetValue(hk, out var was);
        _wasDown[hk] = down;
        if (!seen || !down || was) return false;
        if (isDown(Hotkey.VkCtrl) != hk.Ctrl || isDown(Hotkey.VkShift) != hk.Shift || isDown(Hotkey.VkAlt) != hk.Alt) return false;
        var now = _nowMs();
        if (_lastFire.TryGetValue(hk, out var last) && now - last < _debounceMs) return false;
        _lastFire[hk] = now;
        return true;
    }

    /// <summary>Forget all edge state (e.g. after focus loss, so a key held across alt-tab doesn't fire).</summary>
    public void Reset() { _wasDown.Clear(); }
}
