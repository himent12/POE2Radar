using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class DodgeInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Press_release_recovery_has_one_owner_and_no_repeat_presses(bool wasd)
    {
        var held = new HashSet<ushort>(); var downs = new List<ushort>();
        void Down(ushort k) { Assert.True(held.Add(k)); downs.Add(k); }
        void Up(ushort k) => Assert.True(held.Remove(k));
        var d = new DodgeInput(); var now = DateTime.UtcNow;
        d.Start(now, 32, wasd ? new ushort[] { 83, 65 } : Array.Empty<ushort>(), 650, Down, Up);
        Assert.True(d.Tick(now.AddMilliseconds(69), Up)); Assert.Contains((ushort)32, held);
        Assert.True(d.Tick(now.AddMilliseconds(70), Up)); Assert.DoesNotContain((ushort)32, held);
        Assert.True(d.Tick(now.AddMilliseconds(250), Up)); Assert.Empty(held);
        Assert.True(d.Tick(now.AddMilliseconds(649), Up));
        Assert.False(d.Tick(now.AddMilliseconds(650), Up)); Assert.Equal(wasd ? 3 : 1, downs.Count);
    }
    [Fact] public void Rebinding_or_focus_loss_releases_captured_keys_not_new_setting()
    {
        var held = new HashSet<ushort>(); var d = new DodgeInput();
        d.Start(DateTime.UtcNow,32,new ushort[] {87},650,k => held.Add(k), k => held.Remove(k));
        d.Cancel(k => held.Remove(k)); Assert.Empty(held); Assert.False(d.Busy);
        d.Start(DateTime.UtcNow,88,Array.Empty<ushort>(),650,k => held.Add(k), k => held.Remove(k));
        Assert.Equal(new ushort[] {88}, held); d.Cancel(k => held.Remove(k)); Assert.Empty(held);
    }
    [Fact] public void Partial_dispatch_failure_releases_direction_keys()
    {
        var held = new HashSet<ushort>(); var d = new DodgeInput();
        Assert.Throws<InvalidOperationException>(() => d.Start(DateTime.UtcNow,32,new ushort[] {87},650,
            k => { if (k == 32) throw new InvalidOperationException(); held.Add(k); }, k => held.Remove(k)));
        Assert.Empty(held); Assert.False(d.Busy);
    }
}
