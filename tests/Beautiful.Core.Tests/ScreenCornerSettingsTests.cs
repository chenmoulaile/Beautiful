using Beautiful.Core.Settings;
using Xunit;

/// <summary>
/// 屏幕圆角 + 自动隐藏可配化（对标 MyDockFinder ScreenRound / 悬停触发延迟 / 边缘二次触发）
/// 的设置承载：默认值（升级兼容：旧配置缺这些键必须落在"行为不变"的默认）、规范化钳制与 JSON 往返。
/// </summary>
public class ScreenCornerSettingsTests
{
    // ---------- 默认值（升级兼容红线：默认一律是"历史行为"） ----------

    [Fact]
    public void 新字段默认值_圆角关_半径12_延迟500_唤出touch()
    {
        var s = new AppSettings();
        Assert.False(s.ScreenCornersEnabled);                              // 默认关
        Assert.Equal(12, s.ScreenCornerRadius);
        Assert.Equal(500, s.DockAutoHideDelayMs);                          // = 旧硬编码值
        Assert.Equal("touch", s.DockAutoHideReveal);                       // = 历史行为
    }

    // ---------- 规范化 ----------

    [Fact]
    public void 圆角半径_区间内原样_越界钳到4到48()
    {
        Assert.Equal(12, new AppSettings { ScreenCornerRadius = 12 }.Normalized().ScreenCornerRadius);
        Assert.Equal(4, new AppSettings { ScreenCornerRadius = 4 }.Normalized().ScreenCornerRadius);
        Assert.Equal(48, new AppSettings { ScreenCornerRadius = 48 }.Normalized().ScreenCornerRadius);
        Assert.Equal(4, new AppSettings { ScreenCornerRadius = 1 }.Normalized().ScreenCornerRadius);
        Assert.Equal(4, new AppSettings { ScreenCornerRadius = -20 }.Normalized().ScreenCornerRadius);
        Assert.Equal(48, new AppSettings { ScreenCornerRadius = 500 }.Normalized().ScreenCornerRadius);
    }

    [Fact]
    public void 隐藏延迟_区间内原样_越界钳到0到5000()
    {
        Assert.Equal(800, new AppSettings { DockAutoHideDelayMs = 800 }.Normalized().DockAutoHideDelayMs);
        Assert.Equal(0, new AppSettings { DockAutoHideDelayMs = 0 }.Normalized().DockAutoHideDelayMs);
        Assert.Equal(0, new AppSettings { DockAutoHideDelayMs = -5 }.Normalized().DockAutoHideDelayMs);
        Assert.Equal(5000, new AppSettings { DockAutoHideDelayMs = 5000 }.Normalized().DockAutoHideDelayMs);
        Assert.Equal(5000, new AppSettings { DockAutoHideDelayMs = 99_999 }.Normalized().DockAutoHideDelayMs);
    }

    [Fact]
    public void 唤出方式_大小写与空白归一_未知值兜touch()
    {
        Assert.Equal("touch", new AppSettings { DockAutoHideReveal = "TOUCH" }.Normalized().DockAutoHideReveal);
        Assert.Equal("dwell", new AppSettings { DockAutoHideReveal = " Dwell " }.Normalized().DockAutoHideReveal);
        Assert.Equal("double", new AppSettings { DockAutoHideReveal = "double" }.Normalized().DockAutoHideReveal);
        Assert.Equal("touch", new AppSettings { DockAutoHideReveal = "flick" }.Normalized().DockAutoHideReveal);
        Assert.Equal("touch", new AppSettings { DockAutoHideReveal = null! }.Normalized().DockAutoHideReveal);
        Assert.Equal("touch", new AppSettings { DockAutoHideReveal = "   " }.Normalized().DockAutoHideReveal);
    }

    [Fact]
    public void 新字段Normalized幂等()
    {
        var once = new AppSettings
        {
            ScreenCornersEnabled = true,
            ScreenCornerRadius = 999,
            DockAutoHideDelayMs = -1,
            DockAutoHideReveal = "NOPE"
        }.Normalized();
        Assert.Equal(once, once.Normalized());
    }

    // ---------- 遮罩颜色 ----------

    [Fact]
    public void 遮罩颜色默认纯黑_升级兼容()
    {
        Assert.Equal(Beautiful.Core.Layout.ScreenCornerPlan.DefaultMaskColor, new AppSettings().ScreenCornerMaskColor);
        Assert.Equal("#FF000000", new AppSettings().ScreenCornerMaskColor);
    }

    [Fact]
    public void 遮罩颜色Normalized收敛_非法回落默认()
    {
        Assert.Equal("#FF000000", new AppSettings { ScreenCornerMaskColor = "#000000" }.Normalized().ScreenCornerMaskColor);
        Assert.Equal("#80101010", new AppSettings { ScreenCornerMaskColor = "#80101010" }.Normalized().ScreenCornerMaskColor);
        Assert.Equal("#FFFF8000", new AppSettings { ScreenCornerMaskColor = "#ff8000" }.Normalized().ScreenCornerMaskColor);
        Assert.Equal("#FF000000", new AppSettings { ScreenCornerMaskColor = "nope" }.Normalized().ScreenCornerMaskColor);
        Assert.Equal("#FF000000", new AppSettings { ScreenCornerMaskColor = null! }.Normalized().ScreenCornerMaskColor);
    }

    [Fact]
    public void 遮罩颜色JSON往返()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { ScreenCornerMaskColor = "#80101010" });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal("#80101010", parsed!.ScreenCornerMaskColor);
    }

    // ---------- JSON 往返（配置解析） ----------

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(48)]
    public void 圆角半径JSON往返(int radius)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { ScreenCornerRadius = radius });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal(radius, parsed!.ScreenCornerRadius);
    }

    [Theory]
    [InlineData("touch")]
    [InlineData("dwell")]
    [InlineData("double")]
    public void 唤出方式与延迟JSON往返(string reveal)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new AppSettings { DockAutoHideReveal = reveal, DockAutoHideDelayMs = 1200, ScreenCornersEnabled = true });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal(reveal, parsed!.DockAutoHideReveal);
        Assert.Equal(1200, parsed.DockAutoHideDelayMs);
        Assert.True(parsed.ScreenCornersEnabled);
    }
}
