using Beautiful.Core.Icons;
using Xunit;

/// <summary>
/// 快捷方式显示名 → Tahoe 图标键。钉住的是「不能错画」：没有对应 PNG 的名字必须返回 null，
/// 否则把 Chrome 画成 Safari、把普通程序画成访达，比留着系统图标更糟。
/// </summary>
public class MacDisplayNameTests
{
    [Theory]
    [InlineData(@"C:\Users\x\Desktop\Visual Studio Code.lnk", "code")]
    [InlineData("Visual Studio Code.lnk", "code")]
    [InlineData("Windows Terminal.lnk", "windowsterminal")]
    [InlineData("Telegram Desktop.lnk", "telegram")]
    [InlineData("Microsoft Edge.lnk", "msedge")]
    [InlineData("File Explorer.lnk", "explorer")]
    [InlineData("Notepad.lnk", "notepad")]
    [InlineData("网易云音乐.lnk", "cloudmusic")]
    [InlineData("微信.lnk", "wechat")]
    [InlineData("WPS Office.lnk", "wps")]
    [InlineData("Steam.lnk", "steam")]
    [InlineData("GitHub Desktop.lnk", "githubdesktop")]
    [InlineData("计算器.lnk", "calculator")]
    [InlineData("设置.lnk", "system_settings")]
    public void 常见快捷方式命中对应图标键(string name, string expected)
        => Assert.Equal(expected, MacDisplayName.KeyFor(name));

    [Theory]
    [InlineData("Sublime Text.lnk")]
    [InlineData("Google Chrome.lnk")]
    [InlineData("某个没见过的程序.lnk")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 没有对应图标的名字返回空_交回系统图标(string? name)
        => Assert.Null(MacDisplayName.KeyFor(name));

    [Fact]
    public void 全角与后缀不影响匹配()
        => Assert.Equal("code", MacDisplayName.KeyFor("Ｖｉｓｕａｌ Ｓｔｕｄｉｏ Ｃｏｄｅ - 快捷方式.lnk"));

    [Fact]
    public void 带盘符路径只取末段()
        => Assert.Equal("telegram", MacDisplayName.KeyFor(@"D:\Apps\Telegram Desktop.exe"));
}
