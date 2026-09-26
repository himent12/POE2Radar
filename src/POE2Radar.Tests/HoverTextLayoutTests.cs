using POE2Radar.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace POE2Radar.Tests;

public sealed class HoverTextLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void Reuses_layout_without_allocating_and_invalidates_when_content_or_width_changes()
    {
        var cache = new HoverTextLayout();
        const string detail = "A market variant with a long name\nRecent trend: -12.5% · traded volume 300 div";
        var first = cache.Get("Estimated value: 18 div", detail, 32);
        for (int i = 0; i < 1000; i++) cache.Get("Estimated value: 18 div", detail, 32);
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) cache.Get("Estimated value: 18 div", detail, 32);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        output.WriteLine($"Cached layout: {allocated} bytes for 10,000 calls");
        Assert.Equal(0, allocated);
        Assert.Same(first, cache.Get("Estimated value: 18 div", detail, 32));
        Assert.NotSame(first, cache.Get("Estimated value: 19 div", detail, 32));
        var narrow = cache.Get("Estimated value: 19 div", detail, 10);
        Assert.All(narrow.Rows, row => Assert.True(row.Length <= 10));
        Assert.Contains("Recent", string.Join(" ", narrow.Rows));
    }
}
