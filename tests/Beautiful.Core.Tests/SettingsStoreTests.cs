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
            HidePanelOnDeactivate = false
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
