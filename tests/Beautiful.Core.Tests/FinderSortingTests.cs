using System;
using System.Linq;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 列表排序测试（Phase 6-a）。
/// <para>
/// macOS Finder 的列表视图恒定"文件夹在前、文件在后"，组内才按列排序；名称列使用自然排序，
/// 其余列并列时回落到名称自然序，完全并列则保持原顺序（稳定）。排序函数不得修改调用方列表，
/// 因为 App 侧会把同一个集合同时用于显示与选中状态。
/// </para>
/// </summary>
public class FinderSortingTests
{
    private static readonly DateTime T1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FinderEntry File(
        string name, long size = 0, DateTime? modified = null, string kind = "Document")
        => new(name, IsDirectory: false, size, modified ?? T1, kind);

    private static FinderEntry Dir(
        string name, long size = 0, DateTime? modified = null, string kind = "Folder")
        => new(name, IsDirectory: true, size, modified ?? T1, kind);

    private static string[] Names(System.Collections.Generic.IReadOnlyList<FinderEntry> entries)
        => entries.Select(e => e.Name).ToArray();

    [Fact]
    public void 默认按名称排序_目录恒在文件之前且组内自然序()
    {
        var entries = new[]
        {
            File("file10"),
            Dir("beta"),
            File("file2"),
            Dir("Alpha"),
            File("file1"),
        };

        var sorted = FinderSorting.Sort(entries);

        Assert.Equal(new[] { "Alpha", "beta", "file1", "file2", "file10" }, Names(sorted));
        Assert.All(sorted.Take(2), e => Assert.True(e.IsDirectory));
        Assert.All(sorted.Skip(2), e => Assert.False(e.IsDirectory));
    }

    [Fact]
    public void 名称列大小写不敏感_数字按数值排序()
    {
        var entries = new[] { File("FILENAME10"), File("filename2"), File("FileName1") };

        var sorted = FinderSorting.Sort(entries, FinderSortKey.Name);

        Assert.Equal(new[] { "FileName1", "filename2", "FILENAME10" }, Names(sorted));
    }

    [Fact]
    public void 大小列升序_降序只翻转组内且目录仍在最前()
    {
        var entries = new[]
        {
            Dir("z-dir", size: 999),
            File("big", size: 300),
            File("small", size: 100),
            Dir("a-dir", size: 1),
        };

        var asc = FinderSorting.Sort(entries, FinderSortKey.Size);
        Assert.Equal(new[] { "a-dir", "z-dir", "small", "big" }, Names(asc));

        var desc = FinderSorting.Sort(entries, FinderSortKey.Size, descending: true);
        Assert.Equal(new[] { "z-dir", "a-dir", "big", "small" }, Names(desc));
    }

    [Fact]
    public void 修改时间列_默认升序_降序反转()
    {
        var entries = new[] { File("middle", modified: T2), File("old", modified: T1), File("new", modified: T3) };

        Assert.Equal(new[] { "old", "middle", "new" },
            Names(FinderSorting.Sort(entries, FinderSortKey.Modified)));
        Assert.Equal(new[] { "new", "middle", "old" },
            Names(FinderSorting.Sort(entries, FinderSortKey.Modified, descending: true)));
    }

    [Fact]
    public void 类型列忽略大小写_并列按名称自然序()
    {
        var entries = new[]
        {
            File("b", kind: "Folder"),
            File("A", kind: "folder"),
            File("C", kind: "Image"),
        };

        var sorted = FinderSorting.Sort(entries, FinderSortKey.Kind);

        Assert.Equal(new[] { "A", "b", "C" }, Names(sorted));
    }

    [Fact]
    public void 排序键并列_按名称自然序决胜()
    {
        var entries = new[] { File("file10", size: 10), File("file2", size: 10), File("file1", size: 10) };

        var sorted = FinderSorting.Sort(entries, FinderSortKey.Size);

        Assert.Equal(new[] { "file1", "file2", "file10" }, Names(sorted));
    }

    [Fact]
    public void 全部键并列_保持原始顺序_排序稳定()
    {
        var first = new FinderEntry("same", false, 10, T1, "Z");
        var second = new FinderEntry("same", false, 10, T2, "A");

        var sorted = FinderSorting.Sort(new[] { first, second }, FinderSortKey.Size);

        Assert.Equal(new[] { T1, T2 }, sorted.Select(e => e.ModifiedUtc).ToArray());
    }

    [Fact]
    public void 名称降序_目录仍在文件之前()
    {
        var entries = new[] { Dir("Alpha"), File("file1"), Dir("beta"), File("file2") };

        var sorted = FinderSorting.Sort(entries, FinderSortKey.Name, descending: true);

        Assert.Equal(new[] { "beta", "Alpha", "file2", "file1" }, Names(sorted));
        Assert.True(sorted[0].IsDirectory);
        Assert.True(sorted[1].IsDirectory);
        Assert.False(sorted[2].IsDirectory);
        Assert.False(sorted[3].IsDirectory);
    }

    [Fact]
    public void 排序不修改输入列表()
    {
        var entries = new[] { File("b", size: 2), File("a", size: 1) };
        var snapshot = entries.ToArray();

        var sorted = FinderSorting.Sort(entries, FinderSortKey.Size);

        Assert.Equal(snapshot, entries);
        Assert.NotSame(entries, sorted);
    }

    [Fact]
    public void 空输入或null_返回空列表且不抛异常()
    {
        Assert.Empty(FinderSorting.Sort(Array.Empty<FinderEntry>()));
        Assert.Empty(FinderSorting.Sort(null!));
    }
}
