using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public sealed class AobPatternsTests
{
    [Theory]
    [InlineData(0x116)]
    [InlineData(0x120)]
    [InlineData(0x200)]
    public void Game_state_pattern_ignores_branch_displacement(int branch)
    {
        byte[] code = [0x48, 0x39, 0x2D, 0x33, 0x5F, 0x4C, 0x04,
            0x0F, 0x85, 0, 0, 0, 0, 0xB9, 0x48, 0x01, 0x00, 0x00];
        BitConverter.GetBytes(branch).CopyTo(code, 9);
        var pattern = Assert.Single(AobPatterns.GameStateRefs);
        Assert.Equal(0, Assert.Single(AobScanner.FindPattern(code, pattern.Bytes)));
        Assert.Equal(unchecked((nint)0x1445D1EE8), AobScanner.ResolveRipRelative(
            unchecked((nint)0x14010BFAE), 0, pattern.DispOffset, pattern.InstrLen, code));
    }

    [Fact]
    public void Game_state_pattern_rejects_other_allocation_sizes()
    {
        byte[] code = [0x48, 0x39, 0x2D, 0, 0, 0, 0,
            0x0F, 0x85, 0x76, 0x01, 0x00, 0x00, 0xB9, 0xE8, 0x02, 0x00, 0x00];
        Assert.Empty(AobScanner.FindPattern(code, Assert.Single(AobPatterns.GameStateRefs).Bytes));
    }
}
