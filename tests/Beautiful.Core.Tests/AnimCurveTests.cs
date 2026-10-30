using System;
using System.Collections.Generic;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>曲线与帧统计的测试：端点错 = 动画起止闪一下；NaN 漏出去 = 整块网格画没。</summary>
public class AnimCurveTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void 所有曲线端点都是0到1(double t)
    {
        if (t == 0.0)
        {
            Assert.Equal(0.0, AnimCurve.EaseOutQuint(0.0), 9);
            Assert.Equal(0.0, AnimCurve.EaseInOutQuad(0.0), 9);
            Assert.Equal(0.0, AnimCurve.EaseOutQuad(0.0), 9);
            Assert.Equal(0.0, AnimCurve.EaseInQuad(0.0), 9);
        }
        else
        {
            Assert.Equal(1.0, AnimCurve.EaseOutQuint(1.0), 9);
            Assert.Equal(1.0, AnimCurve.EaseInOutQuad(1.0), 9);
            Assert.Equal(1.0, AnimCurve.EaseOutQuad(1.0), 9);
            Assert.Equal(1.0, AnimCurve.EaseInQuad(1.0), 9);
        }
    }

    [Fact]
    public void 全部曲线单调不减()
    {
        foreach (var curve in new Func<double, double>[]
                 {
                     AnimCurve.EaseOutQuint, AnimCurve.EaseInOutQuad, AnimCurve.EaseOutQuad, AnimCurve.EaseInQuad,
                 })
        {
            var prev = 0.0;
            for (var i = 0; i <= 100; i++)
            {
                var v = curve(i / 100.0);
                Assert.True(v >= prev - 1e-9, "曲线在某处回落");
                prev = v;
            }
        }
    }

    [Fact]
    public void 打开曲线是快起慢收()
    {
        // easeOutQuint 在 20% 时间就走过一半以上路程，"落位"感来自这里
        Assert.True(AnimCurve.EaseOutQuint(0.2) > 0.6);
    }

    [Fact]
    public void 关闭曲线是慢起快收()
    {
        Assert.True(AnimCurve.EaseInQuad(0.5) < 0.3);
    }

    [Fact]
    public void 形变曲线中点是半程()
    {
        Assert.Equal(0.5, AnimCurve.EaseInOutQuad(0.5), 6);
    }

    [Fact]
    public void 越界进度被钳住()
    {
        Assert.Equal(0.0, AnimCurve.EaseOutQuint(-3), 9);
        Assert.Equal(1.0, AnimCurve.EaseOutQuint(3), 9);
    }

    [Fact]
    public void NaN不会漏进渲染()
    {
        Assert.Equal(0.0, AnimCurve.Clamp01(double.NaN));
        Assert.Equal(1.0, AnimCurve.Progress(double.NaN, 500));
    }

    [Fact]
    public void 零时长直接完成()
    {
        Assert.Equal(1.0, AnimCurve.Progress(10, 0));
        Assert.Equal(1.0, AnimCurve.Progress(10, -5));
    }

    [Fact]
    public void 按名字取曲线()
    {
        Assert.Equal(AnimCurve.EaseOutQuint(0.3), AnimCurve.ByName("easeOutQuint")(0.3), 9);
        Assert.Equal(0.4, AnimCurve.ByName("没有这个曲线")(0.4), 9);   // 未知回落到恒等
        Assert.Equal(AnimCurve.EaseInOutQuad(0.7), AnimCurve.ByName("morph")(0.7), 9);
    }

    [Fact]
    public void 帧统计的分位数用最近秩法()
    {
        // 1..100 的帧间隔：P50=50，P95=95
        var intervals = new List<double>();
        for (var i = 1; i <= 100; i++) intervals.Add(i);
        var stats = FrameStats.FromIntervals(intervals);
        Assert.Equal(100, stats.Frames);
        Assert.Equal(50, stats.P50, 6);
        Assert.Equal(95, stats.P95, 6);
        Assert.Equal(100, stats.Max, 6);
    }

    [Fact]
    public void 空帧序列返回空统计而不是抛()
    {
        var stats = FrameStats.FromIntervals(Array.Empty<double>());
        Assert.Equal(0, stats.Frames);
        Assert.Equal(0, stats.P95, 6);
        Assert.Equal(0, FrameStats.FromIntervals(null).Frames);
    }
}
