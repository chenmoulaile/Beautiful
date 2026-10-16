using Beautiful.Core.TopBar;
using Xunit;

/// <summary>
/// "窗口"菜单文字压缩规则测试。
/// <para>
/// 用户反馈"点击窗口出现的菜单太大，缩小一点"。菜单宽度由最长的那条标题决定，
/// 而 Windows 应用的标题常是"完整路径 + 文件名 • 工程 - 编辑器"。这里锁的是
/// "留哪一段"的判断：文件名优先于目录、编辑器装饰段先丢、最后才截断。
/// </para>
/// </summary>
public class WindowMenuLabelTests
{
    [Fact]
    public void 编辑器长标题只留文件名()
    {
        // 用户真机上实测到的那条 Sublime 标题
        const string raw =
            @"D:\Code_Source\MoniSai\2026 CSP-S 模拟赛 Day 17 (Div 2)\draft.cpp • (Code_Source) - Sublime Text (UNREGISTERED)";
        Assert.Equal("draft.cpp", WindowMenuLabel.Compact(raw));
    }

    [Fact]
    public void 资源管理器类标题也只留最后一段()
    {
        Assert.Equal("z.txt", WindowMenuLabel.Compact(@"C:\Users\chenk\Documents\z.txt"));
    }

    [Fact]
    public void 浏览器标题没有路径就原样保留()
    {
        const string title = "2026 CSP-S 复赛模拟赛 Day 8 - Google Chrome";
        Assert.Equal(title, WindowMenuLabel.Compact(title));
    }

    [Fact]
    public void 超长且无路径时截断并保留开头()
    {
        var longTitle = new string('甲', 90);
        var compacted = WindowMenuLabel.Compact(longTitle);

        Assert.Equal(WindowMenuLabel.DefaultMaxChars, compacted.Length);
        Assert.EndsWith("…", compacted);
        Assert.StartsWith("甲甲甲", compacted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空标题给占位而不是空行(string? title)
        => Assert.Equal(WindowMenuLabel.Untitled, WindowMenuLabel.Compact(title));

    [Fact]
    public void 路径以分隔符结尾时不炸()
    {
        // 只有目录、没有文件名：这时"最后一段"是空的，退回整串而不是吐一个空条目
        var result = WindowMenuLabel.Compact(@"C:\Users\chenk\");
        Assert.Equal(@"C:\Users\chenk\", result);
    }

    [Fact]
    public void 装饰段只在前面有内容时才丢()
    {
        // 标题本身就以 " • " 开头时不能丢成空串
        Assert.Equal("• 只有装饰符", WindowMenuLabel.Compact(" \u2022 只有装饰符"));
    }

    [Fact]
    public void 多余空白折叠成单个空格()
        => Assert.Equal("a b - c", WindowMenuLabel.Compact("  a   b  -  c  "));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2)]
    public void 过小的长度上限被兜住(int maxChars)
    {
        var result = WindowMenuLabel.Compact("abcdefghij", maxChars);
        Assert.True(result.Length >= 4 && result.Length <= 5, $"实际长度 {result.Length}");
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void 自定义上限生效()
        => Assert.Equal("2026 CSP-S 复赛…", WindowMenuLabel.Compact("2026 CSP-S 复赛模拟赛 Day 8 - Google Chrome", 14));
}
