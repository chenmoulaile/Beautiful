using System.Text.Json;
using Beautiful.Core.Settings;
using Xunit;

public class AppSettingsTests
{
    [Fact]
    public void 默认值_macOS_隐藏任务栏_不自启_偏移0_胶囊与失焦收起开启()
    {
        var s = new AppSettings();
        Assert.Equal("macos", s.Theme);
        Assert.Equal("dark", s.Appearance);
        Assert.Equal("blue", s.AccentColor);
        Assert.True(s.NativeModules);
        Assert.True(s.NativeBlur);
        Assert.False(s.NativeTrayBridge);   // Phase 2 注入默认关：真机演练回执后才允许默认开
        Assert.Equal("auto", s.MaterialQuality);
        Assert.True(s.FullscreenAvoidanceEnabled);
        Assert.Equal("hide", s.TaskbarPolicy);
        Assert.False(s.Autostart);
        Assert.True(s.ShowMediaCapsule);
        Assert.True(s.HidePanelOnDeactivate);
    }

    [Fact]
    public void 未知Theme或任务栏策略_收敛为默认值()
    {
        Assert.Equal("macos", new AppSettings { Theme = "mac0s" }.Normalized().Theme);
        Assert.Equal("macos", new AppSettings { Theme = "macos" }.Normalized().Theme);
        Assert.Equal("macos", new AppSettings { Theme = "linux" }.Normalized().Theme);
        Assert.Equal("macos", new AppSettings { Theme = "HARMONYOS" }.Normalized().Theme);
        Assert.Equal("auto", new AppSettings { Appearance = "AUTO" }.Normalized().Appearance);
        Assert.Equal("dark", new AppSettings { Appearance = "sepia" }.Normalized().Appearance);
        Assert.Equal("purple", new AppSettings { AccentColor = " PURPLE " }.Normalized().AccentColor);
        Assert.Equal("blue", new AppSettings { AccentColor = "chartreuse" }.Normalized().AccentColor);
        Assert.Equal("live", new AppSettings { MaterialQuality = "LIVE" }.Normalized().MaterialQuality);
        Assert.Equal("auto", new AppSettings { MaterialQuality = "turbo" }.Normalized().MaterialQuality);
        Assert.Equal("hide", new AppSettings { TaskbarPolicy = "always" }.Normalized().TaskbarPolicy);
        Assert.Equal("autohide", new AppSettings { TaskbarPolicy = "AutoHide" }.Normalized().TaskbarPolicy);
        Assert.Equal("keep", new AppSettings { TaskbarPolicy = "keep" }.Normalized().TaskbarPolicy);
    }

    [Fact]
    public void v2默认值_顶栏切换器预览弹跳开_倒影关_图标56_放大1点7_底部_热键默认()
    {
        var s = new AppSettings();
        Assert.True(s.TopBarEnabled);
        Assert.True(s.SwitcherEnabled);
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Equal("alt+tab", s.SwitcherHotkey);
        Assert.Equal(56, s.DockIconSize);
        Assert.Equal(4, s.DockEdgeMargin);
        Assert.Equal(1.7, s.MagnificationStrength);
        Assert.Equal("bottom", s.DockPosition);
        Assert.True(s.PreviewEnabled);
        Assert.True(s.BounceEnabled);
        Assert.Null(s.ReflectionEnabled);   // v3 T4 三态：null = 跟随主题默认（不再由设置硬关/硬开）
        Assert.True(s.FullscreenPassthrough);
        Assert.Equal("ctrl+alt+space", s.LauncherHotkey);
        Assert.True(s.LauncherEnabled);       // v3 T7：启动器默认开（关掉它等于没有这个功能）
        Assert.False(s.AutoHideDock);
    }

