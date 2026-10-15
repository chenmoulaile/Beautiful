using System;
using System.Collections.Generic;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 分隔符参与的行基准排布（<see cref="DockRowLayout"/>）+ 逐帧左缘（<see cref="MagnificationGeometry"/>）。
/// 两条原生语义在这里被钉死：分隔符**向行贡献宽度**，却**不进放大场**（缩放恒 1）。
/// </summary>
public class DockRowLayoutTests
{
    private const double Spacing = 6;
    private const double Icon = 44;
    private const double SeparatorBox = 11;    // 线粗 1 + 两侧留白各 5
    private const double MinVisible = 0.5;

    private static DockBaseLayout Run(
        IReadOnlyList<double> widths, IReadOnlyList<bool> magnifiable,
        double[] baseLefts, double[] centers, int[] map)
        => DockRowLayout.Compute(widths, magnifiable, Spacing, MinVisible, baseLefts, centers, map);

    [Fact]
    public void 分隔符占位宽但不进放大场_中心与下标映射都跳过它()
    {
        // [图标 44][分隔符 11][图标 44]：行宽含分隔符，场里却只有两个图标
        var widths = new[] { Icon, SeparatorBox, Icon };
        var magnifiable = new[] { true, false, true };
        var baseLefts = new double[3];
        var centers = new double[3];
        var map = new int[3];

        var layout = Run(widths, magnifiable, baseLefts, centers, map);

        Assert.Equal(2, layout.FieldCount);          // 分隔符不入场
        Assert.Equal(3, layout.VisibleCount);         // 三条都占位
        Assert.Equal(44 + Spacing + SeparatorBox + Spacing + 44, layout.NaturalWidth, 6);
        Assert.Equal(new[] { 0.0, 50.0, 67.0 }, baseLefts);
        Assert.Equal(new[] { 22.0, 89.0 }, new[] { centers[0], centers[1] });
        Assert.Equal(new[] { 0, 2 }, new[] { map[0], map[1] });   // 下标 1（分隔符）不在映射里
    }

    [Fact]
    public void 全是分隔符的行没有放大目标()
    {
        var widths = new[] { SeparatorBox, SeparatorBox };
        var baseLefts = new double[2];
        var centers = new double[2];
        var map = new int[2];

        var layout = Run(widths, new[] { false, false }, baseLefts, centers, map);

        Assert.Equal(0, layout.FieldCount);
        Assert.Equal(2, layout.VisibleCount);
        Assert.Equal(SeparatorBox + Spacing + SeparatorBox, layout.NaturalWidth, 6);
        Assert.Equal(0.0, baseLefts[0], 6);
        Assert.Equal(SeparatorBox + Spacing, baseLefts[1], 6);
    }

    [Fact]
    public void 收起的媒体槽既不占位也不进场()
    {
        // [图标][胶囊 Width=0][图标][分隔符]：隐藏槽贴着下一项，不留空隙、不产生"幽灵鼓包"
        var widths = new[] { Icon, 0.0, Icon, SeparatorBox };
        var magnifiable = new[] { true, true, true, false };
        var baseLefts = new double[4];
        var centers = new double[4];
        var map = new int[4];

        var layout = Run(widths, magnifiable, baseLefts, centers, map);

        Assert.Equal(2, layout.FieldCount);           // 胶囊 + 分隔符都不在有效场里（胶囊宽度 0 不占位）
        Assert.Equal(3, layout.VisibleCount);
        Assert.Equal(Icon + Spacing + Icon + Spacing + SeparatorBox, layout.NaturalWidth, 6);
        Assert.Equal(50.0, baseLefts[1], 6);          // 隐藏槽与下一项同左缘
        Assert.Equal(50.0, baseLefts[2], 6);
        Assert.Equal(0, map[0]);                      // 场里只有两个图标：胶囊（隐藏）与分隔符（不入场）
        Assert.Equal(2, map[1]);
    }

