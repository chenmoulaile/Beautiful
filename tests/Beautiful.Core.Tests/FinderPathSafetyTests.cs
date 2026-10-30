using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 路径安全测试（Phase 6-b 纯函数）。
/// <para>
/// 判定根、尾随分隔符、大小写与 UNC 共享根都是容易出错的地方；路径只经
/// <see cref="System.IO.Path.GetFullPath(string)"/> 规范化，不做任何存在性检查。
/// 前缀陷阱（<c>C:\A</c> 与 <c>C:\AB</c>）必须靠“祖先 + 分隔符”比较排除。
/// </para>
/// </summary>
public class FinderPathSafetyTests
{
    // ---------- IsSameOrDescendant ----------

    [Theory]
    [InlineData(@"C:\Users\Alice", @"C:\Users\Alice", true)]
    [InlineData(@"c:\users\alice", @"C:\Users\Alice", true)]
    [InlineData(@"C:\Users\Alice\", @"C:\Users\Alice", true)]
    [InlineData(@"C:\Users\Alice\Docs", @"C:\Users\Alice", true)]
    [InlineData(@"C:\Users\Alice\Docs\", @"C:\Users\Alice\", true)]
    [InlineData(@"C:\Users\Alice\..\Bob", @"C:\Users\Bob", true)]
    [InlineData(@"C:/Users/Alice", @"C:\Users", true)]
    [InlineData(@"C:\Windows", @"C:\", true)]
    [InlineData(@"C:\", @"C:\", true)]
    public void 同路径或子路径_不区分大小写与尾随分隔符(string candidate, string ancestor, bool expected)
    {
        Assert.Equal(expected, FinderPathSafety.IsSameOrDescendant(candidate, ancestor));
    }

    [Theory]
    [InlineData(@"C:\Users\Alice", @"C:\Users\Alice2", false)]
    [InlineData(@"C:\Users\Alice2", @"C:\Users\Alice", false)]
    [InlineData(@"C:\Users", @"C:\Users\Alice", false)]
    [InlineData(@"D:\Users\Alice", @"C:\Users", false)]
    public void 兄弟或前缀相近路径_不算后代(string candidate, string ancestor, bool expected)
    {
        Assert.Equal(expected, FinderPathSafety.IsSameOrDescendant(candidate, ancestor));
    }

    [Theory]
    [InlineData(@"\\server\share", @"\\server\share", true)]
    [InlineData(@"\\server\share\folder", @"\\server\share", true)]
    [InlineData(@"\\SERVER\SHARE\Folder", @"\\server\share", true)]
    [InlineData(@"\\server\share\folder\", @"\\server\share\", true)]
    [InlineData(@"\\server\share2", @"\\server\share", false)]
    [InlineData(@"\\server\share", @"\\server\share\folder", false)]
    public void UNC共享根_按共享边界比较(string candidate, string ancestor, bool expected)
    {
        Assert.Equal(expected, FinderPathSafety.IsSameOrDescendant(candidate, ancestor));
    }

    [Theory]
    [InlineData(null, @"C:\Users")]
    [InlineData("", @"C:\Users")]
    [InlineData("   ", @"C:\Users")]
    [InlineData(@"C:\Users", null)]
    [InlineData(@"C:\Users", "")]
    [InlineData("C:\\bad\u0000name", @"C:\Users")]
    public void 无效路径_返回false(string? candidate, string? ancestor)
    {
        Assert.False(FinderPathSafety.IsSameOrDescendant(candidate!, ancestor!));
    }

    // ---------- CanMoveInto ----------

    [Theory]
    [InlineData(@"C:\A\file.txt", @"C:\B", true)]
    [InlineData(@"C:\A\file.txt", @"C:\A", true)]
    [InlineData(@"C:\A\B", @"C:\A", true)]
    [InlineData(@"C:\A", @"C:\B", true)]
    [InlineData(@"C:\A", @"C:\AB", true)]
    [InlineData(@"C:\A", @"C:\A", false)]
    [InlineData(@"C:\A", @"C:\A\", false)]
    [InlineData(@"C:\A", @"C:\A\B\C", false)]
    [InlineData(@"C:\A", @"c:\a\b", false)]
    public void 移动目标_目录不能进自身或后代_文件不受限(
        string source,
        string target,
        bool expected)
    {
        var sourceIsDirectory = !source.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, FinderPathSafety.CanMoveInto(source, sourceIsDirectory, target));
    }

    [Theory]
    [InlineData(null, false, @"C:\A")]
    [InlineData("", false, @"C:\A")]
    [InlineData(@"C:\A\file.txt", false, null)]
    [InlineData(@"C:\A\file.txt", false, "   ")]
    [InlineData("C:\\bad\u0000name", true, @"C:\A")]
    [InlineData(@"C:\A", true, "C:\\bad\u0000name")]
    public void 移动目标_无效路径一律false(string? source, bool sourceIsDirectory, string? target)
    {
        Assert.False(FinderPathSafety.CanMoveInto(source!, sourceIsDirectory, target!));
    }
}
