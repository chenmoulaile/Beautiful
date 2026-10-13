using Beautiful.Core.Dock;
using Xunit;

public class MagnifierTests
{
    [Fact]
    public void 悬停时自身最大()
    {
        var s = Magnifier.ComputeScales(5, hovered: 2, maxScale: 2.0, sigma: 2.0);
        Assert.Equal(2.0, s[2], 3);
        Assert.All(s, v => Assert.InRange(v, 1.0, 2.0));
    }

    [Fact]
    public void 远离悬停项的图标接近1()
    {
        var s = Magnifier.ComputeScales(11, hovered: 0, maxScale: 2.0, sigma: 2.0);
        Assert.True(s[5] < 1.15);
    }

    [Fact]
    public void 无悬停时全部为1()
    {
        var s = Magnifier.ComputeScales(4, hovered: -1, maxScale: 2.0, sigma: 2.0);
        Assert.All(s, v => Assert.Equal(1.0, v));
    }
}
