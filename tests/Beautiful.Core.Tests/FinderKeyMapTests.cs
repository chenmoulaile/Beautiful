using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 键盘手势映射测试（Phase 6-a）。
/// <para>
/// 手势串统一规范化：去空白、转小写、按 <c>+</c> 分段、修饰键顺序无关。Ctrl 在 macOS 语义下
/// 就是 ⌘（Command），因此 <c>⌘+c</c> 与 <c>ctrl+c</c> 必须得到同一命令。
/// </para>
/// </summary>
public class FinderKeyMapTests
{
    [Theory]
    [InlineData("ctrl+c", FinderCommand.Copy)]
    [InlineData("ctrl+x", FinderCommand.Cut)]
    [InlineData("ctrl+v", FinderCommand.Paste)]
    [InlineData("ctrl+a", FinderCommand.SelectAll)]
    [InlineData("ctrl+n", FinderCommand.NewWindow)]
    [InlineData("ctrl+shift+n", FinderCommand.NewFolder)]
    [InlineData("ctrl+t", FinderCommand.NewTab)]
    [InlineData("ctrl+w", FinderCommand.CloseTab)]
    [InlineData("ctrl+[", FinderCommand.Back)]
    [InlineData("ctrl+]", FinderCommand.Forward)]
    [InlineData("ctrl+1", FinderCommand.IconView)]
    [InlineData("ctrl+2", FinderCommand.ListView)]
    [InlineData("ctrl+3", FinderCommand.ColumnView)]
    [InlineData("ctrl+4", FinderCommand.GalleryView)]
    [InlineData("enter", FinderCommand.Rename)]
    [InlineData("f2", FinderCommand.Rename)]
    [InlineData("backspace", FinderCommand.Delete)]
    [InlineData("shift+backspace", FinderCommand.EmptyTrash)]
    [InlineData("space", FinderCommand.QuickLook)]
    [InlineData("ctrl+shift+.", FinderCommand.ToggleHidden)]
    [InlineData("ctrl+shift+g", FinderCommand.GoToFolder)]
    [InlineData("ctrl+f", FinderCommand.Search)]
    [InlineData("ctrl+i", FinderCommand.GetInfo)]
    [InlineData("escape", FinderCommand.Cancel)]
    public void 既定手势_映射到对应命令(string gesture, FinderCommand expected)
    {
        Assert.Equal(expected, FinderKeyMap.Map(gesture));
    }

    [Theory]
    [InlineData("shift+ctrl+n")]
    [InlineData("ctrl + SHIFT + n")]
    [InlineData("  N + Shift + Ctrl  ")]
    [InlineData("SHIFT+CTRL+N")]
    public void 修饰键顺序与空白大小写_不影响映射(string gesture)
    {
        Assert.Equal(FinderCommand.NewFolder, FinderKeyMap.Map(gesture));
    }

    [Fact]
    public void 无修饰键手势_同样允许空白与大小写()
    {
        Assert.Equal(FinderCommand.Rename, FinderKeyMap.Map("  ENTER "));
        Assert.Equal(FinderCommand.Delete, FinderKeyMap.Map("Backspace"));
        Assert.Equal(FinderCommand.Cancel, FinderKeyMap.Map("ESCape"));
    }

    [Theory]
    [InlineData("⌘+c", FinderCommand.Copy)]
    [InlineData("⌘+Shift+N", FinderCommand.NewFolder)]
    [InlineData("Command+C", FinderCommand.Copy)]
    [InlineData("cmd+c", FinderCommand.Copy)]
    [InlineData("control+c", FinderCommand.Copy)]
    public void Ctrl等价于Command键(string gesture, FinderCommand expected)
    {
        Assert.Equal(expected, FinderKeyMap.Map(gesture));
    }

    [Fact]
    public void 已定义组合的乱序写法仍正确()
    {
        Assert.Equal(FinderCommand.EmptyTrash, FinderKeyMap.Map("backspace+shift"));
    }

    [Fact]
    public void 未定义组合不能误落到相邻命令()
    {
        Assert.Equal(FinderCommand.None, FinderKeyMap.Map("ctrl+shift+w"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+q")]
    [InlineData("ctrl+shift")]
    [InlineData("ctrl+alt+n")]
    [InlineData("+")]
    public void 未知或空手势_返回None(string? gesture)
    {
        Assert.Equal(FinderCommand.None, FinderKeyMap.Map(gesture));
    }
}
