using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// genie 几何纯函数测试。
/// <para>
/// "跑起来像不像 macOS"没法单测，但下面这些是它的<b>前提</b>：起点必须精确等于源窗口、终点必须真的
/// 塌到目标点、过程中带与带不许交叉（交叉=画面翻面）、越界的计时不许改变形状。
/// 渲染侧每帧只做"归一化矩形 × 源宽高"，所以这些不变量一旦锁住，观感问题只可能是参数而不是 bug。
/// </para>
/// </summary>
public class GenieMathTests
{
    private const int Slices = GenieMath.DefaultSlices;

    private static List<(int Index, GenieRect Rect, double Opacity)> Sample(
        double t, int slices = Slices, double lag = GenieMath.DefaultLag,
        double targetX = 0.5, double targetY = 1.6, double endWidth = 0.12)
        => GenieMath.Sample(t, slices, lag, targetX, targetY, endWidth);

    // ---------- 端点 ----------

    [Fact]
    public void 起点每条带精确等于源窗口里的原始位置()
    {
        var bands = Sample(0);
        Assert.Equal(Slices, bands.Count);
        for (var i = 0; i < Slices; i++)
        {
            var (index, rect, opacity) = bands[i];
            Assert.Equal(i, index);
            Assert.Equal(0.0, rect.X, 9);
            Assert.Equal(i / (double)Slices, rect.Y, 9);
            Assert.Equal(1.0, rect.Width, 9);
            Assert.Equal(1.0 / Slices, rect.Height, 9);
            Assert.Equal(1.0, opacity, 9);
        }
    }

    [Fact]
    public void 终点全部塌到目标点_高度归零且宽度收到目标图标宽()
    {
        const double targetX = 0.42, targetY = 1.6, endWidth = 0.12;
        var bands = Sample(1, targetX: targetX, targetY: targetY, endWidth: endWidth);

        foreach ((_, var rect, var opacity) in bands)
        {
            Assert.Equal(targetY, rect.Y, 9);
            Assert.Equal(0.0, rect.Height, 9);
            Assert.Equal(endWidth, rect.Width, 9);
            Assert.Equal(targetX - endWidth / 2, rect.X, 9);
            Assert.Equal(0.0, opacity, 9);   // 收尾必须淡干净，否则最后一帧是硬边
        }
    }

    // ---------- 过程 ----------

    [Fact]
    public void 随进度推进_每条带只向下移动且只变矮()
    {
        var previous = Sample(0);
        for (var step = 1; step <= 20; step++)
        {
            var current = Sample(step / 20.0);
            for (var i = 0; i < Slices; i++)
            {
                Assert.True(current[i].Rect.Y >= previous[i].Rect.Y - 1e-9, $"带 {i} 在 t={step / 20.0} 往回走了");
                Assert.True(current[i].Rect.Height <= previous[i].Rect.Height + 1e-9, $"带 {i} 在 t={step / 20.0} 长高了");
                Assert.True(current[i].Rect.Width <= previous[i].Rect.Width + 1e-9);
            }
            previous = current;
        }
    }

    [Fact]
    public void 带与带不许交叉_否则画面会翻面()
    {
        foreach (var t in new[] { 0.0, 0.15, 0.3, 0.45, 0.6, 0.75, 0.9, 1.0 })
        {
            var bands = Sample(t);
            for (var i = 1; i < bands.Count; i++)
                Assert.True(bands[i].Rect.Y >= bands[i - 1].Rect.Y - 1e-9,
                    $"t={t} 时带 {i} 越到了带 {i - 1} 上面");
        }
    }

    [Fact]
    public void 靠近目标的底带必须先于顶带动起来()
    {
        foreach (var t in new[] { 0.1, 0.25, 0.5, 0.75 })
        {
            var top = GenieMath.SliceProgress(t, 0, Slices);
            var bottom = GenieMath.SliceProgress(t, Slices - 1, Slices);
            Assert.True(bottom >= top, $"t={t} 时顶带反而跑在前面");
            Assert.Equal(0.0, GenieMath.SliceProgress(0, 0, Slices));
        }
    }

    [Fact]
    public void 局部进度两端分别是零与一()
    {
        for (var i = 0; i < Slices; i++)
        {
            Assert.Equal(0.0, GenieMath.SliceProgress(0.0, i, Slices), 9);
            Assert.Equal(1.0, GenieMath.SliceProgress(1.0, i, Slices), 9);
        }
    }

    // ---------- 钳制与退化 ----------

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.001)]
    public void 负进度等同于零(double t)
    {
        var a = Sample(t);
        var b = Sample(0);
        Assert.Equal(a.Select(x => x.Rect), b.Select(x => x.Rect));
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(9.0)]
    public void 越上界的进度等同于终点(double t)
    {
        var a = Sample(t);
        var b = Sample(1);
        Assert.Equal(a.Select(x => x.Rect), b.Select(x => x.Rect));
    }

    [Fact]
    public void 带数退化到一也不会崩()
    {
        var bands = Sample(0.5, slices: 1);
        Assert.Single(bands);
        Assert.True(bands[0].Rect.Height >= 0);
    }

    [Fact]
    public void 滞后为零时所有带同步_仍然是一条不交叉的收拢()
    {
        var bands = Sample(0.4, lag: 0);
        var first = bands[0].Rect;
        var last = bands[^1].Rect;
        Assert.True(last.Y >= first.Y);
        Assert.Equal(bands[0].Opacity, bands[^1].Opacity, 9);
    }

    [Fact]
    public void 同一进度重复采样结果一致_说明没有藏状态()
    {
        Assert.Equal(Sample(0.37).Select(x => x.Rect), Sample(0.37).Select(x => x.Rect));
    }

    // ---------- 缓动与缩放 ----------

    [Fact]
    public void 缓动端点与单调性()
    {
        Assert.Equal(0.0, GenieMath.EaseOut(0), 9);
        Assert.Equal(1.0, GenieMath.EaseOut(1), 9);
        Assert.True(GenieMath.EaseOut(0.5) > 0.5);   // ease-out：前半段快过线性
        var prev = -1.0;
        for (var t = 0.0; t <= 1.0001; t += 0.05)
        {
            var v = GenieMath.EaseOut(t);
            Assert.True(v >= prev);
            prev = v;
        }
    }

    [Fact]
    public void 缩放动画两端分别是源矩形与目标矩形()
    {
        var from = new GenieRect(0, 0, 1, 1);
        var to = new GenieRect(0.2, 0.3, 0.5, 0.4);

        var a = GenieMath.ZoomLerp(from, to, 0);
        var b = GenieMath.ZoomLerp(from, to, 1);
        Assert.Equal(from, a);
        Assert.Equal(to, b);
    }

    [Fact]
    public void 钳制函数只认零一区间()
    {
        Assert.Equal(0.0, GenieMath.Clamp01(-5));
        Assert.Equal(0.0, GenieMath.Clamp01(0));
        Assert.Equal(0.5, GenieMath.Clamp01(0.5));
        Assert.Equal(1.0, GenieMath.Clamp01(5));
    }
}
