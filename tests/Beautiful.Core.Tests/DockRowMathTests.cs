using System;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock 行/面板竖向几何（<see cref="DockRowMath"/>）。
/// 这组断言就是用户"下方的 dock 栏有一段是黑框，我只需要 dock 的大小"的验收口径：
/// 行内余量与面板内边距改成主题驱动后，IconSize=44 的可见面板必须从 v2 的 76 DIP 收到"图标 + 一小圈呼吸"。
/// </summary>
public class DockRowMathTests
{
    private const double Gap = 2.0;        // 倒影带间隙（与 DockSurface 常量同源）
    private const double Ratio = 0.4;      // 倒影高 / 图标尺寸

    [Fact]
    public void 倒影关闭时行高恰为图标加主题行内余量()
    {
        // 单测直接钉死"RowHeightDip = IconSize + 主题余量"这一条：没有隐藏的常数项，面板不再凭空长高
        Assert.Equal(44 + 10, DockRowMath.RowHeightDip(44, 10, false, Gap, Ratio), 6);
        Assert.Equal(44 + 16, DockRowMath.RowHeightDip(44, 16, false, Gap, Ratio), 6);
        Assert.Equal(44 + 12, DockRowMath.RowHeightDip(44, 12, false, Gap, Ratio), 6);
        Assert.Equal(32 + 10, DockRowMath.RowHeightDip(32, 10, false, Gap, Ratio), 6);
        Assert.Equal(0.0, DockRowMath.ReflectionExtraDip(44, 10, false, Gap, Ratio), 6);
    }

    [Fact]
    public void 倒影开启时行高仍完整容下倒影且与余量无关()
    {
        // 倒影关→开必须让行"长出"那截（镜像 + 间隙不被条带下缘裁掉）：44 + 2×2 + 0.8×44 = 83.2
        var withReflection = DockRowMath.RowHeightDip(44, 10, true, Gap, Ratio);
        Assert.Equal(83.2, withReflection, 6);
        Assert.Equal(83.2 - 54.0, DockRowMath.ReflectionExtraDip(44, 10, true, Gap, Ratio), 6);
        // 倒影开启时行内余量被抵掉（行高 = 图标 + 2×间隙 + 2×比例×图标）→ 收紧面板不影响倒影路径，
        // 老设置里显式开着倒影的用户看到的条带高度与 v2 完全一致。
        Assert.Equal(DockRowMath.RowHeightDip(44, 20, true, Gap, Ratio), withReflection, 6);
        Assert.Equal(DockRowMath.RowHeightDip(44, 8, true, Gap, Ratio), withReflection, 6);
    }

    [Fact]
    public void 行内余量已经装得下倒影时不再加高()
    {
        // 大余量 + 小图标：2×gap + 2×ratio×icon < inset → 余量为 0，行高不缩到图标之下也不虚增
        Assert.Equal(0.0, DockRowMath.ReflectionExtraDip(32, 40, true, Gap, Ratio), 6);
        Assert.Equal(72.0, DockRowMath.RowHeightDip(32, 40, true, Gap, Ratio), 6);
    }

    [Fact]
    public void 可见面板高只比行高多两倍竖向内边距()
    {
        // AppBar 预留带 = 可见面板：这条等式是"预留带 ≠ 面板"这类黑框回归的唯一防线
        Assert.Equal(66.0, DockRowMath.PanelHeightDip(DockRowMath.RowHeightDip(44, 10, false, Gap, Ratio), 6), 6);
        Assert.Equal(95.2, DockRowMath.PanelHeightDip(DockRowMath.RowHeightDip(44, 10, true, Gap, Ratio), 6), 6);
        Assert.Equal(54.0, DockRowMath.PanelHeightDip(54.0, 0.0), 6);
    }

    [Fact]
    public void 放大余量随面板收紧一起加长不被窗口上沿裁切()
    {
        // IconSize=44、MaxScale=1.8：图标绕底边中心放大长高 35.2 DIP，
        // 行内只有上半截 5 DIP + 面板内边距 6 DIP 可用 → 必须越出面板 24.2 DIP，窗口得留出 25。
        Assert.Equal(25.0, DockRowMath.MagnificationHeadroomDip(44, 1.8, 10, 6, 22), 6);
        // 旧的统一余量 20 + 内边距 6 时同一公式给出 19.2 → 取下限 22（与 v2 实测窗口高一致）
        Assert.Equal(22.0, DockRowMath.MagnificationHeadroomDip(44, 1.8, 20, 6, 22), 6);
        // 不放大主题（MaxScale=1）恒取下限，不受主题余量影响
        Assert.Equal(22.0, DockRowMath.MagnificationHeadroomDip(44, 1.0, 16, 5, 22), 6);
        // 大图标 + 高强度：越出量线性增长，不会溢出下限就完事
        Assert.Equal(72 * 1.5 - 6 - 8, DockRowMath.MagnificationHeadroomDip(72, 2.5, 12, 8, 22), 6);
    }
}
