using System;
using System.Collections.Generic;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 自然排序比较器测试（Phase 6-a）。
/// <para>
/// Finder 名称列必须"file2 排在 file10 前面"，同时大小写差异不能让排序抖动：
/// 非数字段先用 <see cref="StringComparison.OrdinalIgnoreCase"/> 比较，数字段按数值比较，
/// 全部等价时才用 <see cref="StringComparison.Ordinal"/> 做最终决胜。这里把每条口径都钉死，
/// 避免以后换成 <c>StringComparer.CurrentCulture</c> 后在不同机器上出现不同次序。
/// </para>
/// </summary>
public class NaturalStringComparerTests
{
    private static readonly IComparer<string> Cmp = NaturalStringComparer.Instance;

    [Fact]
    public void 数字段按数值比较_而不是逐字符比较()
    {
        Assert.True(Cmp.Compare("file2", "file10") < 0);
        Assert.True(Cmp.Compare("file10", "file2") > 0);
        Assert.True(Cmp.Compare("2", "10") < 0);
        Assert.True(Cmp.Compare("10", "2") > 0);
    }

    [Fact]
    public void 非数字段忽略大小写_数字段仍按数值决胜()
    {
        // 前缀大小写不同不影响后面的数字比较：大小写不是主排序键。
        Assert.True(Cmp.Compare("File2", "File10") < 0);
        Assert.True(Cmp.Compare("FILENAME10", "filename2") > 0);
        Assert.True(Cmp.Compare("filename2", "FILENAME10") < 0);

        var names = new[] { "FILENAME10", "filename2", "FileName1" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "FileName1", "filename2", "FILENAME10" }, names);
    }

    [Fact]
    public void 大小写仅作最终决胜_方向确定且可逆()
    {
        // 忽略大小写后完全相等时，Ordinal 让大写字母排在小写字母之前。
        Assert.True(Cmp.Compare("Apple", "apple") < 0);
        Assert.True(Cmp.Compare("apple", "Apple") > 0);
        Assert.Equal(0, Cmp.Compare("Apple", "Apple"));
        Assert.Equal(0, Cmp.Compare("apple", "apple"));

        var names = new[] { "apple", "Apple", "APPLE" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "APPLE", "Apple", "apple" }, names);
    }

    [Fact]
    public void 前导零不参与数值_但并列时按原始串序数决胜()
    {
        // 数值都是 2；原始串 "file002" < "file02" < "file2"（Ordinal）。
        Assert.True(Cmp.Compare("file02", "file2") < 0);
        Assert.True(Cmp.Compare("file2", "file02") > 0);
        Assert.True(Cmp.Compare("file002", "file02") < 0);

        var names = new[] { "file10", "file2", "file02", "file002", "file1" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "file1", "file002", "file02", "file2", "file10" }, names);
    }

    [Fact]
    public void null排在最前_空串排在非空串前_且左右对称()
    {
        Assert.Equal(0, Cmp.Compare(null, null));
        Assert.True(Cmp.Compare(null, "") < 0);
        Assert.True(Cmp.Compare("", null) > 0);
        Assert.True(Cmp.Compare(null, "a") < 0);
        Assert.True(Cmp.Compare("a", null) > 0);
        Assert.True(Cmp.Compare("", "a") < 0);
        Assert.True(Cmp.Compare("a", "") > 0);
        Assert.Equal(0, Cmp.Compare("", ""));
    }

    [Fact]
    public void 数字段与非数字段相遇_按首字符序数比较()
    {
        // '1'(49) < 'a'(97)：数字开头排在字母开头之前。
        Assert.True(Cmp.Compare("1", "a") < 0);
        Assert.True(Cmp.Compare("a", "1") > 0);
        Assert.True(Cmp.Compare("1a", "a1") < 0);
    }

    [Fact]
    public void 前缀相同时短串在前()
    {
        Assert.True(Cmp.Compare("file", "file2") < 0);
        Assert.True(Cmp.Compare("file2", "file") > 0);
        Assert.True(Cmp.Compare("file2", "file2a") < 0);
    }

    [Fact]
    public void 数字值相等时继续比较后续段()
    {
        // 2 与 02 数值相等，不因前导零提前分胜负；后续 'b' < 'c' 决定次序。
        Assert.True(Cmp.Compare("a2b", "a02c") < 0);
        Assert.True(Cmp.Compare("a02c", "a2b") > 0);
    }

    [Fact]
    public void Instance是单例且可直接用于排序()
    {
        Assert.NotNull(NaturalStringComparer.Instance);
        Assert.Same(NaturalStringComparer.Instance, NaturalStringComparer.Instance);

        var names = new[] { "file10", "File3", "file1" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "file1", "File3", "file10" }, names);
    }
}
