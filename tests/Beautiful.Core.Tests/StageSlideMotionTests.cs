using System;
using Beautiful.Core.StageManager;
using Xunit;

/// <summary>
/// 台前调度滑动动画的运动学纯函数（wp-macos-parity r6）：缓动曲线（CSS cubic-bezier 求值）
/// 与起止插值。约定：端点恒等（0→0、1→1）、曲线单调不降、时长 0 是"一步到位"的合法退化。
/// token 值 cubic-bezier(0,0,0.58,1)（= MacTokens.EaseOut）必须原样解析成功。
/// </summary>
public class StageSlideMotionTests
{
    private const string TokenEaseOut = "cubic-bezier(0,0,0.58,1)";

    // ---------- StageEase：解析 ----------

    [Fact]
    public void 定稿token曲线原样解析()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        Assert.Equal(StageEase.Default, ease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ease")]
    [InlineData("cubic-bezier(0,0,0.58)")]        // 参数不足
    [InlineData("cubic-bezier(0,0,0.58,1,2)")]    // 参数超量
    [InlineData("cubic-bezier(a,b,c,d)")]          // 非数字
    [InlineData("cubic-bezier(1.2,0,0.58,1)")]     // x1 越界（CSS 规范：x∈[0,1]）
    [InlineData("cubic-bezier(0,0,-0.1,1)")]       // x2 越界
    public void 非法曲线一律拒绝回落由调用方处理(string? text)
    {
        Assert.False(StageEase.TryParseCurve(text, out _));
    }

    [Fact]
    public void y坐标允许越出01区间_x坐标不允许()
    {
        // CSS：y 无界（回弹曲线合法），x 必须在 [0,1]。
        Assert.True(StageEase.TryParseCurve("cubic-bezier(0.2,1.5,0.4,1)", out _));
        Assert.True(StageEase.TryParseCurve("cubic-bezier(0.2,-0.5,0.4,1)", out _));
        Assert.False(StageEase.TryParseCurve("cubic-bezier(0.2,1.5,1.4,1)", out _));
    }

    // ---------- StageEase：求值 ----------

    [Fact]
    public void 曲线端点恒等_越界钳制()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        Assert.Equal(0, ease.Evaluate(-0.5));
        Assert.Equal(0, ease.Evaluate(0));
        Assert.Equal(1, ease.Evaluate(1));
        Assert.Equal(1, ease.Evaluate(1.5));
    }

    [Fact]
    public void 定稿缓动是前快后慢_全程不低于对角线()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        Assert.True(ease.Evaluate(0.25) > 0.25, "ease-out 应先快：y(0.25)>0.25");
        Assert.True(ease.Evaluate(0.75) > 0.75, "ease-out 尾段缓慢逼近 1：y(0.75)>0.75");
        Assert.True(ease.Evaluate(0.99) < 1.0, "未到端点不得提前到 1");
    }

    [Fact]
    public void 曲线单调不降()
    {
        Assert.True(StageEase.TryParseCurve("cubic-bezier(0.42,0,0.58,1)", out var easeInOut));
        var prev = 0.0;
        for (var t = 0.0; t <= 1.0 + 1e-9; t += 0.05)
        {
            var y = easeInOut.Evaluate(Math.Min(t, 1.0));
            Assert.True(y >= prev - 1e-9, $"在 t={t:0.00} 处出现回退：{y} < {prev}");
            prev = y;
        }
    }

    [Fact]
    public void 竖直段曲线退回二分仍收敛()
    {
        // x1==x2（导数可为 0 的极端参数）：牛顿失稳必须由二分兜住，不抛异常、不发散。
        Assert.True(StageEase.TryParseCurve("cubic-bezier(0.5,0,0.5,1)", out var ease));
        Assert.Equal(0, ease.Evaluate(0));
        Assert.Equal(1, ease.Evaluate(1));
        var mid = ease.Evaluate(0.5);
        Assert.InRange(mid, 0.0, 1.0);
        Assert.True(ease.Evaluate(0.25) <= ease.Evaluate(0.75) + 1e-9, "单调性仍须成立");
    }

    // ---------- StageSlideMotion：进度与插值 ----------

    [Fact]
    public void 进度端点与完成态()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        Assert.Equal(0, StageSlideMotion.Progress(-10, 260, ease));
        Assert.Equal(0, StageSlideMotion.Progress(0, 260, ease));
        Assert.Equal(1, StageSlideMotion.Progress(260, 260, ease));
        Assert.Equal(1, StageSlideMotion.Progress(999, 260, ease));
    }

    [Fact]
    public void 时长为零或负是合法退化_一步到位()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        Assert.Equal(1, StageSlideMotion.Progress(0, 0, ease));
        Assert.Equal(1, StageSlideMotion.Progress(100, 0, ease));
        Assert.Equal(1, StageSlideMotion.Progress(50, -5, ease));
    }

    [Fact]
    public void 中段进度等于缓动在时间分数处的值()
    {
        Assert.True(StageEase.TryParseCurve(TokenEaseOut, out var ease));
        var expected = ease.Evaluate(130 / 260.0);
        Assert.Equal(expected, StageSlideMotion.Progress(130, 260, ease), 12);
    }

    [Fact]
    public void 插值端点与中点()
    {
        Assert.Equal(100, StageSlideMotion.Interpolate(100, 900, 0));
        Assert.Equal(900, StageSlideMotion.Interpolate(100, 900, 1));
        Assert.Equal(500, StageSlideMotion.Interpolate(100, 900, 0.5));
        // 越界进度钳制（防外层浮点毛刺把坐标推出起止）。
        Assert.Equal(100, StageSlideMotion.Interpolate(100, 900, -0.3));
        Assert.Equal(900, StageSlideMotion.Interpolate(100, 900, 1.3));
    }

    [Fact]
    public void 收起方向插值终点正确()
    {
        // 展开位 100 → 收起位 -1660（条宽 196 的物理值随 DPI 而异，这里只验数学）。
        Assert.Equal(-1660, StageSlideMotion.Interpolate(100, -1660, 1));
        Assert.True(StageSlideMotion.Interpolate(100, -1660, 0.5) < 100, "收起方向中途必然左移");
    }
}
