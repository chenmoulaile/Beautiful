using System;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 放大行逐帧几何测试（Task 6 评审发现 #2 的几何证明）。
/// <para>
/// 场景与 DockSurface 一致：容器 <c>RenderTransformOrigin=(0.5,1)</c> → 真实像素矩形 =
/// <c>[layout_i - extra_i/2, layout_i + width_i·scale_i - extra_i/2]</c>，
/// 其中 <c>layout_i</c> 是 <c>Canvas.Left</c> 的写入值、<c>extra_i = (scale_i-1)·width_i</c>。
/// 本文件证明 drawn 坐标（<c>MagnificationGeometry.ComputeRowLefts</c> 的第一个输出数组）满足三条：
/// 相邻像素间距恒等于原 spacing、整行不溢出自己宽度、整行在背板里居中。
/// </para>
/// </summary>
public class MagnificationGeometryTests
{
    private const double Spacing = 6;
    private const double BaseSize = 44;

    /// <summary>悬停态样本：中间项 1.8×，两侧 1.2×/1.05×（正是"上升沿挤压、下降沿撑开"最明显的形状）。</summary>
    private static readonly double[] Scales = { 1.0, 1.05, 1.2, 1.8, 1.2, 1.05, 1.0 };

    private static double[] BaseLefts(int n)
    {
        var arr = new double[n];
        for (var i = 0; i < n; i++) arr[i] = i * (BaseSize + Spacing);
        return arr;
    }

    private static (double[] Drawn, double[] Layout, double TotalExtra) Row(bool centerRow, double[]? scales = null)
    {
        var s = scales ?? Scales;
        var n = s.Length;
        var widths = new double[n];
        Array.Fill(widths, BaseSize);
        var drawn = new double[n];
        var layout = new double[n];
        var totalExtra = MagnificationGeometry.ComputeRowLefts(BaseLefts(n), widths, s, centerRow, drawn, layout);
        return (drawn, layout, totalExtra);
    }

    /// <summary>像素右缘：drawn_i + width_i·scale_i（缩放原点 0.5 时，layout + extra/2 的修正正好抵消）。</summary>
    private static double DrawnRight(double[] drawn, int i, double scale) => drawn[i] + BaseSize * scale;

    [Fact]
    public void 放大态下相邻像素间距恒等于原间距()
    {
        var (drawn, _, _) = Row(centerRow: false);
        for (var i = 0; i + 1 < drawn.Length; i++)
        {
            var gap = drawn[i + 1] - DrawnRight(drawn, i, Scales[i]);
            Assert.True(Math.Abs(gap - Spacing) < 1e-9,
                $"第 {i}→{i + 1} 项画出间距 {gap:F6} ≠ spacing {Spacing}（旧实现会差 (extra_i-extra_{i + 1})/2）");
        }
    }

    [Fact]
    public void 面板宽度固定时整行以自身中心外扩且间距依旧均匀()
    {
        var (drawn, _, totalExtra) = Row(centerRow: true);
        for (var i = 0; i + 1 < drawn.Length; i++)
            Assert.True(Math.Abs((drawn[i + 1] - DrawnRight(drawn, i, Scales[i])) - Spacing) < 1e-9);

        var n = drawn.Length;
        var natural = n * BaseSize + (n - 1) * Spacing;
        // 中心不变：未放大中心 == 放大后 drawn 区间中心
        var grown = (drawn[0] + DrawnRight(drawn, n - 1, Scales[n - 1])) / 2.0;
        Assert.True(Math.Abs(grown - natural / 2.0) < 1e-9, $"行中心漂移：{grown} vs {natural / 2.0}");
        // 左右各让出总增量的一半
        Assert.True(Math.Abs(drawn[0] + totalExtra / 2.0) < 1e-9, $"首项左移量应为 {totalExtra / 2.0}，实际 {-drawn[0]}");
    }

