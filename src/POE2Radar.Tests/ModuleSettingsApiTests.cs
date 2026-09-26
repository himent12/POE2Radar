using System.Text.Json;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>The dashboard's writes for the macro/trade modules are sanitized server-side: arm bits stay
/// hotkey-only, hotkeys that collide with the overlay's own keys are unbound, and only http(s) bookmarks survive.</summary>
public sealed class ModuleSettingsApiTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buff_keeper_arm_bit_is_never_taken_from_http(bool armed)
    {
        Assert.True(ApiServer.TryParseBuffKeeper(J("""{"enabled":true,"toggleHotkey":"F4","rules":[]}"""), armed, out var b));
        Assert.Equal(armed, b.Enabled);
    }

    [Fact]
    public void Buff_rules_are_clamped_and_capped()
    {
        var rules = string.Join(",", Enumerable.Range(0, 30).Select(_ => """{"key":999,"trigger":"Bogus","intervalMs":1,"minGapMs":0}"""));
        Assert.True(ApiServer.TryParseBuffKeeper(J($$"""{"toggleHotkey":"F8","rules":[{{rules}}]}"""), false, out var b));
        Assert.Equal(16, b.Rules.Count);
        Assert.All(b.Rules, r =>
        {
            Assert.Equal(0, r.Key);
            Assert.Equal("Missing", r.Trigger);
            Assert.True(r.IntervalMs >= 500);
            Assert.True(r.MinGapMs >= 250);
        });
        Assert.Equal("F4", b.ToggleHotkey); // F8 is the flask kill-switch → falls back
    }

    [Fact]
    public void Command_hotkeys_colliding_with_overlay_or_buff_toggle_are_unbound()
    {
        Assert.True(ApiServer.TryParseCommands(J("""
            {"commands":[{"name":"a","hotkey":"F8","text":"/x"},{"name":"b","hotkey":"f4","text":"/y"},{"name":"c","hotkey":"ctrl+f1","text":"/z"}],
             "bookmarks":[], "inspectWikiHotkey":"Insert", "inspectDbHotkey":"Alt+G"}
            """), "F4", out var c));
        Assert.Equal("", c.Commands[0].Hotkey);
        Assert.Equal("", c.Commands[1].Hotkey);
        Assert.Equal("Ctrl+F1", c.Commands[2].Hotkey);
        Assert.Equal("", c.InspectWikiHotkey);
        Assert.Equal("Alt+G", c.InspectDbHotkey);
    }

    [Fact]
    public void Only_http_bookmarks_survive_including_placeholder_templates()
    {
        Assert.True(ApiServer.TryParseCommands(J("""
            {"commands":[],"bookmarks":[
              {"name":"trade","url":"https://www.pathofexile.com/trade2/search/poe2/{league}"},
              {"name":"evil","url":"javascript:alert(1)"},
              {"name":"file","url":"file:///etc/passwd"}]}
            """), "F4", out var c));
        Assert.Single(c.Bookmarks);
        Assert.Equal("trade", c.Bookmarks[0].Name);
    }

    [Fact]
    public void Map_check_patterns_are_trimmed_deduplicated_and_capped()
    {
        var many = string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"p{i}\""));
        Assert.True(ApiServer.TryParseMapCheck(J($$"""{"enabled":true,"dangerous":[" Reflect ","reflect","",{{many}}]}"""), out var m));
        Assert.Equal("Reflect", m.Dangerous[0]);
        Assert.Equal(100, m.Dangerous.Count);
    }

    [Fact]
    public void Trade_panel_position_is_clamped_on_screen()
    {
        Assert.True(ApiServer.TryParseTrade(J("""{"panelX":4,"panelY":-1,"maxSessions":0}"""), out var t));
        Assert.Equal(0.95f, t.PanelX);
        Assert.Equal(0f, t.PanelY);
        Assert.Equal(1, t.MaxSessions);
    }
}
