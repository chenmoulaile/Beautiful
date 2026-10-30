using System;
using System.Linq;
using Beautiful.Core.Controls;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// 控制中心的"点了到底改什么"取证（v3 T7）。
/// 面板只做渲染与接线，全部判断（当前哪一项、打开写什么值、哪些砖块在这个主题下真的有意义）
/// 都在这里钉住——否则界面与设置页迟早会对同一个设置项给出两种解释，而那类分歧在截图里看不出来。
/// </summary>
public class QuickSettingsTests
{
    /// <summary>砖块投影的测试入口（v4：只剩 macOS 语义的三块，不再有壁纸/玻璃壳探测参数）。</summary>
    private static IReadOnlyList<QuickTile> Tiles(AppSettings s, ThemeDefinition theme)
        => QuickSettings.ToggleTiles(s, theme);
    // ---------- 分段与白名单同源 ----------

    [Fact]
    public void 主题分段与设置白名单一项不差()
        => Assert.Equal(AppSettings.KnownThemes.OrderBy(x => x), QuickSettings.Themes.Select(t => t.Id).OrderBy(x => x));

    [Fact]
    public void 任务栏策略分段与设置白名单一项不差()
        => Assert.Equal(AppSettings.KnownTaskbarPolicies.OrderBy(x => x),
                       QuickSettings.TaskbarPolicies.Select(t => t.Id).OrderBy(x => x));

    [Fact]
    public void 当前选择恒命中分段里的一项()
    {
        // macOS-only：脏值 "linux" 回落到分段第一项（macos）
        var s = new AppSettings { Theme = "linux", TaskbarPolicy = "keep" };
        Assert.Equal("macos", QuickSettings.SelectedThemeId(s));
        Assert.Equal("keep", QuickSettings.SelectedTaskbarPolicyId(s));
    }

    [Fact]
    public void 手改配置里的非法标识回落第一项而不是让分段一个都不选()
    {
        // AppSettings.Normalized 会先把脏值收敛，但面板拿到的对象未必都过那一步（例如刚 with 出来的中间态）
        var dirty = new AppSettings { Theme = "windows95", TaskbarPolicy = "explode" };
        Assert.Equal(QuickSettings.Themes[0].Id, QuickSettings.SelectedThemeId(dirty));
        Assert.Equal(QuickSettings.TaskbarPolicies[0].Id, QuickSettings.SelectedTaskbarPolicyId(dirty));
    }

    [Fact]
    public void 未知分段标识不改写设置()
    {
        var s = new AppSettings();
        Assert.Equal("macos", s.Theme);                                   // 默认主题
        Assert.Same(s, QuickSettings.WithTheme(s, "harmonyos"));          // 未知标识（harmonyos 已非白名单）：返回原对象不改写
        Assert.Same(s, QuickSettings.WithTheme(s, "not-a-theme"));
        Assert.Same(s, QuickSettings.WithTaskbarPolicy(s, "not-a-policy"));
    }

    // ---------- 放大：强度就是开关 ----------

    [Fact]
    public void 强度落在下限即为关()
    {
        Assert.False(QuickSettings.IsMagnificationOn(new AppSettings { MagnificationStrength = AppSettings.MinMagnificationStrength }));
        Assert.True(QuickSettings.IsMagnificationOn(new AppSettings { MagnificationStrength = 1.4 }));
    }

    [Fact]
    public void 关掉放大落到下限_再打开落到默认倍率()
    {
        // 关掉就是把强度写成 ×1.0 —— 设置里放大强度只有这一个数，"上次是 ×2.4"那一刻就没有存档了。
        // 打开因此回到看得见放大的默认倍率（见 QuickSettings.WithMagnification 的取舍说明），
        // 而不是回到某个凭空的记忆值。
        var s = new AppSettings { MagnificationStrength = 2.4 };
        var off = QuickSettings.WithMagnification(s, on: false);
        Assert.Equal(AppSettings.MinMagnificationStrength, off.MagnificationStrength);
        var on = QuickSettings.WithMagnification(off, on: true);
        Assert.Equal(AppSettings.DefaultMagnificationStrength, on.MagnificationStrength);
        Assert.True(QuickSettings.IsMagnificationOn(on));
    }

    [Fact]
    public void 已经是开的时候再设开不会把用户调好的倍率顶掉()
    {
        var s = new AppSettings { MagnificationStrength = 2.4 };
        Assert.Equal(2.4, QuickSettings.WithMagnification(s, on: true).MagnificationStrength);
    }

    [Fact]
    public void 本来就是关时打开回落到默认强度而不是一个看不见的倍率()
    {
        var s = new AppSettings { MagnificationStrength = AppSettings.MinMagnificationStrength };
        var on = QuickSettings.WithMagnification(s, on: true);
        Assert.Equal(AppSettings.DefaultMagnificationStrength, on.MagnificationStrength);
        Assert.True(QuickSettings.IsMagnificationOn(on));
    }

    [Fact]
    public void 打开关掉的放大后新值仍过设置规范化()
    {
        // 折算出来的强度必须始终落在滑杆合法区间内，否则会出现"当前渲染用 2.6、重启后变 2.5"
        var on = QuickSettings.WithMagnification(new AppSettings { MagnificationStrength = 1.0 }, true);
        Assert.InRange(on.Normalized().MagnificationStrength,
                      AppSettings.MinMagnificationStrength, AppSettings.MaxMagnificationStrength);
    }