    [Fact]
    public void 行宽与上报总宽严格一致且底板不偏心()
    {
        var n = Scales.Length;
        var natural = n * BaseSize + (n - 1) * Spacing;
        var (drawn, _, totalExtra) = Row(centerRow: false);
        // AutoWidth 下 DockSurface 上报的期望宽度 = natural + totalExtra：
        // drawn 区间必须正好铺满它，左缘为 0（首项不再溢出圆角面板），右缘不超出（整行在背板里居中）。
        Assert.True(Math.Abs(drawn[0]) < 1e-9, $"首项绘制左缘应为 0，实际 {drawn[0]}");
        var rowRight = DrawnRight(drawn, n - 1, Scales[n - 1]);
        Assert.True(Math.Abs(rowRight - (natural + totalExtra)) < 1e-9,
            $"行右缘 {rowRight:F4} 应等于上报宽度 {natural + totalExtra:F4}（旧写法多算 extra_last/2 → 行在自己的背板里偏心）");
    }

    [Fact]
    public void 布局左缘比绘制左缘多出自身增量的一半()
    {
        var (drawn, layout, _) = Row(centerRow: false);
        for (var i = 0; i < drawn.Length; i++)
        {
            var extra = (Scales[i] - 1.0) * BaseSize;
            Assert.True(Math.Abs(layout[i] - (drawn[i] + extra / 2.0)) < 1e-9,
                $"第 {i} 项 layout 应为 drawn+extra/2={drawn[i] + extra / 2.0}，实际 {layout[i]}");
        }
    }

    [Fact]
    public void 隐藏槽宽度为零时不留空隙也不参与前缀和()
    {
        // 中间项收起（胶囊 Width=0）：RecomputeBaseLayout 给它 base=50、width=0 →
        // extra 恒为 0，第三项仍紧贴第一项的 spacing，不产生"幽灵鼓包"。
        var scales = new double[] { 1.0, 1.8, 1.2 };
        var widths = new double[] { BaseSize, 0.0, BaseSize };
        var baseLefts = new double[] { 0.0, 50.0, 50.0 };
        var drawn = new double[3];
        var layout = new double[3];
        var totalExtra = MagnificationGeometry.ComputeRowLefts(baseLefts, widths, scales, false, drawn, layout);

        Assert.Equal((1.2 - 1.0) * BaseSize, totalExtra, 6);          // 零宽项即使 scale=1.8 也不贡献额外宽度
        Assert.Equal(0.0, drawn[0], 9);
        Assert.Equal(BaseSize + Spacing, drawn[2], 6);                // 第一项右缘 + spacing
        Assert.Equal(50.0, drawn[1], 9);
        Assert.True(Math.Abs(layout[1] - drawn[1]) < 1e-9, "零宽项的 layout 与 drawn 必须重合（无额外宽度可让）");
    }

    [Fact]
    public void 缓冲区长度不足时抛出参数异常()
    {
        Assert.Throws<ArgumentException>(() => MagnificationGeometry.ComputeRowLefts(
            BaseLefts(3), new double[] { 44, 44 }, new double[] { 1, 1, 1 }, false, new double[3], new double[3]));
        Assert.Throws<ArgumentException>(() => MagnificationGeometry.ComputeRowLefts(
            BaseLefts(3), new double[] { 44, 44, 44 }, new double[] { 1, 1 }, false, new double[3], new double[3]));
        Assert.Throws<ArgumentException>(() => MagnificationGeometry.ComputeRowLefts(
            BaseLefts(3), new double[] { 44, 44, 44 }, new double[] { 1, 1, 1 }, false, new double[2], new double[3]));
        Assert.Throws<ArgumentException>(() => MagnificationGeometry.ComputeRowLefts(
            BaseLefts(3), new double[] { 44, 44, 44 }, new double[] { 1, 1, 1 }, false, new double[3], new double[2]));
    }

    [Fact]
    public void 空行返回零增量且不写缓冲区()
    {
        var drawn = new double[0];
        var layout = new double[0];
        Assert.Equal(0.0, MagnificationGeometry.ComputeRowLefts(
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), true, drawn, layout));
    }
}
