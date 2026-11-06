using Beautiful.Core.Dock;
using Beautiful.Core.Settings;
using Xunit;

/// <summary>
/// 隐藏模式 9 档（对标 MyDockFinder）+ 临时显示修饰键 + 每显示器覆盖配置的设置承载：
/// 默认值（升级兼容：旧配置缺这些键必须落在"行为不变"的默认）、规范化收敛与 JSON 往返。
/// </summary>
public class DockHideModeSettingsTests
{
    // ---------- 默认值（升级兼容红线） ----------

    [Fact]
    public void 新字段默认值_模式off_修饰键ctrl_覆盖列表空()
    {
        var s = new AppSettings();
        Assert.Equal("off", s.DockHideMode);           // 默认常显 = 历史行为
        Assert.Equal("ctrl", s.DockModifierKey);
        Assert.Empty(s.DockMonitorProfiles);           // 单屏机器：无覆盖，行为与现状一致
        Assert.Equal(9, AppSettings.KnownDockHideModes.Count);
        Assert.Equal(new[] { "ctrl", "alt", "shift", "win" }, AppSettings.KnownDockModifierKeys);
    }

    // ---------- 规范化 ----------

    [Fact]
    public void 隐藏模式_大小写空白归一_未知兜off()
    {
        Assert.Equal("auto", new AppSettings { DockHideMode = "AUTO" }.Normalized().DockHideMode);
        Assert.Equal("smart", new AppSettings { DockHideMode = " Smart " }.Normalized().DockHideMode);
        Assert.Equal("fullscreen", new AppSettings { DockHideMode = "fullscreen" }.Normalized().DockHideMode);
        Assert.Equal("maximized", new AppSettings { DockHideMode = "maximized" }.Normalized().DockHideMode);
        Assert.Equal("edge", new AppSettings { DockHideMode = "edge" }.Normalized().DockHideMode);
        Assert.Equal("modifier", new AppSettings { DockHideMode = "modifier" }.Normalized().DockHideMode);
        Assert.Equal("desktop", new AppSettings { DockHideMode = "desktop" }.Normalized().DockHideMode);
        Assert.Equal("dim", new AppSettings { DockHideMode = "dim" }.Normalized().DockHideMode);
        Assert.Equal("off", new AppSettings { DockHideMode = "flick" }.Normalized().DockHideMode);
        Assert.Equal("off", new AppSettings { DockHideMode = null! }.Normalized().DockHideMode);
        Assert.Equal("off", new AppSettings { DockHideMode = "  " }.Normalized().DockHideMode);
    }

    [Fact]
    public void 修饰键_大小写归一_未知兜ctrl()
    {
        Assert.Equal("alt", new AppSettings { DockModifierKey = "ALT" }.Normalized().DockModifierKey);
        Assert.Equal("shift", new AppSettings { DockModifierKey = " shift " }.Normalized().DockModifierKey);
        Assert.Equal("win", new AppSettings { DockModifierKey = "win" }.Normalized().DockModifierKey);
        Assert.Equal("ctrl", new AppSettings { DockModifierKey = "hyper" }.Normalized().DockModifierKey);
        Assert.Equal("ctrl", new AppSettings { DockModifierKey = null! }.Normalized().DockModifierKey);
    }

    [Fact]
    public void 覆盖配置规范化_非法值逐条收敛()
    {
        var s = new AppSettings
        {
            DockMonitorProfiles =
            [
                new() { Monitor = "2", Edge = "left", AutoHide = true, IconSize = 40 },
                new() { Monitor = "auto", Edge = "top", IconSize = 999 },   // 键收敛 primary、边收敛 null、尺寸钳 96
                null!,                                                       // JSON null 字面量条目
            ]
        };
        var list = s.Normalized().DockMonitorProfiles;
        Assert.Equal(2, list.Count);
        Assert.Equal("2", list[0].Monitor);
        Assert.Equal("left", list[0].Edge);
        Assert.True(list[0].AutoHide);
        Assert.Equal(40, list[0].IconSize);
        Assert.Equal("primary", list[1].Monitor);
        Assert.Null(list[1].Edge);
        Assert.Equal(96, list[1].IconSize);
    }

    [Fact]
    public void 新字段Normalized幂等()
    {
        var once = new AppSettings
        {
            DockHideMode = "NOPE",
            DockModifierKey = "HYPER",
            DockMonitorProfiles = [new DockMonitorProfile { Monitor = "x", Edge = "top", IconSize = 3 }]
        }.Normalized();
        // record 生成的等值对 List 属性（DockMonitorProfiles）是引用语义，with 归一后必是新列表实例——
        // 幂等断言拆两刀：除列表外整记录等值（两边列表置空后比较）+ 列表逐条等值
        //（DockMonitorProfile 是 record，元素按值比较）。
        Assert.Equal(once with { DockMonitorProfiles = null! }, once.Normalized() with { DockMonitorProfiles = null! });
        Assert.Equal(once.DockMonitorProfiles, once.Normalized().DockMonitorProfiles);
    }

    // ---------- JSON 往返（配置解析） ----------

    [Theory]
    [InlineData("off")]
    [InlineData("auto")]
    [InlineData("smart")]
    [InlineData("fullscreen")]
    [InlineData("maximized")]
    [InlineData("edge")]
    [InlineData("modifier")]
    [InlineData("desktop")]
    [InlineData("dim")]
    public void 隐藏模式JSON往返(string mode)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { DockHideMode = mode });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal(mode, parsed!.DockHideMode);
    }

    [Fact]
    public void 覆盖配置JSON往返_含null字段()
    {
        var original = new AppSettings
        {
            DockHideMode = "smart",
            DockModifierKey = "win",
            DockMonitorProfiles =
            [
                new DockMonitorProfile { Monitor = "1", Edge = "left", AutoHide = true, IconSize = 40 },
                new DockMonitorProfile { Monitor = "2", Edge = null, AutoHide = null, IconSize = null },
            ]
        };
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(parsed);
        Assert.Equal("smart", parsed!.DockHideMode);
        Assert.Equal("win", parsed.DockModifierKey);
        Assert.Equal(2, parsed.DockMonitorProfiles.Count);
        Assert.Equal("1", parsed.DockMonitorProfiles[0].Monitor);
        Assert.Equal("left", parsed.DockMonitorProfiles[0].Edge);
        Assert.True(parsed.DockMonitorProfiles[0].AutoHide);
        Assert.Equal(40, parsed.DockMonitorProfiles[0].IconSize);
        Assert.Equal("2", parsed.DockMonitorProfiles[1].Monitor);
        Assert.Null(parsed.DockMonitorProfiles[1].Edge);
        Assert.Null(parsed.DockMonitorProfiles[1].AutoHide);
        Assert.Null(parsed.DockMonitorProfiles[1].IconSize);
    }

    [Fact]
    public void 旧配置缺新键_读到代码默认()
    {
        // 旧 settings.json 没有 DockHideMode/DockModifierKey/DockMonitorProfiles 这些键：
        // 反序列化必须落在代码默认（行为不变），不能抛错也不能变 null。
        const string legacy = """{"Theme":"macos","DockIconSize":56}""";
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacy);
        Assert.NotNull(parsed);
        Assert.Equal("off", parsed!.DockHideMode);
        Assert.Equal("ctrl", parsed.DockModifierKey);
        Assert.Empty(parsed.DockMonitorProfiles);
    }
}
