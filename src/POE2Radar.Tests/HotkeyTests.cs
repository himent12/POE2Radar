using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("F5", 0x74, false, false, false)]
    [InlineData("ctrl+f5", 0x74, true, false, false)]
    [InlineData("Alt+W", 'W', false, false, true)]
    [InlineData("Shift+Ctrl+1", '1', true, true, false)]
    [InlineData("Num5", 0x65, false, false, false)]
    [InlineData("Insert", 0x2D, false, false, false)]
    [InlineData("Home", 0x24, false, false, false)]
    [InlineData("PageUp", 0x21, false, false, false)]
    [InlineData("Mouse4", 0x05, false, false, false)]
    [InlineData("Mouse5", 0x06, false, false, false)]
    [InlineData("z", 'Z', false, false, false)]
    [InlineData("F24", 0x87, false, false, false)]
    [InlineData("`", 0xC0, false, false, false)]
    [InlineData("Ctrl+\\", 0xDC, true, false, false)]
    [InlineData("Alt+Num+", 0x6B, false, false, true)]
    [InlineData(" Control + '", 0xDE, true, false, false)]
    public void Parses_keys_and_modifiers(string text, int vk, bool ctrl, bool shift, bool alt)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(vk, ctrl, shift, alt), hk);
    }

    [Theory]
    [InlineData("F5")] [InlineData("Ctrl+Shift+Alt+F12")] [InlineData("Alt+W")] [InlineData("Num0")]
    [InlineData("Mouse4")] [InlineData("PageDown")] [InlineData("Ctrl+=")] [InlineData("Shift+/")]
    [InlineData("Num+")] [InlineData("'")] [InlineData("[")] [InlineData("]")] [InlineData(";")]
    [InlineData(",")] [InlineData(".")] [InlineData("-")] [InlineData("\\")] [InlineData("9")]
    public void Canonical_text_round_trips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(text, hk.ToString());
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Fact]
    public void Modifier_order_is_normalized()
    {
        Assert.True(Hotkey.TryParse("Shift+Ctrl+1", out var hk));
        Assert.Equal("Ctrl+Shift+1", hk.ToString());
    }

    [Theory]
    [InlineData("")] [InlineData("  ")] [InlineData("None")] [InlineData(null)]
    public void Empty_or_none_is_unbound(string? text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.False(hk.IsBound);
        Assert.Equal("", hk.ToString());
    }

    [Theory]
    [InlineData("Ctrl")] [InlineData("Ctrl+")] [InlineData("F25")] [InlineData("F0")] [InlineData("Hyper+F5")]
    [InlineData("Banana")] [InlineData("Num10")]
    public void Rejects_garbage(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Reserved_overlay_keys_are_detected_in_any_modifier_combo()
    {
        Assert.True(CommandSettings.IsReserved(Hotkey.ParseOrNone("F6")));
        Assert.True(CommandSettings.IsReserved(Hotkey.ParseOrNone("Alt+F12")));
        Assert.False(CommandSettings.IsReserved(Hotkey.ParseOrNone("F5")));
        Assert.True(CommandSettings.IsReserved(Hotkey.ParseOrNone("Ctrl+F8")));
        Assert.True(CommandSettings.IsReserved(Hotkey.ParseOrNone("Insert")));
        Assert.True(CommandSettings.IsReserved(Hotkey.ParseOrNone("Ctrl+Shift+D")));
        Assert.False(CommandSettings.IsReserved(Hotkey.ParseOrNone("D")));
        Assert.False(CommandSettings.IsReserved(Hotkey.ParseOrNone("F1")));
        var s = new CommandSettings();
        foreach (var hk in s.Commands.Select(c => c.Hotkey).Concat(s.Bookmarks.Select(b => b.Hotkey))
                     .Append(s.InspectWikiHotkey).Append(s.InspectDbHotkey))
        {
            Assert.True(Hotkey.TryParse(hk, out var parsed), hk);
            Assert.False(CommandSettings.IsReserved(parsed), hk);
        }
    }

    private sealed class Keys
    {
        public readonly HashSet<int> Down = new();
        public bool IsDown(int vk) => Down.Contains(vk);
    }

    [Fact]
    public void Fires_once_per_press_on_rising_edge()
    {
        long now = 0; var keys = new Keys(); var w = new HotkeyWatcher(150, () => now);
        var f5 = Hotkey.ParseOrNone("F5");
        Assert.False(w.Pressed(f5, keys.IsDown)); // first observation only records state
        keys.Down.Add(0x74); now += 16;
        Assert.True(w.Pressed(f5, keys.IsDown));
        now += 16; Assert.False(w.Pressed(f5, keys.IsDown)); // held
        keys.Down.Remove(0x74); now += 200; Assert.False(w.Pressed(f5, keys.IsDown));
        keys.Down.Add(0x74); now += 16; Assert.True(w.Pressed(f5, keys.IsDown));
    }

    [Fact]
    public void Key_held_when_binding_first_seen_does_not_fire()
    {
        var keys = new Keys(); keys.Down.Add('H'); keys.Down.Add(Hotkey.VkAlt);
        var w = new HotkeyWatcher(0, () => 0);
        var hk = Hotkey.ParseOrNone("Alt+H");
        Assert.False(w.Pressed(hk, keys.IsDown));
        Assert.False(w.Pressed(hk, keys.IsDown));
    }

    [Fact]
    public void Exact_modifiers_keep_plain_and_ctrl_bindings_apart()
    {
        long now = 0; var keys = new Keys(); var w = new HotkeyWatcher(0, () => now);
        var plain = Hotkey.ParseOrNone("F5"); var ctrl = Hotkey.ParseOrNone("Ctrl+F5");
        bool Plain() => w.Pressed(plain, keys.IsDown);
        bool Ctrl() => w.Pressed(ctrl, keys.IsDown);
        Plain(); Ctrl();
        keys.Down.Add(Hotkey.VkCtrl); Assert.False(Plain()); Assert.False(Ctrl());
        keys.Down.Add(0x74); now++; Assert.False(Plain()); Assert.True(Ctrl());
        keys.Down.Remove(Hotkey.VkCtrl); now++; Assert.False(Plain()); Assert.False(Ctrl()); // releasing Ctrl mid-hold
        keys.Down.Remove(0x74); now++; Plain(); Ctrl();
        keys.Down.Add(0x74); now++; Assert.True(Plain()); Assert.False(Ctrl());
        keys.Down.Add(Hotkey.VkCtrl); now++; Assert.False(Plain()); Assert.False(Ctrl()); // adding Ctrl mid-hold
        keys.Down.Remove(0x74); now++; Plain(); Ctrl();
        keys.Down.Add(Hotkey.VkShift); keys.Down.Add(0x74); now++;
        Assert.False(Plain()); Assert.False(Ctrl()); // Ctrl+Shift+F5 is neither
    }

    [Fact]
    public void Debounce_swallows_chatter()
    {
        long now = 0; var keys = new Keys(); var w = new HotkeyWatcher(150, () => now);
        var hk = Hotkey.ParseOrNone("Mouse4");
        w.Pressed(hk, keys.IsDown);
        keys.Down.Add(5); now = 10; Assert.True(w.Pressed(hk, keys.IsDown));
        keys.Down.Remove(5); now = 30; w.Pressed(hk, keys.IsDown);
        keys.Down.Add(5); now = 50; Assert.False(w.Pressed(hk, keys.IsDown));
        keys.Down.Remove(5); now = 200; w.Pressed(hk, keys.IsDown);
        keys.Down.Add(5); now = 220; Assert.True(w.Pressed(hk, keys.IsDown));
    }

    [Fact]
    public void Unbound_never_fires_and_reset_rearms_first_observation()
    {
        var keys = new Keys(); var w = new HotkeyWatcher(0, () => 0);
        Assert.False(w.Pressed(default, _ => true));
        var hk = Hotkey.ParseOrNone("F1");
        w.Pressed(hk, keys.IsDown);
        keys.Down.Add(0x70);
        w.Reset();
        Assert.False(w.Pressed(hk, keys.IsDown));
    }
}
