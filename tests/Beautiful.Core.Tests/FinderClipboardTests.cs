using System;
using System.Linq;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 粘贴计划测试（Phase 6-c）。
/// <para>
/// 这里钉死的是 macOS 与 Windows 资源管理器真正不同的那几条：复制到同一目录要生成
/// <c>name copy.ext</c>（不是 <c>name (1).ext</c>）、剪切回原目录必须什么都不做、
/// 文件夹不能粘进自己的子孙、源消失后要跳过而不是让执行层去撞异常。
/// </para>
/// </summary>
public class FinderClipboardTests
{
    private const string Dir = @"C:\Work";

    private static string[] Names(params string[] names) => names;

    [Fact]
    public void 剪贴板构造会去空去重并保持顺序()
    {
        var clipboard = FinderClipboard.Of(
            [@"C:\A\one.txt", "  ", @"c:\a\ONE.txt\", @"C:\A\two.txt"],
            FinderClipboardMode.Copy);

        Assert.Equal(2, clipboard.Paths.Count);
        Assert.Equal(@"C:\A\one.txt", clipboard.Paths[0]);
        Assert.Equal(@"C:\A\two.txt", clipboard.Paths[1]);
        Assert.False(clipboard.IsEmpty);
    }

    [Fact]
    public void 空剪贴板不产生计划()
    {
        Assert.Empty(FinderPastePlanner.Plan(FinderClipboard.Empty, Dir));
        Assert.Empty(FinderPastePlanner.Plan(null, Dir));
        Assert.True(FinderClipboard.Empty.IsEmpty);
    }

    [Fact]
    public void 目标目录为空抛ArgumentException()
    {
        var clipboard = FinderClipboard.Of([@"C:\A\one.txt"], FinderClipboardMode.Copy);

        Assert.Throws<ArgumentException>(() => FinderPastePlanner.Plan(clipboard, "  "));
    }

    [Fact]
    public void 复制到别的目录保留原名()
    {
        var clipboard = FinderClipboard.Of([@"C:\A\one.txt", @"C:\A\dir"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(clipboard, Dir, Names("other.txt"));

        Assert.All(plan, item => Assert.Equal(FinderPasteStatus.Ready, item.Status));
        Assert.Equal(@"C:\Work\one.txt", plan[0].TargetPath);
        Assert.False(plan[0].Renamed);
        Assert.Equal(@"C:\Work\dir", plan[1].TargetPath);
    }

    [Fact]
    public void 复制到同一目录生成macOS风格的copy名()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\report.docx"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(clipboard, Dir, Names("report.docx"));

        Assert.Equal(FinderPasteStatus.Ready, plan[0].Status);
        Assert.Equal("report copy.docx", plan[0].FinalName);
        Assert.True(plan[0].Renamed);
        Assert.Equal(@"C:\Work\report copy.docx", plan[0].TargetPath);
    }

    [Fact]
    public void 同一目录已有copy名时继续避让()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\a.txt"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(clipboard, Dir, Names("a.txt", "a copy.txt"));

        Assert.Equal("a copy (1).txt", plan[0].FinalName);
    }

    [Fact]
    public void 剪切回原目录被判定为无需执行()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\a.txt"], FinderClipboardMode.Cut);

        var plan = FinderPastePlanner.Plan(clipboard, Dir);

        Assert.Equal(FinderPasteStatus.SameDirectory, plan[0].Status);
        Assert.False(plan[0].WillExecute);
    }

    [Fact]
    public void 文件夹不能粘进自己的子孙目录()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\docs"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(
            clipboard,
            @"C:\Work\docs\2026",
            isDirectory: _ => true);

        Assert.Equal(FinderPasteStatus.IntoItself, plan[0].Status);
    }

    [Fact]
    public void 文件按文件处理时不做粘进自身判定()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\docs"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(
            clipboard,
            @"C:\Work\docs\2026",
            isDirectory: _ => false);

        Assert.Equal(FinderPasteStatus.Ready, plan[0].Status);
    }

    [Fact]
    public void 源已消失的项被标记为Missing且不给目标路径()
    {
        var clipboard = FinderClipboard.Of([@"C:\A\gone.txt", @"C:\A\here.txt"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(clipboard, Dir, exists: p => p.EndsWith("here.txt", StringComparison.Ordinal));

        Assert.Equal(FinderPasteStatus.Missing, plan[0].Status);
        Assert.Equal(string.Empty, plan[0].TargetPath);
        Assert.Equal(FinderPasteStatus.Ready, plan[1].Status);
    }

    [Fact]
    public void 同名多源按顺序分配序号()
    {
        var clipboard = FinderClipboard.Of([@"C:\A\x\same.txt", @"C:\B\y\same.txt"], FinderClipboardMode.Copy);

        var plan = FinderPastePlanner.Plan(clipboard, Dir);

        Assert.Equal("same.txt", plan[0].FinalName);
        Assert.Equal("same (1).txt", plan[1].FinalName);
        Assert.All(plan, item => Assert.Equal(FinderPasteStatus.Ready, item.Status));
    }

    [Fact]
    public void 盘符根这类没有名字的源被跳过()
    {
        var clipboard = FinderClipboard.Of([@"C:\"], FinderClipboardMode.Copy);

        Assert.Empty(FinderPastePlanner.Plan(clipboard, Dir));
    }

    [Fact]
    public void 计划项的可执行标记只对Ready为真()
    {
        var clipboard = FinderClipboard.Of([@"C:\Work\a.txt"], FinderClipboardMode.Cut);
        var plan = FinderPastePlanner.Plan(clipboard, Dir);

        Assert.False(plan.Single().WillExecute);
    }
}
