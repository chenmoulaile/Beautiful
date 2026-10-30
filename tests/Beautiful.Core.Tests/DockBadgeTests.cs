using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock 角标与进度条规则测试（Phase 7-T2 / 7-T1 绘制侧）。
/// <para>
/// 这些规则真机上要等"微信真的来一条消息"才能看到，所以必须在这里把数字口径钉死：
/// 0 不画、99 封顶、标题里的括号必须是纯数字、进度非有限值不画。
/// </para>
/// </summary>
public class DockBadgeTests
{
    [Fact]
    public void 计数格式化在零与封顶处收口()
    {
        Assert.Null(DockBadge.Format(0));
        Assert.Null(DockBadge.Format(-3));
        Assert.Equal("1", DockBadge.Format(1));
        Assert.Equal("99", DockBadge.Format(99));
        Assert.Equal("99+", DockBadge.Format(100));
        Assert.Equal("99+", DockBadge.Format(4321));
    }

    [Fact]
    public void 可见性判定与格式化一致()
    {
        Assert.False(DockBadge.IsVisible(0));
        Assert.True(DockBadge.IsVisible(1));
    }

    [Theory]
    [InlineData("微信(3)", 3)]
    [InlineData("微信（12）", 12)]
    [InlineData("QQ [5]", 5)]
    [InlineData("微信", 0)]
    [InlineData("微信(abc)", 0)]
    [InlineData("会议 2026(10月) 纪要", 0)]
    [InlineData("微信(12345)", 0)]
    [InlineData("微信(0)", 0)]
    [InlineData("", 0)]
    public void 标题未读数只认最后一段纯数字括号(string title, int expected)
    {
        Assert.Equal(expected, DockBadge.ParseCountFromTitle(title));
    }

    [Fact]
    public void 标题里多个括号时取最右的那个()
    {
        Assert.Equal(7, DockBadge.ParseCountFromTitle("群聊(3) 微信(7)"));
    }

    [Fact]
    public void 进度钳制不产生NaN宽度()
    {
        Assert.Equal(0, DockProgressBar.Clamp(double.NaN));
        Assert.Equal(0, DockProgressBar.Clamp(-1));
        Assert.Equal(0, DockProgressBar.Clamp(0));
        Assert.Equal(0.42, DockProgressBar.Clamp(0.42), precision: 6);
        Assert.Equal(1, DockProgressBar.Clamp(7));
        Assert.Equal(0, DockProgressBar.Clamp(double.PositiveInfinity));
    }

    [Fact]
    public void 进度条在图标内居中且不超过图标()
    {
        Assert.Equal(14, DockProgressBar.LeftInset(100), precision: 6);   // (100 - 72)/2
        Assert.Equal(0, DockProgressBar.LeftInset(0), precision: 6);
        Assert.Equal(0, DockProgressBar.LeftInset(-5), precision: 6);
    }

    [Fact]
    public void 不确定态相位循环且对非法输入归零()
    {
        Assert.Equal(0, DockProgressBar.IndeterminatePhase(0));
        Assert.Equal(0.5, DockProgressBar.IndeterminatePhase(700, 1400), precision: 6);
        Assert.Equal(0.25, DockProgressBar.IndeterminatePhase(1750, 1400), precision: 6);   // 超过一圈回绕
        Assert.Equal(0, DockProgressBar.IndeterminatePhase(-1));
        Assert.Equal(0, DockProgressBar.IndeterminatePhase(double.NaN));
        Assert.Equal(0, DockProgressBar.IndeterminatePhase(5, 0));   // 周期非正按 1ms 处理
    }

    [Theory]
    [InlineData("56%", 0.56)]
    [InlineData(" 56 % ", 0.56)]
    [InlineData("0%", 0.0)]
    [InlineData("100%", 1.0)]
    [InlineData("105%", 1.0)]
    [InlineData("56", -1.0)]
    [InlineData("abc%", -1.0)]
    [InlineData("已完成 51%", 0.51)]
    [InlineData("操作进度: 6%；速度: 760 MB/秒", 0.06)]
    [InlineData("已完成 100%", 1.0)]
    [InlineData("12345%", -1.0)]
    [InlineData("", -1.0)]
    [InlineData(null, -1.0)]
    public void 进度文本只认带百分号的纯数字(string? text, double expected)
    {
        var value = DockProgressBar.ParsePercentText(text);
        Assert.Equal(expected, value, precision: 6);
    }
}
