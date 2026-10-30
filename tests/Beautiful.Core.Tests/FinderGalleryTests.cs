using System.Collections.Generic;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 画廊视图布局测试（Phase 6-c）。
/// <para>
/// 胶片条容量、原点、命中、滚动、方向键、大图缩放六件事共用同一套常量：
/// 把这些算术钉死，UI 侧才会出现"点到的缩略图就是选中的那个、选中项永远在视口内"。
/// 数值全部手算（84 宽 + 6 间隙 = 每格 90）。
/// </para>
/// </summary>
public class FinderGalleryTests
{
    // 视口 370 = 2*10 留白 + 3*84 + 2*6 → 恰好 3 格，便于手算
    private const double ViewportThreeThumbs = 370;

    [Fact]
    public void 容量按视口宽度算出能放下几个缩略图()
    {
        Assert.Equal(3, FinderGallery.Measure(ViewportThreeThumbs, 10).Capacity);
        Assert.Equal(1, FinderGallery.Measure(1, 10).Capacity);      // 极窄视口仍给 1，避免除零
        Assert.Equal(8, FinderGallery.Measure(800, 20).Capacity);    // (780+6)/90 = 8.73 → 8
    }

    [Fact]
    public void 内容宽度含首尾留白与格间间隙()
    {
        Assert.Equal(20, FinderGallery.Measure(ViewportThreeThumbs, 0).ContentWidth, precision: 3);
        Assert.Equal(284, FinderGallery.Measure(ViewportThreeThumbs, 3).ContentWidth, precision: 3);
        Assert.Equal(374, FinderGallery.Measure(ViewportThreeThumbs, 4).ContentWidth, precision: 3);
    }

    [Fact]
    public void 缩略图原点按格宽加间隙递进且越界返回负一()
    {
        Assert.Equal(10, FinderGallery.OriginOf(0, 5), precision: 3);
        Assert.Equal(100, FinderGallery.OriginOf(1, 5), precision: 3);
        Assert.Equal(190, FinderGallery.OriginOf(2, 5), precision: 3);
        Assert.Equal(-1, FinderGallery.OriginOf(5, 5), precision: 3);
        Assert.Equal(-1, FinderGallery.OriginOf(-1, 5), precision: 3);
    }

    [Fact]
    public void 命中测试在格子内选中间隙与留白不选()
    {
        Assert.Equal(0, FinderGallery.IndexAt(10, 5));
        Assert.Equal(0, FinderGallery.IndexAt(94, 5));    // 第 0 格右边缘（10+84）
        Assert.Equal(-1, FinderGallery.IndexAt(95, 5));   // 落进 6px 间隙
        Assert.Equal(1, FinderGallery.IndexAt(100, 5));
        Assert.Equal(-1, FinderGallery.IndexAt(9, 5));    // 左留白
        Assert.Equal(-1, FinderGallery.IndexAt(500, 5));  // 末项之后
        Assert.Equal(-1, FinderGallery.IndexAt(double.NaN, 5));
    }

    [Fact]
    public void 滚动偏移把选中项居中并夹在内容两端()
    {
        // 20 项：内容宽 = 20 + 20*84 + 19*6 = 1814；视口 370 → 最大偏移 1444
        var offset = FinderGallery.ScrollOffsetFor(ViewportThreeThumbs, 20, 10);
        var expected = (10 + (10 * 90)) + 42 - 185;
        Assert.Equal(expected, offset, precision: 3);

        Assert.Equal(0, FinderGallery.ScrollOffsetFor(ViewportThreeThumbs, 20, 0), precision: 3);
        Assert.Equal(1444, FinderGallery.ScrollOffsetFor(ViewportThreeThumbs, 20, 19), precision: 3);
    }

    [Fact]
    public void 未选中或空列表的滚动偏移归零()
    {
        Assert.Equal(0, FinderGallery.ScrollOffsetFor(ViewportThreeThumbs, 20, -1), precision: 3);
        Assert.Equal(0, FinderGallery.ScrollOffsetFor(ViewportThreeThumbs, 0, 3), precision: 3);
        Assert.Equal(0, FinderGallery.ScrollOffsetFor(double.NaN, 5, 2), precision: 3);
    }

    [Fact]
    public void 方向键在两端夹住不环绕()
    {
        Assert.Equal(3, FinderGallery.MoveSelection(2, 1, 5));
        Assert.Equal(4, FinderGallery.MoveSelection(4, 1, 5));
        Assert.Equal(0, FinderGallery.MoveSelection(0, -1, 5));
        Assert.Equal(-1, FinderGallery.MoveSelection(0, 1, 0));
    }

    [Fact]
    public void 无选中时按方向落到首项或末项()
    {
        Assert.Equal(0, FinderGallery.MoveSelection(-1, 1, 5));
        Assert.Equal(4, FinderGallery.MoveSelection(-1, -1, 5));
    }

    [Fact]
    public void 大图等比缩进可用区域且不放大小图()
    {
        var (width, height) = FinderGallery.FitPreview(800, 600, 4000, 3000);
        Assert.Equal(800, width, precision: 3);
        Assert.Equal(600, height, precision: 3);

        var (narrowWidth, narrowHeight) = FinderGallery.FitPreview(800, 600, 1000, 4000);
        Assert.Equal(150, narrowWidth, precision: 3);
        Assert.Equal(600, narrowHeight, precision: 3);

        var (smallWidth, smallHeight) = FinderGallery.FitPreview(800, 600, 16, 16);
        Assert.Equal(16, smallWidth, precision: 3);
        Assert.Equal(16, smallHeight, precision: 3);
    }

    [Fact]
    public void 源尺寸非法时预览尺寸为零由调用方回退()
    {
        Assert.Equal((0d, 0d), FinderGallery.FitPreview(800, 600, 0, 300));
        Assert.Equal((0d, 0d), FinderGallery.FitPreview(800, 600, 300, -1));

        var (width, height) = FinderGallery.FitPreview(0, 0, 100, 50);
        Assert.True(width > 0 && height > 0);   // 可用区域未布局时也给一个正尺寸，不产生 NaN
    }
}
