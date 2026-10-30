using Beautiful.Core.Layout;
using Xunit;

/// <summary>v4 Phase 5-T2：触发角命中几何与驻留状态机（无 UI、无时钟依赖）。</summary>
public class HotCornerPlanTests
{
    private static readonly PhysicalRect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(0, 0, HotCorner.TopLeft)]
    [InlineData(5, 5, HotCorner.TopLeft)]
    [InlineData(1919, 0, HotCorner.TopRight)]
    [InlineData(0, 1079, HotCorner.BottomLeft)]
    [InlineData(1919, 1079, HotCorner.BottomRight)]
    public void 四角命中(int x, int y, HotCorner expected)
        => Assert.Equal(expected, HotCornerPlan.Hit(Monitor, x, y));

    [Theory]
    [InlineData(6, 6)]
    [InlineData(960, 540)]
    [InlineData(-1, 0)]
    [InlineData(1920, 1079)]
    public void 热区外不命中(int x, int y)
        => Assert.Null(HotCornerPlan.Hit(Monitor, x, y));

    [Fact]
    public void 副屏负坐标同样成立()
    {
        var secondary = new PhysicalRect(-2560, 0, 0, 1440);
        Assert.Equal(HotCorner.TopLeft, HotCornerPlan.Hit(secondary, -2560, 0));
        Assert.Equal(HotCorner.BottomRight, HotCornerPlan.Hit(secondary, -1, 1439));
    }

    [Fact]
    public void 驻留满时长只触发一次_离开后重新武装()
    {
        var state = new HotCornerDwellState();
        Assert.Null(state.Update(HotCorner.TopLeft, 1000, 300));
        Assert.Null(state.Update(HotCorner.TopLeft, 1299, 300));
        Assert.Equal(HotCorner.TopLeft, state.Update(HotCorner.TopLeft, 1300, 300));
        Assert.Null(state.Update(HotCorner.TopLeft, 2000, 300));      // 已触发，不重复
        Assert.Null(state.Update(null, 2100, 300));                    // 离开
        Assert.Null(state.Update(HotCorner.TopLeft, 2200, 300));      // 重新进入
        Assert.Equal(HotCorner.TopLeft, state.Update(HotCorner.TopLeft, 2500, 300));
    }

    [Fact]
    public void 换角会重新计时()
    {
        var state = new HotCornerDwellState();
        Assert.Null(state.Update(HotCorner.TopLeft, 0, 300));
        Assert.Null(state.Update(HotCorner.TopRight, 299, 300));
        Assert.Null(state.Update(HotCorner.TopRight, 400, 300));
        Assert.Equal(HotCorner.TopRight, state.Update(HotCorner.TopRight, 599, 300));
    }
}
