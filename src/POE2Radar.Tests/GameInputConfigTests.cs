using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public sealed class GameInputConfigTests
{
    internal static string Config(bool wasd = false, int q = 81) => "[UI]\nuse_wasd_to_move=" + wasd.ToString().ToLowerInvariant()
        + "\n[ACTION_KEYS]\n" + Actions(q) + "\n[WASD_ACTION_KEYS]\n" + Actions(q + 1)
        + "move_up=87\nmove_left=65\nmove_down=83\nmove_right=68\n";
    private static string Actions(int q) => string.Join('\n', Enumerable.Range(1, 13)
        .Select(i => "use_bound_skill" + i + "=" + (i == 4 ? q.ToString() : i == 9 ? q + " 2" : "0")))
        + "\nuse_dodge_roll=32\nuse_flask_in_slot1=49\nuse_flask_in_slot2=50\nweapon_swap=88\n";

    [Theory]
    [InlineData(false, 81, "Mouse")]
    [InlineData(true, 82, "WASD")]
    public void Reads_active_mode_and_ctrl_bar_without_assuming_defaults(bool wasd, int key, string mode)
    {
        var input = GameInputConfig.Parse(Config(wasd));
        Assert.True(input.Complete);
        Assert.Equal(mode, input.Mode);
        Assert.Equal(key, input.Find("use_bound_skill4")!.Key);
        Assert.Equal(2, input.Find("use_bound_skill9")!.Modifiers);
        Assert.Equal("Ctrl+" + (char)key, input.Find("use_bound_skill9")!.Label);
        Assert.False(input.Find("use_bound_skill1")!.Usable);
    }

    [Theory]
    [InlineData("81 bad")]
    [InlineData("81 8")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("81 2 4")]
    public void Invalid_binding_never_silently_becomes_a_plain_key(string value)
    {
        var input = GameInputConfig.Parse(Config().Replace("use_bound_skill4=81", "use_bound_skill4=" + value));
        Assert.False(input.Complete);
        Assert.Null(input.Find("use_bound_skill4"));
    }

    [Fact]
    public void Missing_mode_or_truncated_slots_blocks_automatic_import()
    {
        Assert.False(GameInputConfig.Parse(Config().Replace("use_wasd_to_move=false", "")).Complete);
        Assert.False(GameInputConfig.Parse(Config().Replace("use_bound_skill13=0", "")).Complete);
    }

    [Fact]
    public void Fingerprint_tracks_active_keys_and_mode_but_not_graphics_or_location()
    {
        var input = GameInputConfig.Parse(Config());
        Assert.Equal(input.Fingerprint, GameInputConfig.Parse(Config() + "\n[DISPLAY]\nwidth=1920", "elsewhere").Fingerprint);
        Assert.NotEqual(input.Fingerprint, GameInputConfig.Parse(Config(q: 69)).Fingerprint);
        Assert.NotEqual(input.Fingerprint, GameInputConfig.Parse(Config(true)).Fingerprint);
    }

    [Fact]
    public void Whitespace_bom_crlf_and_comments_are_supported()
    {
        var input = GameInputConfig.Parse("\uFEFF; comment\r\n" + Config().Replace("=", " = ").Replace("\n", "\r\n"));
        Assert.True(input.Complete);
        Assert.Equal(81, input.Find("use_bound_skill4")!.Key);
    }
}
