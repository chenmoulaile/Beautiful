using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock「一格 = 一个应用」的分组键。键算错的后果不是报错，而是把两个不同应用悄悄并成一格
/// （或该并的没并、Dock 继续冗长），所以口径必须由单测钉死。
/// </summary>
public class DockAppKeyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData(@"D:\Apps\Microsoft VS Code\Code.exe", "code")]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("CHROME.EXE", "chrome")]
    [InlineData(@"D:\QQ\QQ.exe", "qq")]
    public void Exe_路径折成小写基名(string path, string expected)
        => Assert.Equal(expected, DockAppKey.Of(path));

    [Theory]
    [InlineData(@"C:\Users\x\Desktop\File Explorer.lnk", "explorer")]
    [InlineData("File Explorer.lnk", "explorer")]
    [InlineData("fileexplorer.lnk", "explorer")]
    public void Windows_资源管理器快捷方式_与_explorer_同键(string path, string expected)
        => Assert.Equal(expected, DockAppKey.Of(path));

    [Theory]
    [InlineData("builtin:finder")]
    [InlineData("builtin:launchpad")]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}")]
    [InlineData("shell:RecycleBinFolder")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 内置格与非应用源_不参与合并(string? source)
        => Assert.Equal(string.Empty, DockAppKey.Of(source));

    [Fact]
    public void 同一应用的窗口与固定项_键相等()
        => Assert.Equal(DockAppKey.Of(@"D:\Apps\chrome.exe"),
                        DockAppKey.Of(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));

    [Fact]
    public void 不同应用_键不等()
        => Assert.NotEqual(DockAppKey.Of(@"D:\chrome.exe"), DockAppKey.Of(@"D:\msedge.exe"));

    [Fact]
    public void 带参数的伪路径_不产生怪键()
        => Assert.Equal(string.Empty, DockAppKey.Of(@"C:\Windows\explorer.exe shell:AppsFolder"));
}
