using System;
using Beautiful.Core.Launcher;
using Beautiful.Core.Settings;
using Xunit;

/// <summary>
/// 启动器热键的解析与配置自愈取证（v3 T7）。
/// 核心风险是"全局吞键"：一个没有修饰键的组合会让用户在任何输入框里都敲不出那个键，
/// 所以这里钉的不是"能不能配"，而是"哪一类配置根本进不了设置"。
/// </summary>
public class LauncherHotkeyTests
{
    [Theory]
    [InlineData("ctrl+alt+space", true)]
    [InlineData("CTRL+ALT+SPACE", true)]
    [InlineData("ctrl alt space", true)]
    [InlineData("control+alt+space", true)]
    [InlineData("windows+f2", true)]
    [InlineData("shift+enter", true)]
    [InlineData("ctrl+alt+shift+f12", true)]
    // 下面这些都必须被拒（理由见类型注释的不对称代价）
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("space", false)]                 // 无修饰键 = 吞掉所有应用的空格
    [InlineData("ctrl+space", false)]            // IME 中英切换：吞掉它 = 所有应用里切不了输入法
    [InlineData("win+space", false)]             // 键盘布局切换
    [InlineData("alt+space", false)]             // 前台窗口系统菜单
    [InlineData("j", false)]
    [InlineData("ctrl+", false)]                 // 缺主键
    [InlineData("ctrl+a+b", false)]              // 两个主键
    [InlineData("ctrl+whatever", false)]         // 主键名不在表内
    [InlineData("+++", false)]
    public void 解析只接受一个主键加至少一个修饰键(string? raw, bool expected)
        => Assert.Equal(expected, LauncherHotkey.TryParse(raw, out _));

    [Theory]
    [InlineData("ctrl+j", "j")]
    [InlineData("alt+f12", "f12")]
    [InlineData("win+2", "2")]
    [InlineData("ctrl+shift+enter", "enter")]
    public void 主键名被原样认出来(string raw, string key)
    {
        Assert.True(LauncherHotkey.TryParse(raw, out var gesture));
        Assert.Equal(key, gesture.Key);
    }

    [Fact]
    public void 别名与乱序都归一到同一规范串()
    {
        Assert.Equal("ctrl+alt+space", LauncherHotkey.Canonicalize("Option+Control+Space"));
        Assert.Equal("ctrl+alt+space", LauncherHotkey.Canonicalize(" alt - ctrl - space "));
        Assert.Equal("win+f2", LauncherHotkey.Canonicalize("SUPER+F2"));
    }

    [Fact]
    public void 非法值回落默认而不是保留脏串()
    {
        Assert.Equal(LauncherHotkey.Default, LauncherHotkey.Canonicalize("space"));
        Assert.Equal(LauncherHotkey.Default, LauncherHotkey.Canonicalize(null));
        Assert.Equal(LauncherHotkey.Default, LauncherHotkey.Canonicalize("垃圾"));
    }

    [Fact]
    public void 默认组合不含系统已占用的绑定()
    {
        // Win+Space = 输入法切换，Ctrl+Space = IME 中英切换：这两个绝不能成为默认值
        var gesture = LauncherHotkey.Default;
        Assert.DoesNotContain("win", gesture.Split('+'));
        Assert.Equal("ctrl+alt+space", gesture);
    }

    [Fact]
    public void 设置默认值与Core默认同源()
        => Assert.Equal(LauncherHotkey.Default, new AppSettings().LauncherHotkey);

    [Fact]
    public void 手改配置里的非法热键被Normalized挡住()
    {
        var healed = new AppSettings { LauncherHotkey = "space" }.Normalized();
        Assert.Equal(LauncherHotkey.Default, healed.LauncherHotkey);

        // 终审补：v2 遗留 / 手改的"单修饰键 + 空格"形式上合法，但那是 IME / 布局 / 系统菜单的
        // 系统绑定，启动器要配对吞键 → 绝不能用它。这条是钩子真的注册热键之后才有的代价。
        foreach (var reserved in new[] { "ctrl+space", "win+space", "alt+space" })
            Assert.Equal(LauncherHotkey.Default, new AppSettings { LauncherHotkey = reserved }.Normalized().LauncherHotkey);

        // 双修饰键的空格组合仍合法（默认值本身就是它，不能被上面那条误伤）
        Assert.Equal("ctrl+alt+space", new AppSettings { LauncherHotkey = "CTRL+ALT+SPACE" }.Normalized().LauncherHotkey);

        var legal = new AppSettings { LauncherHotkey = "  Alt + Ctrl + J " }.Normalized();
        Assert.Equal("ctrl+alt+j", legal.LauncherHotkey);
    }
}
