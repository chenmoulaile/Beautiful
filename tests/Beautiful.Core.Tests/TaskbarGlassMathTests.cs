using Beautiful.Core.Layout;
using Xunit;

/// <summary>
/// "任务栏玻璃壳"的几何纯函数测试。
/// <para>
/// 这个功能的红线是<b>只美化、不接管</b>：装饰必须严格贴合原生任务栏的实际矩形，
/// 而且只在它真的贴着某条边时才画（浮在中间时画圆角会画出一条悬空的高光）。
/// 所以这里锁的是"什么时候画、往哪一侧收口"，而不是颜色。
/// </para>
/// </summary>
public class TaskbarGlassMathTests
{
    private static readonly PhysicalRect Monitor = new(0, 0, 2880, 1920);
    private static readonly PhysicalRect BottomBar = new(0, 1824, 2880, 1920);

    // ---------- 该不该画 ----------

    [Fact]
    public void 正常贴底任务栏可装饰() => Assert.True(TaskbarGlassMath.IsDecoratable(BottomBar, Monitor));

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 1920, 2880, 1920)]     // 零高度
    [InlineData(0, 1900, 2880, 1800)]     // 负高度
    public void 非法尺寸不可装饰(int l, int t, int r, int b)
        => Assert.False(TaskbarGlassMath.IsDecoratable(new PhysicalRect(l, t, r, b), Monitor));

    [Fact]
    public void shell哨兵位置不可装饰()
        => Assert.False(TaskbarGlassMath.IsDecoratable(new PhysicalRect(-32000, -32000, -31640, -31952), Monitor));

    [Fact]
    public void 高得不可能是任务栏的矩形不可装饰()
        => Assert.False(TaskbarGlassMath.IsDecoratable(new PhysicalRect(0, 960, 2880, 1920), Monitor));

    [Fact]
    public void 显示器尺寸非法时一律不画()
    {
        Assert.False(TaskbarGlassMath.IsDecoratable(BottomBar, new PhysicalRect(0, 0, 0, 0)));
        Assert.False(TaskbarGlassMath.IsDecoratable(BottomBar, new PhysicalRect(0, 0, 100, -50)));
    }

    // ---------- 贴哪条边 ----------

    [Fact]
    public void 四条边的识别各自正确()
    {
        Assert.Equal(GlassEdge.Bottom, TaskbarGlassMath.DetectEdge(new PhysicalRect(0, 1824, 2880, 1920), Monitor));
        Assert.Equal(GlassEdge.Top, TaskbarGlassMath.DetectEdge(new PhysicalRect(0, 0, 2880, 96), Monitor));
        Assert.Equal(GlassEdge.Left, TaskbarGlassMath.DetectEdge(new PhysicalRect(0, 0, 96, 1920), Monitor));
        Assert.Equal(GlassEdge.Right, TaskbarGlassMath.DetectEdge(new PhysicalRect(2784, 0, 2880, 1920), Monitor));
    }

    [Fact]
    public void 一两个像素的落位误差不改判定()
        => Assert.Equal(GlassEdge.Bottom, TaskbarGlassMath.DetectEdge(new PhysicalRect(0, 1823, 2880, 1921), Monitor));

    [Fact]
    public void 浮在屏幕中间时判未知_不猜一条边出来()
        => Assert.Equal(GlassEdge.Unknown, TaskbarGlassMath.DetectEdge(new PhysicalRect(400, 800, 2400, 900), Monitor));

    [Fact]
    public void 副屏原点非零时按该屏自己的边判()
    {
        var second = new PhysicalRect(-2880, 0, 0, 1920);
        var bar = new PhysicalRect(-2880, 1824, 0, 1920);
        Assert.Equal(GlassEdge.Bottom, TaskbarGlassMath.DetectEdge(bar, second));
        Assert.True(TaskbarGlassMath.IsDecoratable(bar, second));
    }

    // ---------- 圆角与线宽 ----------

    [Fact]
    public void 圆角不超过短边的一半()
    {
        Assert.Equal(12, TaskbarGlassMath.CornerRadius(2880, 96), 3);
        Assert.Equal(10, TaskbarGlassMath.CornerRadius(2880, 20), 3);   // 被 20/2 钳住
        Assert.Equal(0, TaskbarGlassMath.CornerRadius(0, 96), 3);
        Assert.Equal(0, TaskbarGlassMath.CornerRadius(2880, -1), 3);
    }

    [Fact]
    public void 线宽随DPI放大且在极薄任务栏上封顶()
    {
        Assert.Equal(1, TaskbarGlassMath.EdgeThickness(1.0, 96, TaskbarGlassMath.PreferredHighlightPx), 3);
        Assert.Equal(2, TaskbarGlassMath.EdgeThickness(2.0, 96, TaskbarGlassMath.PreferredHighlightPx), 3);
        Assert.Equal(4, TaskbarGlassMath.EdgeThickness(2.0, 16, TaskbarGlassMath.PreferredHairlinePx), 3); // 16/4 封顶
        Assert.Equal(0, TaskbarGlassMath.EdgeThickness(2.0, 0, TaskbarGlassMath.PreferredHairlinePx), 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void 非法DPI按一比一处理而不是算出负宽度(double scale)
    {
        var thickness = TaskbarGlassMath.EdgeThickness(scale, 96, TaskbarGlassMath.PreferredHairlinePx);
        Assert.True(thickness >= 0);
        Assert.Equal(2, thickness, 3);
    }

    // ---------- 收口方向 ----------

    [Fact]
    public void 贴底时只有上方两角是圆的()
    {
        var (tl, tr, bl, br) = TaskbarGlassMath.RoundedCorners(GlassEdge.Bottom);
        Assert.True(tl);
        Assert.True(tr);
        Assert.False(bl);
        Assert.False(br);
    }

    [Fact]
    public void 贴左时只有右侧两角是圆的()
    {
        var (tl, tr, bl, br) = TaskbarGlassMath.RoundedCorners(GlassEdge.Left);
        Assert.False(tl);
        Assert.True(tr);
        Assert.False(bl);
        Assert.True(br);
    }

    [Fact]
    public void 判不出边时一个角都不圆()
    {
        var (tl, tr, bl, br) = TaskbarGlassMath.RoundedCorners(GlassEdge.Unknown);
        Assert.False(tl || tr || bl || br);
    }
}
