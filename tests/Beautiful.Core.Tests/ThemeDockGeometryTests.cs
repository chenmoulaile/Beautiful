using System;
using Beautiful.Core.Dock;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// 主题 → Dock 竖向几何的合账（v3 T4 的两条用户不满都在这里收口）：
/// ① "dock 栏有一段黑框，我只需要 dock 的大小" ⇒ 面板必须贴住图标（IconSize=44 时 macOS 66 DIP，v2 是 76）；
/// ② "macOS Dock 没有原生分隔符" ⇒ 分隔符是主题数据，只有 macOS 开。
/// 渲染器与条带几何都读这一组数（<see cref="DockRowMath"/>），所以主题值一变，面板与预留带一起变。
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
    public void 图标44时三主题面板都紧贴图标_不再是不装东西的黑框()
    {
        // 行高 = 图标 + 主题余量：呼吸量控制在 10~16 DIP，而不是旧的统一 20
        Assert.Equal(54.0, DockRowMath.RowHeightDip(44, ThemeDefinition.MacOs().DockRowInsetDip, false, Gap, Ratio), 6);
        Assert.Equal(60.0, DockRowMath.RowHeightDip(44, ThemeDefinition.Linux().DockRowInsetDip, false, Gap, Ratio), 6);
        Assert.Equal(56.0, DockRowMath.RowHeightDip(44, ThemeDefinition.HarmonyOs().DockRowInsetDip, false, Gap, Ratio), 6);

        // macOS 66（验收区间 60~66）；GNOME 更方更紧的留白、鸿蒙胶囊最松（但都远小于 v2 的 76）
        Assert.Equal(66.0, Panel(ThemeDefinition.MacOs(), 44, false), 6);
        Assert.Equal(70.0, Panel(ThemeDefinition.Linux(), 44, false), 6);
        Assert.Equal(72.0, Panel(ThemeDefinition.HarmonyOs(), 44, false), 6);
        foreach (var t in new[] { ThemeDefinition.MacOs(), ThemeDefinition.Linux(), ThemeDefinition.HarmonyOs() })
        {
            var panel = Panel(t, 44, false);
            Assert.True(panel <= 76.0, $"{t.Id} 面板 {panel} 不应高于 v2 的 76 DIP（用户要的就是这条）");
            Assert.True(panel - 44.0 <= 30.0, $"{t.Id} 面板比图标多出 {panel - 44:F1} DIP，仍是空框");
            // 图标上下各余一半行内余量：运行指示点（4 DIP）落在下半截里不被裁
            Assert.True(t.DockRowInsetDip / 2.0 >= 4.0, $"{t.Id} 行内下半截放不下指示点");
        }
    }

    [Fact]
    public void 主题行内余量与内边距都是正数且小于旧常量()
    {
        foreach (var t in new[] { ThemeDefinition.MacOs(), ThemeDefinition.Linux(), ThemeDefinition.HarmonyOs() })
        {
            Assert.True(t.DockRowInsetDip > 0, t.Id);
            Assert.True(t.DockPanelPaddingXDip > 0, t.Id);
            Assert.True(t.DockPanelPaddingYDip > 0, t.Id);
            Assert.True(t.DockRowInsetDip < 20, $"{t.Id} 行内余量必须紧于旧的常量 20");
        }
        // 鸿蒙胶囊最松（PanelPaddingY 最大），GNOME dash 内边距最紧
        Assert.True(ThemeDefinition.HarmonyOs().DockPanelPaddingYDip > ThemeDefinition.Linux().DockPanelPaddingYDip);
        Assert.True(ThemeDefinition.HarmonyOs().DockPanelPaddingXDip > ThemeDefinition.Linux().DockPanelPaddingXDip);
    }

    [Fact]
    public void 三主题倒影默认都关_现代原生dock无倒影()
    {
        Assert.False(ThemeDefinition.MacOs().ReflectionDefault);
        Assert.False(ThemeDefinition.Linux().ReflectionDefault);
        Assert.False(ThemeDefinition.HarmonyOs().ReflectionDefault);
        // 显式打开时行照旧长高（老设置的语义不变）
        Assert.Equal(83.2, DockRowMath.RowHeightDip(44, ThemeDefinition.MacOs().DockRowInsetDip, true, Gap, Ratio), 6);
    }

    [Fact]
    public void 倒影设置三态_未显式选择时跟随主题_显式选择优先()
    {
        var unset = new AppSettings();
        Assert.Null(unset.ReflectionEnabled);                              // 默认：跟随主题
        Assert.False(unset.ReflectionEnabledFor(false));                   // macOS/GNOME/鸿蒙默认 → 关
        Assert.True(unset.ReflectionEnabledFor(true));                      // 假想有倒影的主题 → 开
        Assert.True(new AppSettings { ReflectionEnabled = true }.ReflectionEnabledFor(false));   // 用户显式开：主题关不掉
        Assert.False(new AppSettings { ReflectionEnabled = false }.ReflectionEnabledFor(true));  // 用户显式关：主题开不了
    }

    [Fact]
    public void 仅macOS主题开分隔符_线粗与比例符合原生观感()
    {
        var mac = ThemeDefinition.MacOs();
        Assert.True(mac.DockSeparatorsEnabled);
        Assert.False(ThemeDefinition.Linux().DockSeparatorsEnabled);       // GNOME dash 是连续列表
        Assert.False(ThemeDefinition.HarmonyOs().DockSeparatorsEnabled);   // 鸿蒙同理

        Assert.Equal(1.0, mac.DockSeparatorWidthDip, 6);                   // 发丝线
        Assert.InRange(mac.DockSeparatorHeightRatio, 0.6, 0.7);             // 行高的 6~7 成
        Assert.Equal(11.0, mac.DockSeparatorBoxWidthDip, 6);                // 线粗 + 两侧对称留白
        // 关闭的分隔符几何归零：主题没开时既不会画线，也不会占一个"看不见但占宽"的槽
        Assert.Equal(0.0, ThemeDefinition.Linux().DockSeparatorBoxWidthDip, 6);
        Assert.Equal(0.0, ThemeDefinition.HarmonyOs().DockSeparatorBoxWidthDip, 6);
    }

    [Fact]
    public void 放大余量随主题几何走_收紧面板后图标顶边不被窗口裁掉()
    {
        var mac = ThemeDefinition.MacOs();
        var headroom = DockRowMath.MagnificationHeadroomDip(44, mac.MaxScale, mac.DockRowInsetDip,
                                                             mac.DockPanelPaddingYDip, 22);
        // 需要越出面板的量（图标长高 35.2 − 行内上半 5 − 面板内边距 6 = 24.2）必须被窗口余量容下
        Assert.True(headroom >= 44 * (mac.MaxScale - 1.0) - mac.DockRowInsetDip / 2.0 - mac.DockPanelPaddingYDip,
            $"余量 {headroom} 容不下放大越出量");
        // 无放大主题恒取 22（与 v1/v2 实测一致，不多让工作区一个像素）
        Assert.Equal(22.0, DockRowMath.MagnificationHeadroomDip(44, ThemeDefinition.Linux().MaxScale,
            ThemeDefinition.Linux().DockRowInsetDip, ThemeDefinition.Linux().DockPanelPaddingYDip, 22), 6);
    }
}
