using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BuffReadTests
{
    [Theory]
    [InlineData("arcane_surge", true)]
    [InlineData("flask_effect_life", true)]
    [InlineData("player_aura_2", true)]
    [InlineData("a.b-c", true)]
    [InlineData("ab", true)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData("1234_5", false)]
    [InlineData("arcane surge", false)]
    [InlineData("ÿþarcane", false)]
    [InlineData("arcane一", false)]
    public void IsPlausibleBuffId(string id, bool expected)
        => Assert.Equal(expected, Poe2Live.IsPlausibleBuffId(id));

    [Fact]
    public void IsPlausibleBuffId_LengthBounds()
    {
        Assert.True(Poe2Live.IsPlausibleBuffId(new string('a', Poe2Live.MaxBuffIdLength)));
        Assert.False(Poe2Live.IsPlausibleBuffId(new string('a', Poe2Live.MaxBuffIdLength + 1)));
    }

    [Fact]
    public void IsPlausibleBuffVector_AllNullIsEmpty()
    {
        Assert.True(Poe2Live.IsPlausibleBuffVector(0, 0, 0, out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void IsPlausibleBuffVector_Shapes()
    {
        long b = 0x2_0000_0000; // not const: (nint)constant overflow checks are compile-time
        Assert.True(Poe2Live.IsPlausibleBuffVector((nint)b, (nint)(b + 24), (nint)(b + 32), out var n));
        Assert.Equal(3, n);
        Assert.True(Poe2Live.IsPlausibleBuffVector((nint)b, (nint)b, (nint)(b + 64), out n));
        Assert.Equal(0, n);
        Assert.False(Poe2Live.IsPlausibleBuffVector((nint)b, (nint)(b + 12), (nint)(b + 16), out _));        // not 8-aligned
        Assert.False(Poe2Live.IsPlausibleBuffVector((nint)(b + 8), (nint)b, (nint)(b + 16), out _));         // last < first
        Assert.False(Poe2Live.IsPlausibleBuffVector((nint)b, (nint)(b + 16), (nint)(b + 8), out _));         // end < last
        Assert.False(Poe2Live.IsPlausibleBuffVector((nint)b, (nint)(b + (Poe2Live.MaxBuffEntries + 1) * 8L),
            (nint)(b + (Poe2Live.MaxBuffEntries + 1) * 8L), out _));                                           // too many
        Assert.False(Poe2Live.IsPlausibleBuffVector(0x100, 0x108, 0x110, out _));                             // non-user ptrs
        Assert.False(Poe2Live.IsPlausibleBuffVector((nint)b, 0, 0, out _));                                   // half-null
    }

    [Theory]
    [InlineData(12.5f, 12.5f)]
    [InlineData(0f, 0f)]
    [InlineData(-3f, 0f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [InlineData(float.NaN, float.PositiveInfinity)]
    [InlineData(2_000_000f, float.PositiveInfinity)]
    public void NormalizeBuffTime(float raw, float expected)
        => Assert.Equal(expected, Poe2Live.NormalizeBuffTime(raw));

    [Fact]
    public void BuffInfo_IsInfinite()
    {
        Assert.True(new Poe2Live.BuffInfo("x", float.PositiveInfinity, float.PositiveInfinity, 0).IsInfinite);
        Assert.False(new Poe2Live.BuffInfo("x", 3f, 4f, 0).IsInfinite);
    }
}
