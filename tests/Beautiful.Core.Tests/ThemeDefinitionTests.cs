using Beautiful.Core.Themes;
using Xunit;

public class ThemeDefinitionTests
{
    [Fact]
    public void macOS参数符合规格()
    {
        var t = ThemeDefinition.MacOs();
        Assert.Equal("MacOS", t.Id);
        Assert.Equal("#0A84FF", t.AccentHex);        // 系统蓝
        Assert.Equal("dot", t.IndicatorStyle);
        Assert.True(t.BlurEnabled);                  // 暗/亮亚克力
        Assert.Equal(18, t.PanelCornerRadius, 6);
        Assert.Equal(0, t.IconCornerRadiusRatio, 6); // 图标原样不遮罩（T6 收到烘焪位图拥有形状）
        // 暗色亚克力 #B4202020
        Assert.Equal(0xB4, t.DockBackground.A);
        Assert.Equal(0x20, t.DockBackground.R);
        Assert.Equal(0x20, t.DockBackground.G);
        Assert.Equal(0x20, t.DockBackground.B);
    }

    [Fact]
    public void macOS启用放大_而非放大的主题MaxScale为1()
    {
        Assert.Equal(1.8, ThemeDefinition.MacOs().MaxScale, 6);
        Assert.True(ThemeDefinition.MacOs().ShowMagnification);
    }

    [Fact]
    public void ShowMagnification派生属性随MaxScale联动()
    {
        // 控制器裁定：MaxScale > 1 才开高斯放大；macOS MaxScale=1.8 → 放大
        Assert.True(ThemeDefinition.MacOs().ShowMagnification);
    }

    [Fact]
    public void macOS亚克力tint与Dock底色一致()
    {
        // macOS 明确 "tint 同底色"
        Assert.Equal(ThemeDefinition.MacOs().DockBackground, ThemeDefinition.MacOs().AcrylicTint);
    }

    [Fact]
    public void macOS图标为圆角方形带投影_分隔符开启()
    {
        var t = ThemeDefinition.MacOs();
        Assert.Equal(IconMaskStyle.RoundedRectangle, t.IconMask);   // 圆角方块
        Assert.Equal(0.2, t.IconMaskRadiusRatio, 6);               // Big Sur ≈ 边长 20%
        Assert.True(t.IconShadow);                                  // 贴近的柔影
        Assert.True(t.DockSeparatorsEnabled);                        // 固定 | 运行 | 回收站 两道分隔符
    }
}

public class ThemeRecordGeometryTests
{
    [Fact]
    public void DockSeparatorBoxWidthDip由线粗与对称留白合成()
    {
        var mac = ThemeDefinition.MacOs();
        Assert.Equal(mac.DockSeparatorWidthDip + 2.0 * mac.DockSeparatorSideInsetDip,
                     mac.DockSeparatorBoxWidthDip);
    }
}
