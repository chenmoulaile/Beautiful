using System.Text.Json;
using Beautiful.Core.Imaging;
using Beautiful.Core.Settings;
using Xunit;

/// <summary>
/// 墙纸取色（极致美化①）的设置承载：默认值（升级兼容红线：默认关，5.0.0 开箱观感不变）、
/// 规范化钳制与 JSON 往返（与 ScreenCornerSettingsTests 同一套口径）。
/// </summary>
public class WallpaperTintSettingsTests
{
    // ---------- 默认值（升级兼容红线：默认一律是"历史行为"） ----------

    [Fact]
    public void 新键默认值_取色关_强度60()
    {
        var s = new AppSettings();
        Assert.False(s.WallpaperTintEnabled);                                       // 默认关
        Assert.Equal(60, s.WallpaperTintStrength);
        Assert.Equal(WallpaperTint.DefaultStrength, s.WallpaperTintStrength);       // 与 Core 常量同源
    }

    // ---------- 规范化 ----------

    [Fact]
    public void 强度_区间内原样_越界钳到0到100()
    {
        Assert.Equal(60, new AppSettings { WallpaperTintStrength = 60 }.Normalized().WallpaperTintStrength);
        Assert.Equal(0, new AppSettings { WallpaperTintStrength = 0 }.Normalized().WallpaperTintStrength);
        Assert.Equal(100, new AppSettings { WallpaperTintStrength = 100 }.Normalized().WallpaperTintStrength);
        Assert.Equal(0, new AppSettings { WallpaperTintStrength = -5 }.Normalized().WallpaperTintStrength);
        Assert.Equal(100, new AppSettings { WallpaperTintStrength = 500 }.Normalized().WallpaperTintStrength);
    }

    [Fact]
    public void 开关布尔不参与规范化_由JSON异常兜底()
    {
        // 布尔字段的承载口径（AppSettings.Normalized 注释）：JSON 写成非布尔会触发 JsonException，
        // 由 AppSettingsStore 按损坏文件处理——规范化层不碰布尔，这里只锁默认值的往返。
        Assert.False(new AppSettings { WallpaperTintEnabled = false }.Normalized().WallpaperTintEnabled);
        Assert.True(new AppSettings { WallpaperTintEnabled = true }.Normalized().WallpaperTintEnabled);
    }

    // ---------- JSON 往返（System.Text.Json 与 AppSettingsStore 同一序列化口径） ----------

    [Fact]
    public void 两个新键可经JSON往返()
    {
        var original = new AppSettings { WallpaperTintEnabled = true, WallpaperTintStrength = 85 };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.True(restored.WallpaperTintEnabled);
        Assert.Equal(85, restored.WallpaperTintStrength);
    }

    [Fact]
    public void 旧配置缺新键时按默认反序列化()
    {
        const string legacyJson = """{"Theme":"macos","Appearance":"dark"}""";
        var restored = JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(restored);
        Assert.False(restored.WallpaperTintEnabled);       // 旧配置没有这个键 → 行为不变
        Assert.Equal(60, restored.WallpaperTintStrength);
    }
}
