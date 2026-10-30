using System;
using Beautiful.Core.Dock;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// 主题 → Dock 竖向几何的合账（v3 T4 的用户不满收口）：
/// ① "dock 栏有一段黑框，我只需要 dock 的大小" ⇒ 面板必须贴住图标（IconSize=44 时 macOS 66 DIP，v2 是 76）；
/// ② "macOS Dock 有原生分隔符" ⇒ 分隔符是主题数据，macOS 开。
/// 渲染器与条带几何都读这一组数（<see cref="DockRowMath"/>），主题值一变，面板与预留带一起变。
/// </summary>
public class ThemeDockGeometryTests
{
    // DockSurface 的倒影常量（App 侧 ReflectionGapDip / ReflectionHeightRatio）
    private const double Gap = 2.0;
    private const double Ratio = 0.4;

    private static double Panel(ThemeDefinition t, double iconSize, bool reflection)
        => DockRowMath.PanelHeightDip(
            DockRowMath.RowHeightDip(iconSize, t.DockRowInsetDip, reflection, Gap, Ratio),
            t.DockPanelPaddingYDip);

    [Fact]
    public void 图标44时macOS面板紧贴图标_不再是不装东西的黑框()
    {
        // 行高 = 图标 + 10 DIP 呼吸（远小于旧的统一 20）
        Assert.Equal(54.0, DockRowMath.RowHeightDip(44, ThemeDefinition.MacOs().DockRowInsetDip, false, Gap, Ratio), 6);
        // macOS 66 DIP（验收区间 60~66）
        Assert.Equal(66.0, Panel(ThemeDefinition.MacOs(), 44, false), 6);

        var mac = ThemeDefinition.MacOs();
        var panel = Panel(mac, 44, false);
        Assert.True(panel <= 76.0, $"面板 {panel} 不应高于 v2 的 76 DIP");
        Assert.True(panel - 44.0 <= 30.0, $"面板比图标多出 {panel - 44:F1} DIP，仍是空框");
        // 行内下半截放得下运行指示点（4 DIP）
        Assert.True(mac.DockRowInsetDip / 2.0 >= 4.0);
    }

    [Fact]
    public void macOS行内余量与内边距都是正数且小于旧常量()
    {
        var t = ThemeDefinition.MacOs();
        Assert.True(t.DockRowInsetDip > 0, t.Id);
        Assert.True(t.DockPanelPaddingXDip > 0, t.Id);
        Assert.True(t.DockPanelPaddingYDip > 0, t.Id);
        Assert.True(t.DockRowInsetDip < 20, "行内余量必须紧于旧的常量 20");
    }

    [Fact]
    public void macOS倒影默认关_现代原生dock无倒影()
    {
        Assert.False(ThemeDefinition.MacOs().ReflectionDefault);
        // 显式打开时行照着长高（老设置的语义不变）
        Assert.Equal(83.2, DockRowMath.RowHeightDip(44, ThemeDefinition.MacOs().DockRowInsetDip, true, Gap, Ratio), 6);
    }

    [Fact]
    public void 倒影设置三态_未显式选择时跟随主题_显式选择优先()
    {
        var unset = new AppSettings();
        Assert.Null(unset.ReflectionEnabled);                              // 默认：跟随主题
        Assert.False(unset.ReflectionEnabledFor(false));                   // macOS 默认 → 关
        Assert.True(unset.ReflectionEnabledFor(true));                      // 假定有倒影的主题 → 开
        Assert.True(new AppSettings { ReflectionEnabled = true }.ReflectionEnabledFor(false));   // 用户显式开：主题关不掉
        Assert.False(new AppSettings { ReflectionEnabled = false }.ReflectionEnabledFor(true));  // 用户显式关：主题开不了
    }

    [Fact]
    public void 仅macOS主题开分隔符_线粗与比例符合原生观感()
    {
        var mac = ThemeDefinition.MacOs();
        Assert.True(mac.DockSeparatorsEnabled);
        Assert.Equal(1.0, mac.DockSeparatorWidthDip, 6);                   // 发丝线
        Assert.InRange(mac.DockSeparatorHeightRatio, 0.6, 0.7);             // 行高的 6~7 成
        Assert.Equal(11.0, mac.DockSeparatorBoxWidthDip, 6);                // 线粗 + 两侧对称留白
    }

    [Fact]
    public void 放大余量随macOS几何走_收紧面板后图标顶边不被窗口裁掉()
    {
        var mac = ThemeDefinition.MacOs();
        var headroom = DockRowMath.MagnificationHeadroomDip(44, mac.MaxScale, mac.DockRowInsetDip,
                                                             mac.DockPanelPaddingYDip, 22);
        // 需要越出面板的量（图标长高 35.2 − 行内上半 5 − 面板内边距 6 = 24.2）必须被窗口余量容下
        Assert.True(headroom >= 44 * (mac.MaxScale - 1.0) - mac.DockRowInsetDip / 2.0 - mac.DockPanelPaddingYDip,
            $"余量 {headroom} 容不下放大越出量");
    }
}