    [Fact]
    public void v2数值字段_区间内与边界值_原样保留()
    {
        Assert.Equal(40, new AppSettings { DockIconSize = 40 }.Normalized().DockIconSize);
        Assert.Equal(32, new AppSettings { DockIconSize = 32 }.Normalized().DockIconSize);
        Assert.Equal(96, new AppSettings { DockIconSize = 96 }.Normalized().DockIconSize);
        Assert.Equal(2.2, new AppSettings { MagnificationStrength = 2.2 }.Normalized().MagnificationStrength);
        Assert.Equal(1.0, new AppSettings { MagnificationStrength = 1.0 }.Normalized().MagnificationStrength);
        Assert.Equal(2.5, new AppSettings { MagnificationStrength = 2.5 }.Normalized().MagnificationStrength);
    }

    [Fact]
    public void v2数值字段_越界钳制_图标32到72_放大1点0到2点5()
    {
        Assert.Equal(96, new AppSettings { DockIconSize = 200 }.Normalized().DockIconSize);
        Assert.Equal(24, new AppSettings { DockEdgeMargin = 40 }.Normalized().DockEdgeMargin);
        Assert.Equal(0, new AppSettings { DockEdgeMargin = -3 }.Normalized().DockEdgeMargin);
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
        Assert.Equal("macos", new AppSettings { SwitcherStyle = "Linux" }.Normalized().SwitcherStyle);
        Assert.Equal("macos", new AppSettings { SwitcherStyle = "  HARMONYOS  " }.Normalized().SwitcherStyle);
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
        Assert.Equal("ctrl+alt+space", nulled.LauncherHotkey);

        var blank = new AppSettings { SwitcherStyle = "   ", DockPosition = "", SwitcherHotkey = "  ", LauncherHotkey = "" }.Normalized();
        Assert.Equal("macos", blank.SwitcherStyle);
        Assert.Equal("bottom", blank.DockPosition);
        Assert.Equal("alt+tab", blank.SwitcherHotkey);
        Assert.Equal("ctrl+alt+space", blank.LauncherHotkey);
    }

    [Fact]
    public void Normalized幂等_钳制与收敛后再跑一次不变()
    {
        var once = new AppSettings
        {
            DockIconSize = 500, MagnificationStrength = 12, SwitcherStyle = "weird",
            DockPosition = "x", Theme = "nope", TaskbarPolicy = "nope",
            SwitcherHotkey = null!, LauncherHotkey = " "
        }.Normalized();
        Assert.Equal(once, once.Normalized());
    }

    [Fact]
    public void 写入即规范化_滑杆合法值原样_越界值即时钳回()
    {
        // SettingsService.Update/UpdateAndSave 现内部过 Normalized()（Task 1 评审遗留修复）：
        // 内存态 Current 与落盘态同源，滑杆写出的越界值须**立即**钳回，而不是等重启 Load 才改。
        // 合法区间值不得被改写（滑杆拖动逐帧 Update，若合法值也被规范化扰动会破坏实时预览）。
        var ok = new AppSettings { DockIconSize = 60, MagnificationStrength = 2.0 };
        Assert.Equal(ok, ok.Normalized());

        var bad = new AppSettings { DockIconSize = 200, MagnificationStrength = 9 }.Normalized();
        Assert.Equal(96, bad.DockIconSize);         // 图标尺寸上限
        Assert.Equal(2.5, bad.MagnificationStrength); // 放大强度上限
    }

    [Fact]
    public void v3默认值_Dock与顶栏显示器均为auto()
    {
        var s = new AppSettings();
        Assert.Equal("auto", s.DockMonitor);
        Assert.Equal("auto", s.TopBarMonitor);
    }

