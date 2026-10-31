using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 打开动画几何的测试：端点错 = 起播闪一下/收尾闪一下；矩形不居中 = 窗口"从左上角长出来"；
/// NaN 漏出去 = 整块替身画没。占位帧设计与动画起点共用 t=0，所以起始帧的<b>透明</b>也锁在测试里。
/// </summary>
public class OpenAnimMathTests
{
    private const double StartScale = 0.94;

    [Fact]
    public void 起始帧是透明且缩到起点的()
    {
        var f = OpenAnimMath.Start(StartScale);
        Assert.Equal(0.0, f.Opacity, 9);
        Assert.Equal(StartScale, f.Rect.Width, 9);
        Assert.Equal(StartScale, f.Rect.Height, 9);
    }

    [Fact]
    public void 终帧与真实窗口完全重合且不透明()
    {
        var f = OpenAnimMath.End(StartScale);
        Assert.Equal(1.0, f.Opacity, 9);
        Assert.Equal(1.0, f.Rect.Width, 9);
        Assert.Equal(0.0, f.Rect.X, 9);
        Assert.Equal(0.0, f.Rect.Y, 9);
    }

    [Fact]
    public void 矩形始终居中于源窗口()
    {
        // 窗口从中心长出来：X == Y == (1-scale)/2
        for (var i = 0; i <= 20; i++)
        {
            var f = OpenAnimMath.Frame(i / 20.0, StartScale);
            var half = (1.0 - f.Rect.Width) / 2.0;
            Assert.Equal(half, f.Rect.X, 9);
            Assert.Equal(half, f.Rect.Y, 9);
            Assert.Equal(f.Rect.Width, f.Rect.Height, 9);
        }
    }

    [Fact]
    public void 缩放与透明度单调不减()
    {
        var prevScale = 0.0;
        var prevOpacity = 0.0;
        for (var i = 0; i <= 100; i++)
        {
            var f = OpenAnimMath.Frame(i / 100.0, StartScale);
            Assert.True(f.Rect.Width >= prevScale - 1e-9, "缩放在某处回落");
            Assert.True(f.Opacity >= prevOpacity - 1e-9, "透明度在某处回落");
            prevScale = f.Rect.Width;
            prevOpacity = f.Opacity;
        }
    }

    [Fact]
    public void 透明度在前段完成落位全程不透明()
    {
        // FadeShare=0.45：45% 进度处淡入已收口，之后是纯粹的缩放
        Assert.Equal(1.0, OpenAnimMath.Frame(OpenAnimMath.FadeShare, StartScale).Opacity, 9);
        Assert.True(OpenAnimMath.Frame(0.2, StartScale).Opacity < 1.0, "淡入应是快速浮现而非瞬间出现");
    }

    [Fact]
    public void 起点缩放在200ms内走得过半()
    {
        // easeOutQuint：20% 时间走过一半以上——"落位"不仓促的来源
        Assert.True(OpenAnimMath.Frame(0.2, StartScale).Rect.Width > (StartScale + 1.0) / 2.0);
    }

    [Theory]
    [InlineData(-3.0)]
    [InlineData(3.0)]
    public void 越界进度被钳到端点(double t)
    {
        var expected = t < 0 ? OpenAnimMath.Start(StartScale) : OpenAnimMath.End(StartScale);
        var actual = OpenAnimMath.Frame(t, StartScale);
        Assert.Equal(expected.Rect.Width, actual.Rect.Width, 9);
        Assert.Equal(expected.Opacity, actual.Opacity, 9);
    }

    [Fact]
    public void NaN不漏进渲染()
    {
        var f = OpenAnimMath.Frame(double.NaN, StartScale);
        Assert.False(double.IsNaN(f.Rect.X) || double.IsNaN(f.Rect.Width) || double.IsNaN(f.Opacity));
        var bad = OpenAnimMath.Frame(0.5, double.NaN);
        Assert.Equal(1.0, bad.Rect.Width, 9);   // 起点缩放非法 → 退化为不缩放（宁可没有动画，不可画没）
    }

    [Theory]
    [InlineData(0.1)]    // 过小 → 钳回 0.5（与 AnimParams 规范化同区间）
    [InlineData(1.5)]    // 过大 → 钳回 1.0（起点即全尺寸，动画退化为纯淡入）
    public void 起点缩放越界被钳住(double startScale)
    {
        var f = OpenAnimMath.Start(startScale);
        Assert.InRange(f.Rect.Width, OpenAnimMath.MinStartScale, 1.0);
    }
}
