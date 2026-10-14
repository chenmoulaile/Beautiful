using System.Text.Json;
using Beautiful.Core.Settings;
using Xunit;

public class AppSettingsTests
{
    [Fact]
    public void 默认值_鸿蒙_隐藏任务栏_不自启_偏移0_胶囊与失焦收起开启()
    {
        var s = new AppSettings();
        Assert.Equal("harmonyos", s.Theme);
        Assert.Equal("hide", s.TaskbarPolicy);
        Assert.False(s.Autostart);
        Assert.Equal(0, s.LyricsOffsetMs);
        Assert.True(s.ShowMediaCapsule);
        Assert.True(s.HidePanelOnDeactivate);
    }

    [Fact]
    public void Offset收敛_合法范围内原样_越界钳制到正负2000()
    {
        Assert.Equal(0, new AppSettings { LyricsOffsetMs = 0 }.Normalized().LyricsOffsetMs);
        Assert.Equal(2000, new AppSettings { LyricsOffsetMs = 2000 }.Normalized().LyricsOffsetMs);
        Assert.Equal(-2000, new AppSettings { LyricsOffsetMs = -2000 }.Normalized().LyricsOffsetMs);
        Assert.Equal(2000, new AppSettings { LyricsOffsetMs = 5000 }.Normalized().LyricsOffsetMs);
        Assert.Equal(-2000, new AppSettings { LyricsOffsetMs = -5000 }.Normalized().LyricsOffsetMs);
        Assert.Equal(150, new AppSettings { LyricsOffsetMs = 150 }.Normalized().LyricsOffsetMs);
    }

    [Fact]
    public void 未知Theme或任务栏策略_收敛为默认值()
    {
        Assert.Equal("harmonyos", new AppSettings { Theme = "mac0s" }.Normalized().Theme);
        Assert.Equal("macos", new AppSettings { Theme = "macos" }.Normalized().Theme);
        Assert.Equal("linux", new AppSettings { Theme = "linux" }.Normalized().Theme);
        Assert.Equal("harmonyos", new AppSettings { Theme = "HARMONYOS" }.Normalized().Theme);
        Assert.Equal("hide", new AppSettings { TaskbarPolicy = "always" }.Normalized().TaskbarPolicy);
        Assert.Equal("autohide", new AppSettings { TaskbarPolicy = "AutoHide" }.Normalized().TaskbarPolicy);
        Assert.Equal("keep", new AppSettings { TaskbarPolicy = "keep" }.Normalized().TaskbarPolicy);
    }

    [Fact]
    public void v2默认值_顶栏切换器预览弹跳开_倒影关_图标48_放大1点8_底部_热键默认()
    {
        var s = new AppSettings();
        Assert.True(s.TopBarEnabled);
        Assert.True(s.SwitcherEnabled);
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Equal("alt+tab", s.SwitcherHotkey);
        Assert.Equal(48, s.DockIconSize);
        Assert.Equal(1.8, s.MagnificationStrength);
        Assert.Equal("bottom", s.DockPosition);
        Assert.True(s.PreviewEnabled);
        Assert.True(s.BounceEnabled);
        Assert.False(s.ReflectionEnabled);
        Assert.True(s.FullscreenPassthrough);
        Assert.Equal("ctrl+space", s.LauncherHotkey);
        Assert.False(s.AutoHideDock);
    }

    [Fact]
    public void v2数值字段_区间内与边界值_原样保留()
    {
        Assert.Equal(40, new AppSettings { DockIconSize = 40 }.Normalized().DockIconSize);
        Assert.Equal(32, new AppSettings { DockIconSize = 32 }.Normalized().DockIconSize);
        Assert.Equal(72, new AppSettings { DockIconSize = 72 }.Normalized().DockIconSize);
        Assert.Equal(2.2, new AppSettings { MagnificationStrength = 2.2 }.Normalized().MagnificationStrength);
        Assert.Equal(1.0, new AppSettings { MagnificationStrength = 1.0 }.Normalized().MagnificationStrength);
        Assert.Equal(2.5, new AppSettings { MagnificationStrength = 2.5 }.Normalized().MagnificationStrength);
    }

    [Fact]
    public void v2数值字段_越界钳制_图标32到72_放大1点0到2点5()
    {
        Assert.Equal(72, new AppSettings { DockIconSize = 200 }.Normalized().DockIconSize);
        Assert.Equal(32, new AppSettings { DockIconSize = 8 }.Normalized().DockIconSize);
        Assert.Equal(32, new AppSettings { DockIconSize = -100 }.Normalized().DockIconSize);
        Assert.Equal(2.5, new AppSettings { MagnificationStrength = 9 }.Normalized().MagnificationStrength);
        Assert.Equal(1.0, new AppSettings { MagnificationStrength = 0.2 }.Normalized().MagnificationStrength);
        Assert.Equal(1.0, new AppSettings { MagnificationStrength = -5 }.Normalized().MagnificationStrength);
    }

