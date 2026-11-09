using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// IconBake.ClampRadiusRatio：注释自称"纯函数，可单测"的半径钳制（wp-borrow 收口补测，2026-11-10）。
/// None 恒 0 是防"ratio=0.25 + None 画出半个圆角"的组合错；NaN/∞/负归 0 是防渲染侧画没。
/// </summary>
public class IconBakeClampTests
{
    [Fact]
    public void None遮罩_任何比例恒0()
    {
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.None, 0.25), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.None, 0.9), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.None, double.NaN), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.None, -0.5), 9);
    }

    [Fact]
    public void 非数值与无穷_归0()
    {
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, double.NaN), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, double.PositiveInfinity), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, double.NegativeInfinity), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, -1.0), 9);
    }

    [Fact]
    public void 合法区间原样_越过上限钳到0点5()
    {
        Assert.Equal(0.25, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.25), 9);
        Assert.Equal(0.0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.0), 9);
        Assert.Equal(0.5, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.5), 9);   // 内切圆上界
        Assert.Equal(IconBake.MaxMaskRadiusRatio, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.7), 9);
        Assert.Equal(IconBake.MaxMaskRadiusRatio, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 12.0), 9);
    }

    [Fact]
    public void RadiusPx_按比例取整_非法输入兜0()
    {
        Assert.Equal(12, IconBake.RadiusPx(IconMaskStyle.RoundedRectangle, 0.25, 48));  // floor(0.25*48)
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.None, 0.25, 48));               // None 没有半径
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.RoundedRectangle, 0.25, 0));    // 边长非法
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.RoundedRectangle, double.NaN, 48));
    }
}
