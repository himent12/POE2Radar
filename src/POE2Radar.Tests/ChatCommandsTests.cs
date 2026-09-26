using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ChatCommandsTests
{
    private static readonly PlaceholderContext Full = new("MyChar", "TheirName", "HC Runes of Aldur", "The Riverbank");

    private static IReadOnlyList<string> Expand(string text, PlaceholderContext? ctx = null)
    {
        Assert.True(ChatCommands.TryExpand(text, ctx ?? Full, out var lines, out var error), error);
        Assert.Null(error);
        return lines;
    }

    [Fact]
    public void Leading_at_last_stays_a_whisper()
        => Assert.Equal(["@TheirName ty, gl!"], Expand("@last ty, gl!"));

    [Fact]
    public void At_tokens_elsewhere_are_bare_names()
    {
        Assert.Equal(["/invite TheirName"], Expand("/invite @last"));
        Assert.Equal(["/kick MyChar"], Expand("/kick @char"));
        Assert.Equal(["@MyChar note to self"], Expand("@char note to self"));
    }

    [Fact]
    public void Braced_tokens_are_bare_and_case_insensitive()
        => Assert.Equal(["TheirName MyChar HC Runes of Aldur The Riverbank"], Expand("{last} {CHAR} {League} {area}"));

    [Fact]
    public void Only_whole_word_at_tokens_are_replaced()
    {
        Assert.Equal(["@Charlie hi"], Expand("@Charlie hi"));
        Assert.Equal(["@lastname hi"], Expand("@lastname hi"));
        Assert.Equal(["mail foo@char.com"], Expand("mail foo@char.com"));
        Assert.Equal(["@@char"], Expand("@@char"));
        Assert.Equal(["@Last hi"], Expand("@Last hi")); // exact lowercase only: real names are case-sensitive
        Assert.Equal(["see MyChar's stash"], Expand("see @char's stash"));
    }

    [Fact]
    public void Unknown_placeholder_refuses_to_send()
    {
        var noLast = Full with { LastWhisper = null };
        Assert.False(ChatCommands.TryExpand("@last ty", noLast, out var lines, out var error));
        Assert.Empty(lines);
        Assert.Equal("no whisper partner yet", error);
        Assert.False(ChatCommands.TryExpand("/hideout\n{league}", Full with { League = " " }, out _, out error));
        Assert.Equal("league unknown", error);
        Assert.False(ChatCommands.TryExpand("{char}", new PlaceholderContext(), out _, out error));
        Assert.Equal("character name unknown", error);
        // Placeholders the text doesn't use don't matter.
        Assert.Equal(["/hideout"], Expand("/hideout", new PlaceholderContext()));
    }

    [Fact]
    public void Multi_line_text_becomes_separate_messages()
        => Assert.Equal(["/hideout", "@TheirName thanks"], Expand("  /hideout \r\n\r\n@last thanks\n   \n"));

    [Fact]
    public void Blank_text_is_an_error()
    {
        Assert.False(ChatCommands.TryExpand(" \n ", Full, out _, out var error));
        Assert.Equal("nothing to send", error);
        Assert.False(ChatCommands.TryExpand(null, Full, out _, out _));
    }

    [Fact]
    public void Control_characters_are_stripped()
        => Assert.Equal(["/hideout"], Expand("/hide\tout\u0007"));
}