    [Fact]
    public void 空行与越界宽度都归零且不抛()
    {
        var layout = Run(Array.Empty<double>(), Array.Empty<bool>(), new double[0], new double[0], new int[0]);
        Assert.Equal(0.0, layout.NaturalWidth, 6);
        Assert.Equal(0, layout.FieldCount);

        // NaN / 负宽（模板未量到）按隐藏槽处理：不占位、不进场
        var widths = new[] { double.NaN, -3.0, Icon };
        var baseLefts = new double[3];
        var centers = new double[3];
        var map = new int[3];
        var second = Run(widths, new[] { true, true, true }, baseLefts, centers, map);
        Assert.Equal(1, second.VisibleCount);
        Assert.Equal(Icon, second.NaturalWidth, 6);
        Assert.Equal(2, map[0]);                      // 场里只有那个真有宽度的图标
        Assert.Equal(22.0, centers[0], 6);
    }

    [Fact]
    public void 缓冲区长度不足时抛出参数异常()
    {
        var two = new[] { Icon, Icon };
        var both = new[] { true, true };
        Assert.Throws<ArgumentException>(() => DockRowLayout.Compute(
            two, new[] { true }, Spacing, MinVisible, new double[2], new double[2], new int[2]));      // 掩码短
        Assert.Throws<ArgumentException>(() => DockRowLayout.Compute(
            two, both, Spacing, MinVisible, new double[1], new double[2], new int[2]));                 // 左缘短
        Assert.Throws<ArgumentException>(() => DockRowLayout.Compute(
            two, both, Spacing, MinVisible, new double[2], new double[1], new int[2]));                 // 中心短
        Assert.Throws<ArgumentException>(() => DockRowLayout.Compute(
            two, both, Spacing, MinVisible, new double[2], new double[2], new int[1]));                 // 映射短
    }

    [Fact]
    public void 逐帧几何里分隔符不缩放而邻居间距仍恒等于spacing()
    {
        // 中间项是分隔符（缩放恒 1），两侧图标被放大：drawn 相邻间距必须仍是 6，
        // 且分隔符的像素宽度不被拉伸——这就是"竖线不跟着图标长、也不把图标挤歪"的证明。
        var widths = new[] { Icon, SeparatorBox, Icon };
        var scales = new[] { 1.8, 1.0, 1.5 };
        var baseLefts = new[] { 0.0, 50.0, 67.0 };
        var drawn = new double[3];
        var layout = new double[3];

        var totalExtra = MagnificationGeometry.ComputeRowLefts(baseLefts, widths, scales, false, drawn, layout);

        Assert.Equal((1.8 - 1.0) * Icon + (1.5 - 1.0) * Icon, totalExtra, 6);   // 分隔符贡献 0 额外宽度
        Assert.Equal(0.0, drawn[0], 9);
        Assert.Equal(Icon * 1.8 + Spacing, drawn[1], 6);                        // 第一个图标放大后的右缘 + spacing
        Assert.Equal(drawn[1] + SeparatorBox + Spacing, drawn[2], 6);           // 分隔符按**未放大**宽度让位
        Assert.Equal(drawn[1] + SeparatorBox * scales[1], drawn[2] - Spacing, 6);
        // 行右缘 = 上报总宽（底板不偏心）
        Assert.Equal(drawn[2] + Icon * 1.5, widths[0] * scales[0] + Spacing + widths[1] + Spacing + widths[2] * scales[2], 6);
    }

    [Fact]
    public void 面板固定居中时分隔符仍与邻居保持原间距()
    {
        var widths = new[] { Icon, SeparatorBox, Icon, Icon };
        var scales = new[] { 1.0, 1.0, 1.8, 1.0 };
        var baseLefts = new[] { 0.0, 50.0, 67.0, 117.0 };
        var drawn = new double[4];
        var layout = new double[4];
        MagnificationGeometry.ComputeRowLefts(baseLefts, widths, scales, true, drawn, layout);

        for (var i = 0; i + 1 < drawn.Length; i++)
            Assert.Equal(Spacing, drawn[i + 1] - (drawn[i] + widths[i] * scales[i]), 6);
        // 整行以自身中心外扩：中心不漂移
        var natural = Icon * 3 + SeparatorBox + 3 * Spacing;
        var grown = (drawn[0] + drawn[3] + widths[3] * scales[3]) / 2.0;
        Assert.Equal(natural / 2.0, grown, 6);
    }
}