    // ---------- 倒影：三态折算 ----------

    [Fact]
    public void 倒影未显式选择时读主题默认()
    {
        var s = new AppSettings { ReflectionEnabled = null };
        Assert.False(QuickSettings.IsReflectionOn(s, ThemeDefinition.MacOs()));      // 三主题默认都关
        Assert.True(QuickSettings.IsReflectionOn(s with { ReflectionEnabled = true }, ThemeDefinition.MacOs()));
        Assert.True(QuickSettings.IsReflectionOn(s, ThemeDefinition.MacOs() with { ReflectionDefault = true }));
    }

    [Fact]
    public void 点一次倒影就把它落成显式值_此后切主题不再冲掉()
    {
        var s = new AppSettings { ReflectionEnabled = null };
        var on = QuickSettings.WithReflection(s, on: true);
        Assert.True(on.ReflectionEnabled);   // 不再是 null（跟随主题）
        // 显式 true 在"默认关"的主题下依然为开：这正是"用户选过"的语义
        Assert.True(QuickSettings.IsReflectionOn(on, ThemeDefinition.MacOs()));
    }

    // ---------- 媒体胶囊 ----------

    [Fact]
    public void 媒体胶囊开关联动设置值()
    {
        var off = QuickSettings.WithMediaCapsule(new AppSettings { ShowMediaCapsule = true }, on: false);
        Assert.False(off.ShowMediaCapsule);
        Assert.False(QuickSettings.IsMediaCapsuleOn(off));
        Assert.True(QuickSettings.IsMediaCapsuleOn(QuickSettings.WithMediaCapsule(off, on: true)));
    }

    // ---------- 砖块投影 ----------

    [Fact]
    public void 不做放大的主题里放大砖块置灰并给出原因()
    {
        // macOS 放大开启（MaxScale=1.8）；用 MaxScale=1.0 模拟"不放大的主题"以验证置灰路径
        var nonMagnifying = ThemeDefinition.MacOs() with { MaxScale = 1.0 };
        var tiles = Tiles(new AppSettings { Theme = "macos" }, nonMagnifying);
        var magnify = QuickSettings.FindTile(tiles, QuickSettings.MagnificationId)!;
        Assert.False(magnify.IsEnabled);
        Assert.NotNull(magnify.BlockReason);
        Assert.Equal("该主题不做悬停放大", magnify.BlockReason);   // 短到能塞进砖块（标题行已写明当前主题）
        Assert.DoesNotContain("关", magnify.Label);                // 标签只是名字，状态另有其位
    }

    [Fact]
    public void 做放大的主题里放大砖块可点且原因留空()
    {
        var tiles = Tiles(new AppSettings { Theme = "macos" }, ThemeDefinition.MacOs());
        var magnify = QuickSettings.FindTile(tiles, QuickSettings.MagnificationId)!;
        Assert.True(magnify.IsEnabled);
        Assert.Null(magnify.BlockReason);
    }

    [Fact]
    public void 倒影对macOS主题可点_因为渲染器真的会画()
    {
        // 设置页把倒影限制在 macOS 是观感建议；控制中心按"能不能真的改变画面"判定，
        // 于是这里不能出现一个"点了没反应"的砖块。
        var theme = ThemeDefinition.MacOs();
        var tile = QuickSettings.FindTile(
            Tiles(new AppSettings { Theme = "macos" }, theme),
            QuickSettings.ReflectionId)!;
        Assert.True(tile.IsEnabled);
        Assert.Null(tile.BlockReason);
    }

    [Fact]
    public void 砖块投影的IsOn恒等于各自的判定()
    {
        var s = new AppSettings { Theme = "macos", ShowMediaCapsule = false, MagnificationStrength = 2.0, ReflectionEnabled = true };
        var theme = ThemeDefinition.MacOs();
        var tiles = Tiles(s, theme);
        Assert.True(QuickSettings.FindTile(tiles, QuickSettings.MagnificationId)!.IsOn);
        Assert.True(QuickSettings.FindTile(tiles, QuickSettings.ReflectionId)!.IsOn);
        Assert.False(QuickSettings.FindTile(tiles, QuickSettings.MediaCapsuleId)!.IsOn);
        Assert.Equal(3, tiles.Count);   // 顺序与数量都是界面的契约：放大/倒影/媒体胶囊
    }

    [Fact]
    public void 未知砖块标识查询返回空而不是伪造一项()
        => Assert.Null(QuickSettings.FindTile(Tiles(new AppSettings(), ThemeDefinition.MacOs()), "brightness"));

    // ---------- 策略说明文案 ----------

    [Fact]
    public void 三种策略都有各自的中文说明()
    {
        Assert.Contains("移出屏幕", QuickSettings.PolicyHint("hide"));
        Assert.Contains("自动隐藏", QuickSettings.PolicyHint("autohide"));
        Assert.Contains("悬停在其上方", QuickSettings.PolicyHint("keep"));
        Assert.Equal(QuickSettings.PolicyHint("hide"), QuickSettings.PolicyHint("手改成的非法值")); // 兜底与默认策略一致
    }
}
