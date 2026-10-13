using Beautiful.Core.Themes;
using Xunit;

public class ThemeDefinitionTests
{
    [Fact]
    public void 三主题工厂_Id互不相同()
    {
        var ids = new[]
        {
            ThemeDefinition.MacOs().Id,
            ThemeDefinition.Linux().Id,
            ThemeDefinition.HarmonyOs().Id
        };
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void 鸿蒙强调色为鸿蒙蓝0A59F7()
    {
        Assert.Equal("#0A59F7", ThemeDefinition.HarmonyOs().AccentHex);
    }

    [Fact]
    public void 仅macOS启用放大_其余MaxScale为1()
    {
        Assert.Equal(1.8, ThemeDefinition.MacOs().MaxScale, 6);
        Assert.Equal(1.0, ThemeDefinition.HarmonyOs().MaxScale, 6);
        Assert.Equal(1.0, ThemeDefinition.Linux().MaxScale, 6);
    }

    [Fact]
    public void ShowMagnification派生属性随MaxScale联动()
    {
        // 控制器裁定：MaxScale>1 才开高斯放大
        Assert.True(ThemeDefinition.MacOs().ShowMagnification);
        Assert.False(ThemeDefinition.HarmonyOs().ShowMagnification);
        Assert.False(ThemeDefinition.Linux().ShowMagnification);
    }

    [Fact]
    public void 鸿蒙参数符合调研token_默认深色玻璃()
    {
        var t = ThemeDefinition.HarmonyOs();
        Assert.Equal("HarmonyOS", t.Id);
        Assert.Equal(28, t.PanelCornerRadius, 6);   // 圆角 24-34 取 28
        Assert.Equal(0.25, t.IconCornerRadiusRatio, 6); // squircle 连续圆角
        Assert.True(t.BlurEnabled);                  // 毛玻璃
        Assert.Equal("dot", t.IndicatorStyle);       // 底部圆点
        Assert.Equal("HarmonyOS Sans", t.FontFamily);
        // 深色玻璃 #61000000（38% 黑；浅色变体 #59FFFFFF 留给 Task 17 系统主题跟随）
        Assert.Equal(0x61, t.DockBackground.A);
        Assert.Equal(0x00, t.DockBackground.R);
        Assert.Equal(0x00, t.DockBackground.G);
        Assert.Equal(0x00, t.DockBackground.B);
    }

    [Fact]
    public void macOS参数符合规格()
    {
        var t = ThemeDefinition.MacOs();
        Assert.Equal("MacOS", t.Id);
        Assert.Equal("#0A84FF", t.AccentHex);        // 系统蓝
        Assert.Equal("dot", t.IndicatorStyle);
        Assert.True(t.BlurEnabled);                  // 暗/亮亚克力
        Assert.Equal(18, t.PanelCornerRadius, 6);
        Assert.Equal(0, t.IconCornerRadiusRatio, 6); // 图标原样不遮罩
        // 暗色亚克力 #B4202020
        Assert.Equal(0xB4, t.DockBackground.A);
        Assert.Equal(0x20, t.DockBackground.R);
        Assert.Equal(0x20, t.DockBackground.G);
        Assert.Equal(0x20, t.DockBackground.B);
    }

    [Fact]
    public void Linux参数符合规格_实底无模糊()
    {
        var t = ThemeDefinition.Linux();
        Assert.Equal("Linux", t.Id);
        Assert.Equal("#3584E4", t.AccentHex);
        Assert.Equal("bar", t.IndicatorStyle);       // 左侧短条
        Assert.False(t.BlurEnabled);                 // 实底：亚克力禁用
        Assert.Equal(12, t.PanelCornerRadius, 6);
        Assert.Equal(1.0, t.MaxScale, 6);            // 无放大
        // 实底 #E61C1C1C
        Assert.Equal(0xE6, t.DockBackground.A);
        Assert.Equal(0x1C, t.DockBackground.R);
        Assert.Equal(0x1C, t.DockBackground.G);
        Assert.Equal(0x1C, t.DockBackground.B);
    }

    [Fact]
    public void 亚克力tint与Dock底色一致_各主题()
    {
        // macOS 明确"tint 同底色"；鸿蒙按同一模式取面板色作窗口级 blur 等效 tint
        Assert.Equal(ThemeDefinition.MacOs().DockBackground, ThemeDefinition.MacOs().AcrylicTint);
        Assert.Equal(ThemeDefinition.HarmonyOs().DockBackground, ThemeDefinition.HarmonyOs().AcrylicTint);
        Assert.Equal(ThemeDefinition.Linux().DockBackground, ThemeDefinition.Linux().AcrylicTint);
    }
}

public class SuperellipseTests
{
    [Fact]
    public void 默认采样96个点()
    {
        Assert.Equal(96, Superellipse.Points(22, 22, 4.4).Count);
    }

    [Fact]
    public void nExp等于2时退化为椭圆_θ0处为长半轴端点()
    {
        // θ=0 → cos=1,sin=0 → x=a·|1|^(2/2)=a, y=0
        var pts = Superellipse.Points(a: 22, b: 22, nExp: 2);
        Assert.Equal(22, pts[0].X, 6);
        Assert.Equal(0, pts[0].Y, 6);
    }

    [Fact]
    public void nExp等于2时45度点为07071倍半轴()
    {
        // samples=96 → k=12 → θ=2π·12/96=π/4；nExp=2 → 指数 2/2=1 → x=0.7071a, y=0.7071b
        var pts = Superellipse.Points(a: 22, b: 22, nExp: 2);
        Assert.Equal(22 * 0.70710678, pts[12].X, 3);
        Assert.Equal(22 * 0.70710678, pts[12].Y, 3);
    }

    [Fact]
    public void 全部采样点满足超椭圆方程()
    {
        const double a = 22, b = 22, n = 4.4;
        foreach (var (x, y) in Superellipse.Points(a, b, n))
        {
            var lhs = Math.Pow(Math.Abs(x / a), n) + Math.Pow(Math.Abs(y / b), n);
            Assert.Equal(1.0, lhs, 6);
        }
    }

    [Fact]
    public void 四个轴向端点落在包围盒上()
    {
        // k=0→(a,0) k=24→θ=π/2→(0,b) k=48→θ=π→(-a,0) k=72→θ=3π/2→(0,-b)
        var pts = Superellipse.Points(a: 22, b: 22, nExp: 4.4);
        Assert.Equal(22, pts[0].X, 6);
        Assert.Equal(22, pts[24].Y, 6);
        Assert.Equal(-22, pts[48].X, 6);
        Assert.Equal(-22, pts[72].Y, 6);
        Assert.InRange(pts[24].X, -1e-5, 1e-5);
        Assert.InRange(pts[48].Y, -1e-5, 1e-5);
    }
}