    [Fact]
    public void v2未知样式或停靠位_收敛为默认_大小写与首尾空白归一()
    {
        Assert.Equal("macos", new AppSettings { SwitcherStyle = "weird" }.Normalized().SwitcherStyle);
        Assert.Equal("linux", new AppSettings { SwitcherStyle = "Linux" }.Normalized().SwitcherStyle);
        Assert.Equal("harmonyos", new AppSettings { SwitcherStyle = "  HARMONYOS  " }.Normalized().SwitcherStyle);
        Assert.Equal("bottom", new AppSettings { DockPosition = "x" }.Normalized().DockPosition);
        Assert.Equal("left", new AppSettings { DockPosition = "LEFT" }.Normalized().DockPosition);
        Assert.Equal("bottom", new AppSettings { DockPosition = "diagonal" }.Normalized().DockPosition);
    }

    [Fact]
    public void v2字符串字段null或空白_Normalized兜默认且不抛异常()
    {
        // 手改 JSON 可把 string 写成 null（STJ 填进非可空属性）；空白串等同未配置
        var nulled = new AppSettings
        {
            SwitcherStyle = null!, DockPosition = null!, SwitcherHotkey = null!, LauncherHotkey = null!
        }.Normalized();
        Assert.Equal("macos", nulled.SwitcherStyle);
        Assert.Equal("bottom", nulled.DockPosition);
        Assert.Equal("alt+tab", nulled.SwitcherHotkey);
        Assert.Equal("ctrl+space", nulled.LauncherHotkey);

        var blank = new AppSettings { SwitcherStyle = "   ", DockPosition = "", SwitcherHotkey = "  ", LauncherHotkey = "" }.Normalized();
        Assert.Equal("macos", blank.SwitcherStyle);
        Assert.Equal("bottom", blank.DockPosition);
        Assert.Equal("alt+tab", blank.SwitcherHotkey);
        Assert.Equal("ctrl+space", blank.LauncherHotkey);
    }

    [Fact]
    public void Normalized幂等_钳制与收敛后再跑一次不变()
    {
        var once = new AppSettings
        {
            DockIconSize = 500, MagnificationStrength = 12, SwitcherStyle = "weird",
            DockPosition = "x", Theme = "nope", TaskbarPolicy = "nope",
            SwitcherHotkey = null!, LauncherHotkey = " ", LyricsOffsetMs = 99999
        }.Normalized();
        Assert.Equal(once, once.Normalized());
    }

