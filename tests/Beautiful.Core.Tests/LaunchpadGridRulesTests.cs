using Beautiful.Core.Launchpad;
using Xunit;

/// <summary>
/// 启动台自适应网格测试：列数随宽度变、图标优先不缩、窄屏才缩图标、退化输入不炸。
/// 这些数值决定真机上"一页放几个"，只能在纯函数层钉死（截图看不出 4K 与 1366 的差异）。
/// </summary>
public class LaunchpadGridRulesTests
{
    [Fact]
    public void 宽屏列数更多且不超过上限()
    {
        var laptop = LaunchpadGridRules.Fit(800, 700);
        var wide = LaunchpadGridRules.Fit(2400, 1200);
        var insane = LaunchpadGridRules.Fit(20000, 4000);

        Assert.True(wide.Columns > laptop.Columns);
        Assert.True(insane.Columns <= LaunchpadGridRules.MaxColumns);
    }

    [Fact]
    public void 窄屏不会少于三列()
    {
        var narrow = LaunchpadGridRules.Fit(320, 480);
        Assert.Equal(LaunchpadGridRules.MinColumns, narrow.Columns);
        Assert.True(narrow.IconSize >= LaunchpadGridRules.MinIconDip);
    }

    [Fact]
    public void 宽度充足时保持请求的图标尺寸()
    {
        var fit = LaunchpadGridRules.Fit(1600, 900, iconDip: 76);
        Assert.Equal(76, fit.IconSize, precision: 6);
        Assert.True(fit.Capacity >= 15);
    }

    [Fact]
    public void 每页容量等于列乘行且行数至少一()
    {
        var fit = LaunchpadGridRules.Fit(900, 60);
        Assert.Equal(1, fit.Rows);
        Assert.Equal(fit.Columns * fit.Rows, fit.Capacity);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, -50)]
    [InlineData(double.NaN, double.NaN)]
    public void 退化输入不抛异常且给出可用网格(double width, double height)
    {
        var fit = LaunchpadGridRules.Fit(width, height);
        Assert.InRange(fit.Columns, LaunchpadGridRules.MinColumns, LaunchpadGridRules.MaxColumns);
        Assert.True(fit.Rows >= 1);
        Assert.True(fit.IconSize >= LaunchpadGridRules.MinIconDip);
        Assert.True(fit.Capacity >= 3);
    }

    [Fact]
    public void 单元格高度包含标签带()
    {
        var fit = LaunchpadGridRules.Fit(1600, 900, iconDip: 76, gapDip: 20);
        Assert.Equal(fit.IconSize + LaunchpadGridRules.LabelBandDip + 20, fit.CellHeight, precision: 6);
    }
}