    [Fact]
    public void 显示器选择器_关键字与序号归一_非法回落auto()
    {
        // 关键字：大小写与首尾空白归一
        Assert.Equal("auto", new AppSettings { DockMonitor = "AUTO" }.Normalized().DockMonitor);
        Assert.Equal("primary", new AppSettings { TopBarMonitor = "  Primary " }.Normalized().TopBarMonitor);
        // 序号（1 起，与设置面板列出的顺序一致）："2 "/" 2" 归一成裸十进制串
        Assert.Equal("2", new AppSettings { DockMonitor = " 2 " }.Normalized().DockMonitor);
        Assert.Equal("3", new AppSettings { TopBarMonitor = "3" }.Normalized().TopBarMonitor);
        Assert.Equal("16", new AppSettings { DockMonitor = "16" }.Normalized().DockMonitor);   // MaxMonitorIndex 边界
        // 越界/非整数/未知词/null/空白 → auto（在屏几块显示器只有运行时知道，那层回落由 App 侧再做）
        Assert.Equal("auto", new AppSettings { DockMonitor = "17" }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { DockMonitor = "0" }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { DockMonitor = "-1" }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { DockMonitor = "2.5" }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { DockMonitor = "second" }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { DockMonitor = null! }.Normalized().DockMonitor);
        Assert.Equal("auto", new AppSettings { TopBarMonitor = "   " }.Normalized().TopBarMonitor);
    }