    [Fact]
    public void 写入即规范化_滑杆合法值原样_越界值即时钳回()
    {
        // SettingsService.Update/UpdateAndSave 现内部过 Normalized()（Task 1 评审遗留修复）：
        // 内存态 Current 与落盘态同源，滑杆写出的越界值须**立即**钳回，而不是等重启 Load 才改。
        // 合法区间值不得被改写（滑杆拖动逐帧 Update，若合法值也被规范化扰动会破坏实时预览）。
        var ok = new AppSettings { DockIconSize = 60, MagnificationStrength = 2.0, LyricsOffsetMs = 800 };
        Assert.Equal(ok, ok.Normalized());

        var bad = new AppSettings { DockIconSize = 200, MagnificationStrength = 9, LyricsOffsetMs = 5000 }.Normalized();
        Assert.Equal(72, bad.DockIconSize);         // 图标尺寸上限
        Assert.Equal(2.5, bad.MagnificationStrength); // 放大强度上限
        Assert.Equal(2000, bad.LyricsOffsetMs);     // 歌词偏移上限
    }
}

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AppSettingsStore _store;

    public AppSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beautiful-settings-tests", Guid.NewGuid().ToString("N"));
        _store = new AppSettingsStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void 缺失文件_Load返回默认值且不创建文件()
    {
        var s = _store.Load();
        Assert.Equal(new AppSettings(), s);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void Save后Load_全字段往返一致()
    {
        var original = new AppSettings
        {
            Theme = "linux",
            TaskbarPolicy = "keep",
            Autostart = true,
            LyricsOffsetMs = -350,
            ShowMediaCapsule = false,
            HidePanelOnDeactivate = false,
            TopBarEnabled = false,
            SwitcherEnabled = false,
            SwitcherStyle = "harmonyos",
            SwitcherHotkey = "ctrl+alt+tab",
            DockIconSize = 64,
            MagnificationStrength = 2.2,
            DockPosition = "left",
            PreviewEnabled = false,
            BounceEnabled = false,
            ReflectionEnabled = true,
            FullscreenPassthrough = false,
            LauncherHotkey = "win+space",
            AutoHideDock = true
        };
        _store.Save(original);
        Assert.Equal(original, _store.Load());
    }

    [Fact]
    public void Save自动创建缺失的配置目录()
    {
        Assert.False(Directory.Exists(_dir));
        _store.Save(new AppSettings());
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void 损坏JSON_Load返回默认值并删除文件()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ 这不是 JSON !!!");
        Assert.Equal(new AppSettings(), _store.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void JSONnull字面量_Load视为损坏返回默认()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "null");
        Assert.Equal(new AppSettings(), _store.Load());
    }

    [Fact]
    public void 字符串字段null的JSON_Load兜默认且不删文件()
    {
        // 手改 JSON 可写 {"Theme":null}：STJ 会把 null 填进非可空 string 属性，
        // 规范化须兜默认而不是 NRE 漏出（Load 的 catch 不含 NRE，漏出即启动崩溃循环）；
        // 字符串 null 不是结构损坏 → 保留文件（自愈不删数据）。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":null,"TaskbarPolicy":null}""");
        var s = _store.Load();
        Assert.Equal(new AppSettings(), s);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void 部分字段JSON_缺省字段取代码默认()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"macos","LyricsOffsetMs":250}""");
        var s = _store.Load();
        Assert.Equal("macos", s.Theme);
        Assert.Equal(250, s.LyricsOffsetMs);
        Assert.Equal("hide", s.TaskbarPolicy);   // 缺省 → 默认
        Assert.True(s.ShowMediaCapsule);
        Assert.True(s.HidePanelOnDeactivate);
        Assert.False(s.Autostart);
    }

    [Fact]
    public void Offset越界JSON_加载时钳制到正负2000()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"LyricsOffsetMs":5000}""");
        Assert.Equal(2000, _store.Load().LyricsOffsetMs);

        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"LyricsOffsetMs":-5000}""");
        Assert.Equal(-2000, _store.Load().LyricsOffsetMs);
    }

    [Fact]
    public void v1旧配置JSON_缺省v2字段_加载取v2代码默认()
    {
        // 升级兼容：v1 已有用户的 settings.json 不含 v2 字段，加载后须落在 v2 代码默认而不是 false/0
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"macos","TaskbarPolicy":"hide","Autostart":true,"LyricsOffsetMs":-20,"ShowMediaCapsule":false,"HidePanelOnDeactivate":true}""");
        var s = _store.Load();
        Assert.Equal("macos", s.Theme);
        Assert.True(s.TopBarEnabled);
        Assert.True(s.SwitcherEnabled);
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Equal("alt+tab", s.SwitcherHotkey);
        Assert.Equal(48, s.DockIconSize);
        Assert.Equal(1.8, s.MagnificationStrength);
        Assert.Equal("bottom", s.DockPosition);
        Assert.True(s.PreviewEnabled);
        Assert.True(s.BounceEnabled);
        Assert.False(s.ReflectionEnabled);
        Assert.True(s.FullscreenPassthrough);
        Assert.Equal("ctrl+space", s.LauncherHotkey);
        Assert.False(s.AutoHideDock);
    }

    [Fact]
    public void v2数值字段越界JSON_加载时钳制到区间()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"DockIconSize":200,"MagnificationStrength":9}""");
        var s = _store.Load();
        Assert.Equal(72, s.DockIconSize);
        Assert.Equal(2.5, s.MagnificationStrength);

        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"DockIconSize":10,"MagnificationStrength":0.5}""");
        s = _store.Load();
        Assert.Equal(32, s.DockIconSize);
        Assert.Equal(1.0, s.MagnificationStrength);
    }

    [Fact]
    public void v2未知枚举串JSON_收敛默认且不删文件()
    {
        // 值域问题由 Normalized 兜底，不是结构损坏 → 自愈网不删用户的其余配置
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"SwitcherStyle":"weird","DockPosition":"x","Theme":"macos"}""");
        var s = _store.Load();
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Equal("bottom", s.DockPosition);
        Assert.Equal("macos", s.Theme);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void v2字符串字段null的JSON_Load兜默认且不删文件()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"SwitcherStyle":null,"DockPosition":null,"SwitcherHotkey":null,"LauncherHotkey":null}""");
        var s = _store.Load();
        Assert.Equal(new AppSettings(), s);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void v2布尔字段类型错误的JSON_视为损坏恢复默认并删除()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"TopBarEnabled":"yes"}""");
        Assert.Equal(new AppSettings(), _store.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void 类型错误的JSON_视为损坏恢复默认并删除()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"LyricsOffsetMs":"abc"}""");
        Assert.Equal(new AppSettings(), _store.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void 序列化不包含PinnedApps字段_固定应用归dock_json()
    {
        // 控制器裁定 1：固定应用只存在于 config/dock.json，AppSettings 不得携带 PinnedApps
        var json = JsonSerializer.Serialize(new AppSettings());
        Assert.DoesNotContain("PinnedApps", json, StringComparison.OrdinalIgnoreCase);
        var type = typeof(AppSettings);
        Assert.DoesNotContain(type.GetProperties(), p =>
            p.Name.Contains("Pinned", StringComparison.OrdinalIgnoreCase));
    }
}
