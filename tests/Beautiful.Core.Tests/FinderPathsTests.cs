using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 路径纯函数测试（Phase 6-a）。
/// <para>
/// 这里只做字符串运算，不碰文件系统：盘符根、UNC 共享根、尾随分隔符与空路径是四类容易写错的边界。
/// 尤其 UNC：<c>\\server\share</c> 本身就是根，不能再往上一级退到 <c>\\server</c>。
/// </para>
/// </summary>
public class FinderPathsTests
{
    // ---------- Parent ----------

    [Theory]
    [InlineData(@"C:\Users\a", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"C:\Users\a\", @"C:\Users")]
    [InlineData(@"C:\Users\a\\", @"C:\Users")]
    [InlineData(@"C:\Users\", @"C:\")]
    [InlineData(@"\\server\share\folder", @"\\server\share")]
    [InlineData(@"\\server\share\folder\sub", @"\\server\share\folder")]
    [InlineData(@"\\server\share\folder\", @"\\server\share")]
    public void 父目录_去掉最后一段并保留根形态(string path, string expected)
    {
        Assert.Equal(expected, FinderPaths.Parent(path));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 根与空路径_没有父目录(string? path)
    {
        Assert.Null(FinderPaths.Parent(path));
    }

    [Fact]
    public void 父目录_相对路径只按分隔符退一级()
    {
        Assert.Equal("folder", FinderPaths.Parent(@"folder\sub"));
        Assert.Null(FinderPaths.Parent("folder"));
        Assert.Null(FinderPaths.Parent(@"\"));
    }

    // ---------- IsRoot ----------

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\", true)]
    [InlineData(@"\\server\share\\", true)]
    [InlineData(@"\\server\share\folder", false)]
    [InlineData(@"\\server", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void 根判定_仅盘符根与UNC共享根(string? path, bool expected)
    {
        Assert.Equal(expected, FinderPaths.IsRoot(path));
    }

    // ---------- DisplayName ----------

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"C:\Users", "Users")]
    [InlineData(@"C:\Users\a", "a")]
    [InlineData(@"C:\Users\a\", "a")]
    [InlineData(@"C:\Users\a\\", "a")]
    [InlineData(@"\\server\share", "share")]
    [InlineData(@"\\server\share\", "share")]
    [InlineData(@"\\server\share\folder", "folder")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void 显示名_根显示盘符或共享名_普通路径显示末段(string? path, string expected)
    {
        Assert.Equal(expected, FinderPaths.DisplayName(path));
    }

    // ---------- Combine ----------

    [Theory]
    [InlineData(@"C:\Users", "Documents", @"C:\Users\Documents")]
    [InlineData(@"C:\Users\", "Documents", @"C:\Users\Documents")]
    [InlineData(@"C:\Users\\", "Documents", @"C:\Users\Documents")]
    [InlineData(@"C:\", "Windows", @"C:\Windows")]
    [InlineData(@"\\server\share", "folder", @"\\server\share\folder")]
    [InlineData(@"\\server\share\", "folder", @"\\server\share\folder")]
    [InlineData("", "Documents", "Documents")]
    public void 合并路径_处理尾随分隔符与根(string dir, string name, string expected)
    {
        Assert.Equal(expected, FinderPaths.Combine(dir, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 合并路径_空名称抛ArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => FinderPaths.Combine(@"C:\Users", name!));
    }
}
