using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 关闭动画几何的测试：与真实窗口的衔接帧（t=0 全尺寸不透明）、离场终帧（缩到 endScale 全透明）、
/// 缩放与透明度必须同步收紧（两层运动会拆台）、NaN 不漏进渲染。
/// </summary>
public class CloseAnimMathTests
{
    private const double EndScale = 0.92;

    [Fact]
    public void 起始帧与真实窗口完全重合且不透明()
    {
        var f = CloseAnimMath.Start(EndScale);
        Assert.Equal(1.0, f.Opacity, 9);
        Assert.Equal(1.0, f.Rect.Width, 9);
        Assert.Equal(0.0, f.Rect.X, 9);
        Assert.Equal(0.0, f.Rect.Y, 9);
    }

    [Fact]
    public void 终帧缩到终点且全透明()
    {
        var f = CloseAnimMath.End(EndScale);
        Assert.Equal(0.0, f.Opacity, 9);
        Assert.Equal(EndScale, f.Rect.Width, 9);
    }

    [Fact]
    public void 矩形始终居中于源窗口()
    {
        for (var i = 0; i <= 20; i++)
        {
            var f = CloseAnimMath.Frame(i / 20.0, EndScale);
            var half = (1.0 - f.Rect.Width) / 2.0;
            Assert.Equal(half, f.Rect.X, 9);
            Assert.Equal(half, f.Rect.Y, 9);
            Assert.Equal(f.Rect.Width, f.Rect.Height, 9);
        }
    }

    [Fact]
    public void 缩放与透明度同步收紧_不拆成两层运动()
    {
        // 同一条被缓动的进度驱动两者：透明度下降量 == 缩放完成量（同一个 e）
        for (var i = 0; i <= 100; i++)
        {
            var f = CloseAnimMath.Frame(i / 100.0, EndScale);
            var e = 1.0 - f.Opacity;
            var scaleDone = (1.0 - f.Rect.Width) / (1.0 - EndScale);
            Assert.Equal(e, scaleDone, 9);
        }
    }

    [Fact]
    public void 缩放单调减透明度单调减()
    {
        var prevScale = 1.0;
        var prevOpacity = 1.0;
        for (var i = 0; i <= 100; i++)
        {
            var f = CloseAnimMath.Frame(i / 100.0, EndScale);
            Assert.True(f.Rect.Width <= prevScale + 1e-9, "缩放在某处回涨");
            Assert.True(f.Opacity <= prevOpacity + 1e-9, "透明度在某处回涨");
            prevScale = f.Rect.Width;
            prevOpacity = f.Opacity;
        }
    }

    [Fact]
    public void 关闭曲线是慢起快收()
    {
        // easeInQuad：半程时缩放还没走到 30%，"离开"的加速度在末尾
        Assert.True(CloseAnimMath.Frame(0.5, EndScale).Rect.Width > 1.0 - (1.0 - EndScale) * 0.3);
    }

    [Theory]
    [InlineData(-3.0)]
    [InlineData(3.0)]
    public void 越界进度被钳到端点(double t)
    {
        var expected = t < 0 ? CloseAnimMath.Start(EndScale) : CloseAnimMath.End(EndScale);
        var actual = CloseAnimMath.Frame(t, EndScale);
        Assert.Equal(expected.Rect.Width, actual.Rect.Width, 9);
        Assert.Equal(expected.Opacity, actual.Opacity, 9);
    }

    [Fact]
    public void NaN不漏进渲染()
    {
        var f = CloseAnimMath.Frame(double.NaN, EndScale);
        Assert.False(double.IsNaN(f.Rect.X) || double.IsNaN(f.Rect.Width) || double.IsNaN(f.Opacity));
        var bad = CloseAnimMath.Frame(0.5, double.NaN);
        Assert.Equal(1.0, bad.Rect.Width, 9);   // 终点缩放非法 → 退化为纯淡出
    }

    [Theory]
    [InlineData(0.1)]    // 过小 → 钳回 0.5
    [InlineData(1.5)]    // 过大 → 钳回 1.0（动画退化为纯淡出，这是合法降级）
    public void 终点缩放越界被钳住(double endScale)
    {
        var f = CloseAnimMath.End(endScale);
        Assert.InRange(f.Rect.Width, CloseAnimMath.MinEndScale, 1.0);
    }
}
