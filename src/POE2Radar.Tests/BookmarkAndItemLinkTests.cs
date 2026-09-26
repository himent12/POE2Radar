using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BookmarkAndItemLinkTests
{
    [Fact]
    public void Url_placeholders_are_url_encoded()
    {
        var ctx = new UrlContext("HC Runes of Aldur", "Kaom's Heart & Co", "Me/You");
        Assert.True(Bookmarks.TryExpand("https://www.pathofexile.com/trade2/search/poe2/{league}", ctx, out var url, out var error));
        Assert.Null(error);
        Assert.Equal("https://www.pathofexile.com/trade2/search/poe2/HC%20Runes%20of%20Aldur", url);
        Assert.True(Bookmarks.TryExpand("https://example.com/?q={ITEM}&c={char}", ctx, out url, out _));
        Assert.Equal("https://example.com/?q=Kaom%27s%20Heart%20%26%20Co&c=Me%2FYou", url);
    }

    [Fact]
    public void Missing_placeholder_value_refuses()
    {
        Assert.False(Bookmarks.TryExpand("https://x.test/{item}", new UrlContext("L"), out _, out var error));
        Assert.Equal("no hovered item", error);
        Assert.True(Bookmarks.TryExpand("https://poe.ninja/poe2/economy", new UrlContext(), out var url, out _));
        Assert.Equal("https://poe.ninja/poe2/economy", url);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/")]
    [InlineData("steam://run/2694490")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("/usr/bin/xterm")]
    [InlineData("example.com")]
    [InlineData("")]
    public void Non_http_urls_are_rejected(string url)
    {
        Assert.False(Bookmarks.TryExpand(url, new UrlContext(), out _, out var error));
        Assert.Equal("only http(s) URLs are allowed", error);
        Assert.False(BrowserLauncher.Open(url, out _));
    }

    [Fact]
    public void Placeholder_value_cannot_change_scheme_or_host()
    {
        Assert.False(Bookmarks.TryExpand("{item}", new UrlContext(Item: "file:///etc/passwd"), out _, out _));
        Assert.True(Bookmarks.TryExpand("https://good.test/{item}", new UrlContext(Item: "@evil.test/x"), out var url, out _));
        Assert.Equal("good.test", new Uri(url).Host);
    }

    [Fact]
    public void Default_bookmarks_are_valid_templates()
    {
        var ctx = new UrlContext("Standard", "Ring", "Me");
        foreach (var b in new Overlay.Config.CommandSettings().Bookmarks)
            Assert.True(Bookmarks.TryExpand(b.Url, ctx, out _, out var error), $"{b.Name}: {error}");
    }

    [Theory]
    [InlineData("Kaom's Heart", "https://www.poe2wiki.net/wiki/Kaom's_Heart")]
    [InlineData("  Atziri's  Disdain, Reborn ", "https://www.poe2wiki.net/wiki/Atziri's_Disdain,_Reborn")]
    [InlineData("A/B?c#d&e", "https://www.poe2wiki.net/wiki/A%2FB%3Fc%23d%26e")]
    [InlineData("Mjölner", "https://www.poe2wiki.net/wiki/Mj%C3%B6lner")]
    public void Wiki_links_use_underscored_escaped_titles(string name, string expected)
        => Assert.Equal(expected, ItemLinks.Wiki(name));

    [Fact]
    public void Poe2db_links_and_blank_names()
    {
        Assert.Equal("https://poe2db.tw/us/Expert_Dualstring_Bow", ItemLinks.Poe2Db("Expert Dualstring Bow"));
        Assert.Null(ItemLinks.Wiki("  "));
        Assert.Null(ItemLinks.Poe2Db(null));
    }

    [Fact]
    public void Trade_search_names_only_uniques()
    {
        var unique = ItemLinks.TradeSearch("HC Runes of Aldur", "Leather Belt", "Headhunter", Poe2Live.Rarity.Unique)!;
        Assert.StartsWith("https://www.pathofexile.com/trade2/search/poe2/HC%20Runes%20of%20Aldur?q=", unique);
        var q = Query(unique);
        Assert.Equal("securable", q.GetProperty("status").GetProperty("option").GetString());   // instant buyout: real asks
        Assert.Equal("Leather Belt", q.GetProperty("type").GetString());
        Assert.Equal("Headhunter", q.GetProperty("name").GetString());

        var rare = ItemLinks.TradeSearch("Standard", "Leather Belt", "Doom Cord", Poe2Live.Rarity.Rare)!;
        Assert.False(Query(rare).TryGetProperty("name", out _));
        Assert.Null(ItemLinks.TradeSearch(null, "Leather Belt", null, Poe2Live.Rarity.Normal));
    }

    private static JsonElement Query(string url)
        => JsonDocument.Parse(Uri.UnescapeDataString(url.Split("?q=")[1])).RootElement.GetProperty("query").Clone();
}