    [Fact]
    public void 显示器选择器_Normalized幂等()
    {
        var once = new AppSettings { DockMonitor = " 2 ", TopBarMonitor = "PRIMARY" }.Normalized();
        Assert.Equal(once, once.Normalized());
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
            Theme = "macos",
            Appearance = "light",
            AccentColor = "purple",
            TaskbarPolicy = "keep",
            Autostart = true,
            ShowMediaCapsule = false,
            HidePanelOnDeactivate = false,
            TopBarEnabled = false,
            SwitcherEnabled = false,
            SwitcherStyle = "macos",
            SwitcherHotkey = "ctrl+alt+tab",
            DockIconSize = 64,
            MagnificationStrength = 2.2,
            DockPosition = "left",
            PreviewEnabled = false,
            BounceEnabled = false,
            ReflectionEnabled = true,
            FullscreenPassthrough = false,
            // 样本必须用**合法**热键：本测试验的是"存的和读的回一致"，而 LauncherHotkey 落盘前要过
            // LauncherHotkey.Canonicalize（终审补：win+space / ctrl+space / alt+space 这类系统绑定
            // 会被自愈成默认组合，用它当样本就永远等不上）。
            LauncherHotkey = "ctrl+alt+f5",
            AutoHideDock = true,
            DockMonitor = "2",
            TopBarMonitor = "primary"
        };
        _store.Save(original);
        Assert.Equal(original, _store.Load());
    }

    [Fact]
    public void 旧主题与已删除字段_加载自愈并逐条报告迁移()
    {
        // v4 Phase 1：三主题/歌词/玻璃壳/壁纸取色的持久化字段被删除。旧文件必须
        // 1) 值域自愈为 macos，2) 已删除字段只忽略不报错、不删用户的其余配置，
        // 3) 每条自愈都有一条可 grep 的中文证据（App 侧把回调接到 Logger.Info）。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"linux","SwitcherStyle":"harmonyos","LyricsOffsetMs":120,"TaskbarGlass":false,"UseWallpaperAccent":true}""");
        var notes = new List<string>();
        var s = _store.Load(notes.Add);
        Assert.Equal("macos", s.Theme);
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Contains(notes, n => n.Contains("Theme=linux"));
        Assert.Contains(notes, n => n.Contains("LyricsOffsetMs"));
        Assert.Contains(notes, n => n.Contains("TaskbarGlass"));
        Assert.Contains(notes, n => n.Contains("UseWallpaperAccent"));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
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
            """{"Theme":"macos","DockIconSize":60}""");
        var s = _store.Load();
        Assert.Equal("macos", s.Theme);
        Assert.Equal(60, s.DockIconSize);
        Assert.Equal("hide", s.TaskbarPolicy);   // 缺省 → 默认
        Assert.True(s.ShowMediaCapsule);
        Assert.True(s.HidePanelOnDeactivate);
        Assert.False(s.Autostart);
    }

    [Fact]
    public void v1旧配置JSON_缺省v2字段_加载取v2代码默认()
    {
        // 升级兼容：v1 已有用户的 settings.json 不含 v2 字段，加载后须落在 v2 代码默认而不是 false/0
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"macos","TaskbarPolicy":"hide","Autostart":true,"ShowMediaCapsule":false,"HidePanelOnDeactivate":true}""");
        var s = _store.Load();
        Assert.Equal("macos", s.Theme);
        Assert.True(s.TopBarEnabled);
        Assert.True(s.SwitcherEnabled);
        Assert.Equal("macos", s.SwitcherStyle);
        Assert.Equal("alt+tab", s.SwitcherHotkey);
        Assert.Equal(56, s.DockIconSize);
        Assert.Equal(4, s.DockEdgeMargin);
        Assert.Equal(1.7, s.MagnificationStrength);
        Assert.Equal("bottom", s.DockPosition);
        Assert.True(s.PreviewEnabled);
        Assert.True(s.BounceEnabled);
        Assert.Null(s.ReflectionEnabled);   // v3 T4 三态：null = 跟随主题默认（不再由设置硬关/硬开）
        Assert.True(s.FullscreenPassthrough);
        Assert.Equal("ctrl+alt+space", s.LauncherHotkey);
        Assert.True(s.LauncherEnabled);       // v3 T7：启动器默认开（关掉它等于没有这个功能）
        Assert.False(s.AutoHideDock);
        // v3.1 新增的顶栏时钟四项：旧文件里没有这些键，必须落在"与 v3.0.0 观感一致"的代码默认，
        // 而不是布尔零值（零值会把日期/星期/24 小时制全关掉，顶栏就只剩一个光秃秃的时间）。
        Assert.False(s.ClockShowSeconds);
        Assert.True(s.ClockShowDate);
        Assert.True(s.ClockShowWeekday);
        Assert.True(s.ClockUse24Hour);
    }

    [Fact]
    public void 顶栏时钟四项_写入后原样读回()
    {
        Directory.CreateDirectory(_dir);
        _store.Save(new AppSettings
        {
            ClockShowSeconds = true,
            ClockShowDate = false,
            ClockShowWeekday = true,
            ClockUse24Hour = false
        });
        var s = _store.Load();
        Assert.True(s.ClockShowSeconds);
        Assert.False(s.ClockShowDate);
        Assert.True(s.ClockShowWeekday);
        Assert.False(s.ClockUse24Hour);
    }

    [Fact]
    public void 顶栏时钟非布尔JSON_按损坏文件自愈回代码默认()
    {
        // 布尔字段没有"钳制"可言：类型不符即 JsonException → 整体自愈（删文件 + 代码默认）。
        // 这条钉住"不会留下半套值、也不会把异常抛到调用方"。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"ClockShowSeconds":"yes","Theme":"macos"}""");
        var s = _store.Load();
        Assert.Equal("macos", s.Theme);
        Assert.False(s.ClockShowSeconds);
        Assert.True(s.ClockShowDate);
    }

    [Fact]
    public void v2数值字段越界JSON_加载时钳制到区间()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"DockIconSize":200,"MagnificationStrength":9}""");
        var s = _store.Load();
        Assert.Equal(96, s.DockIconSize);
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
    public void v2旧配置JSON_缺省v3显示器字段_加载取auto()
    {
        // 升级兼容：v2 用户的 settings.json 不含 v3 新字段，加载后须落在"auto"而不是 null/空串
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"macos","DockIconSize":56}""");
        var s = _store.Load();
        Assert.Equal("auto", s.DockMonitor);
        Assert.Equal("auto", s.TopBarMonitor);
    }

    [Fact]
    public void 显示器选择器越界或null的JSON_收敛auto且不删文件()
    {
        // 值域问题由 Normalized 兜底，不是结构损坏 → 自愈网不得删掉用户的其余配置
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"DockMonitor":null,"TopBarMonitor":"999","Theme":"linux"}""");
        var s = _store.Load();
        Assert.Equal("auto", s.DockMonitor);
        Assert.Equal("auto", s.TopBarMonitor);
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
            """{"DockIconSize":"abc"}""");
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
