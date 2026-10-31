using Beautiful.Core.Animation;
using Xunit;

/// <summary>FrameTimeStats：帧时间统计（AnimLab 帧时间图的数据层）。</summary>
public class FrameTimeStatsTests
{
    [Fact]
    public void Empty_And_Null_Input_Yield_Zero_Summary()
    {
        var a = FrameTimeStats.Compute(new List<double>());
        var b = FrameTimeStats.Compute(null!);
        Assert.Equal(0, a.Count);
        Assert.Equal(0, b.Count);
        Assert.Equal(0, a.AverageMs, 6);
        Assert.Equal(0, a.P95Ms, 6);
        Assert.Equal(0, a.BadRatio, 6);
    }

    [Fact]
    public void NonPositive_And_NaN_Samples_Are_Discarded()
    {
        var s = FrameTimeStats.Compute(new List<double> { 0, -5, double.NaN, 16.0, 17.0 });
        Assert.Equal(2, s.Count);
        Assert.Equal(16.5, s.AverageMs, 6);
        Assert.Equal(17.0, s.MaxMs, 6);
    }

    [Fact]
    public void Average_Max_And_P95_LastRank()
    {
        // 20 个样本：1..20ms；P95 = ceil(0.95*20)=19 → index 18 → 19ms。
        var samples = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        var s = FrameTimeStats.Compute(samples);
        Assert.Equal(20, s.Count);
        Assert.Equal(10.5, s.AverageMs, 6);
        Assert.Equal(19.0, s.P95Ms, 6);
        Assert.Equal(20.0, s.MaxMs, 6);
    }

    [Fact]
    public void P95_Of_Five_Samples_Is_The_Fifth()
    {
        // ceil(0.95*5)=5 → index 4 → 最大值。
        var s = FrameTimeStats.Compute(new List<double> { 10, 20, 30, 40, 50 });
        Assert.Equal(50.0, s.P95Ms, 6);
    }

    [Fact]
    public void BadRatio_Counts_Frames_Over_Budget()
    {
        // 预算 16.7：16/17/33 中只有 17 与 33 超线 → 2/3。
        var s = FrameTimeStats.Compute(new List<double> { 16.0, 17.0, 33.0 });
        Assert.Equal(2.0 / 3.0, s.BadRatio, 6);
    }

    [Fact]
    public void Custom_Budget_Is_Respected()
    {
        var s = FrameTimeStats.Compute(new List<double> { 5.0, 8.0, 12.0 }, budgetMs: 8.0);
        Assert.Equal(1.0 / 3.0, s.BadRatio, 6);
    }

    [Fact]
    public void NonPositive_Budget_Falls_Back_To_16_7()
    {
        // budget<=0 回落默认线，避免除零/恒超线的假统计。
        var s = FrameTimeStats.Compute(new List<double> { 20.0, 20.0 }, budgetMs: 0);
        Assert.Equal(1.0, s.BadRatio, 6);
    }
}
