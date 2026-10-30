using Beautiful.Core.TopBar;
using Xunit;

/// <summary>v4 Phase 4-T3：真菜单字符串纯函数取证（访问键 / 快捷键 / 空标签）。</summary>
public class MenuMirrorTextTests
{
    [Theory]
    [InlineData("&File", "File", null)]
    [InlineData("Save &As...\tCtrl+Shift+S", "Save As...", "Ctrl+Shift+S")]
    [InlineData("Fish && Chips", "Fish & Chips", null)]
    [InlineData("  &Edit  ", "Edit", null)]
    [InlineData("", "", null)]
    [InlineData(null, "", null)]
    public void 菜单字符串拆分(string? raw, string expectedLabel, string? expectedShortcut)
    {
        var (label, shortcut) = MenuMirrorText.Split(raw);
        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedShortcut, shortcut);
    }
}
