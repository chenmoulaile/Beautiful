using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>关闭退场动画的数学测试：端点错 = 消失瞬间闪一下或缩放方向反了；NaN 漏出去 = GhostLayer 全屏炸帧。</summary>
public class CloseAnimMathTests
{
    [Fact]
    public void 端点_t0_是原样_t1_是终态消失()
    {
        var start = CloseAnimMath.Sample(0.0);
        Assert.Equal(1.0, start.Scale, 9);
        Assert.Equal(1.0, start.Opacity, 9);

        var end = CloseAnimMath.Sample(1.0);
        Assert.Equal(0.92, end.Scale, 9);
        Assert.Equal(0.0, end.Opacity, 9);
    }

    [Fact]
    public void 缩放与不透明度随进度单调()
    {
        var prev = CloseAnimMath.Sample(0.0);
        for (var i = 1; i <= 100; i++)
        {
            var t = i / 100.0;
            var f = CloseAnimMath.Sample(t);
            Assert.True(f.Scale <= prev.Scale + 1e-9, $"t={t}: 缩放 {prev.Scale}→{f.Scale} 非单调减");
            Assert.True(f.Opacity <= prev.Opacity + 1e-9, $"t={t}: 不透明度 {prev.Opacity}→{f.Opacity} 非单调减");
            prev = f;
        }
    }

    [Fact]
    public void easeInQuad_形态_中点缩放大半透明过半()
    {
        // easeInQuad：t=0.5 → e=0.25。退场"开头平缓、消失干脆"的具体落点：
        // 一半时间时画面还剩 75% 缩放 / 75% 不透明度，最后 1/4 时间才完成消失。
        var mid = CloseAnimMath.Sample(0.5);
        Assert.Equal(1.0 - 0.25 * 0.08, mid.Scale, 9);
        Assert.Equal(0.75, mid.Opacity, 9);
    }

    [Theory]
    [InlineData(-0.5)]   // 越界进度钳为 0
    [InlineData(1.5)]    // 越界进度钳为 1
    [InlineData(double.NaN)]
    public void 非法进度不炸_按端点处理(double t)
    {
        var f = CloseAnimMath.Sample(t);
        Assert.InRange(f.Scale, 0.9, 1.0);
        Assert.InRange(f.Opacity, 0.0, 1.0);
    }

    [Fact]
    public void 终态缩放钳制在合法区间()
    {
        // 手改 anim-params.json 写出荒谬值：只能收敛，不能放大或倒放
        Assert.Equal(0.5, CloseAnimMath.Sample(1.0, 0.1).Scale, 9);      // 下限 0.5
        Assert.Equal(1.0, CloseAnimMath.Sample(1.0, 2.0).Scale, 9);      // 上限 1.0
        Assert.Equal(0.92, CloseAnimMath.Sample(1.0, double.NaN).Scale, 9);   // NaN 落默认
        Assert.Equal(0.92, CloseAnimMath.Sample(1.0, double.PositiveInfinity).Scale, 9);
    }

    [Fact]
    public void 归一化矩形_居中缩放_与画布无关()
    {
        // GhostLayer 口径：条带坐标 = 归一化值 × 画布尺寸，"居中缩到 s"恒为 ((1-s)/2, (1-s)/2, s, s)
        var r = CloseAnimMath.RectFor(0.92);
        Assert.Equal(0.04, r.X, 9);
        Assert.Equal(0.04, r.Y, 9);
        Assert.Equal(0.92, r.Width, 9);
        Assert.Equal(0.92, r.Height, 9);
    }

    [Fact]
    public void 归一化矩形_scale1_等于满画布()
    {
        var r = CloseAnimMath.RectFor(1.0);
        Assert.Equal(0.0, r.X, 9);
        Assert.Equal(0.0, r.Y, 9);
        Assert.Equal(1.0, r.Width, 9);
        Assert.Equal(1.0, r.Height, 9);
    }

    [Fact]
    public void 归一化矩形_荒谬缩放钳回合法区间()
    {
        var lo = CloseAnimMath.RectFor(0.01);   // 下限 0.05
        Assert.Equal(0.05, lo.Width, 9);
        Assert.Equal((1.0 - 0.05) / 2, lo.X, 9);
        var hi = CloseAnimMath.RectFor(9.0);    // 上限 1.0
        Assert.Equal(1.0, hi.Width, 9);
    }
}
