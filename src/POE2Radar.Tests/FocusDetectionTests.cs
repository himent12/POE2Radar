using System.Text;
using POE2Radar.Core.Native;
using Xunit;

namespace POE2Radar.Tests;

public sealed class FocusDetectionTests
{
    [Theory]
    [InlineData("""{"address":"0x55","pid":188162,"class":"steam_proton","xwayland":true}""", 188162)]
    [InlineData("""{}""", 0)]                 // no focused window (e.g. empty workspace)
    [InlineData("Invalid", 0)]
    [InlineData("", 0)]
    [InlineData("""{"pid":"x"}""", 0)]
    public void Parses_the_pid_from_hyprland_activewindow(string reply, int expected)
        => Assert.Equal(expected, LinuxX11.ParseHyprlandPid(Encoding.UTF8.GetBytes(reply)));
}
