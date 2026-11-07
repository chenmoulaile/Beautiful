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

// ---------- 每屏排除名单（alpha.29 残项清零，Core ScreenCornerMonitors） ----------

public class ScreenCornerMonitorsSettingsTests
{
    [Fact]
    public void 默认值_空名单_全部屏幕画圆角()
        => Assert.Empty(new AppSettings().ScreenCornersMonitors);

    [Theory]
    [InlineData("primary", "primary")]
    [InlineData(" PRIMARY ", "primary")]
    [InlineData("2", "2")]
    [InlineData(" 2.0 ", "2")]
    [InlineData("auto", null)]
    [InlineData("17", null)]     // 超出 16 台护栏
    [InlineData("junk", null)]
    [InlineData("", null)]
    public void 单键规范化_primary与序号保留_非法丢弃(string raw, string? expected)
    {
        var key = Beautiful.Core.Layout.ScreenCornerMonitors.CanonicalKey(raw);
        Assert.Equal(expected, key);
    }

    [Fact]
    public void 列表规范化_去重保序_丢弃null与非法键()
    {
        var normalized = Beautiful.Core.Layout.ScreenCornerMonitors.Normalized(
            ["primary", null!, "2", "2", "junk", "PRIMARY", "3"]);
        Assert.Equal(new[] { "primary", "2", "3" }, normalized);
    }

    [Fact]
    public void 空名单或全非法_收敛为空_不排除任何屏()
    {
        Assert.Empty(Beautiful.Core.Layout.ScreenCornerMonitors.Normalized(null));
        Assert.Empty(Beautiful.Core.Layout.ScreenCornerMonitors.Normalized(["junk", "auto"]));
        Assert.False(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded([], 2, isPrimary: true));
    }

    [Fact]
    public void 匹配_序号精确优先_primary仅当真是主屏()
    {
        var keys = Beautiful.Core.Layout.ScreenCornerMonitors.Normalized(["primary", "2"]);
        Assert.True(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded(keys, 2, isPrimary: false));
        Assert.True(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded(keys, 1, isPrimary: true));
        Assert.False(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded(keys, 1, isPrimary: false)); // 非主屏不沾 primary 键
        Assert.False(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded(keys, 3, isPrimary: false));
        // 序号越界钳制：手改 JSON 写 99 不会抛（被钳回 16 的语义只在序列化键比对层；16 不在名单内不排除。
        // primary 匹配与 DockMonitorProfiles 同一套：仅看 isPrimary 旗标，与序号是否越界无关）
        Assert.False(Beautiful.Core.Layout.ScreenCornerMonitors.IsExcluded(keys, 99, isPrimary: false));
    }

    [Fact]
    public void 排除名单JSON往返_键原样保留()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new AppSettings { ScreenCornersMonitors = ["primary", "2"], ScreenCornersEnabled = true });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal(new[] { "primary", "2" }, parsed!.ScreenCornersMonitors);
    }
}
