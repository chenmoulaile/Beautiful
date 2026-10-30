using System;
using System.Linq;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 分栏视图列栈测试（Phase 6-b）。
/// <para>
/// 这里钉死的是 macOS 列视图的三条不变量：列栈末项恒为当前目录、选择即截断右侧、
/// 选中文件夹才开新列（选中文件只留预览）。列数上限触发左移后，焦点必须落在新的最右列，
/// 否则键盘 → 会打到一个已经不存在的列上。
/// </para>
/// </summary>
public class FinderColumnViewTests
{
    private const string Root = @"C:\Users\me";

    [Fact]
    public void 初始状态是单列且无选中()
    {
        var state = new FinderColumnState(Root);

        Assert.Equal(new[] { Root }, state.Columns);
        Assert.Equal(1, state.Count);
        Assert.Equal(Root, state.Current);
        Assert.Equal(0, state.FocusIndex);
        Assert.Null(state.SelectionAt(0));
    }

    [Fact]
    public void 空路径构造抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FinderColumnState(" "));
    }

    [Fact]
    public void 选中文件夹追加子目录列并把焦点移到新列()
    {
        var state = new FinderColumnState(Root);

        var changed = state.Select(0, "Docs", isDirectory: true);

        Assert.True(changed);
        Assert.Equal(new[] { Root, @"C:\Users\me\Docs" }, state.Columns);
        Assert.Equal("Docs", state.SelectionAt(0));
        Assert.Equal(1, state.FocusIndex);
        Assert.Null(state.SelectionAt(1));
    }

    [Fact]
    public void 选中文件不追加列且焦点留在本列()
    {
        var state = new FinderColumnState(Root);

        var changed = state.Select(0, "a.txt", isDirectory: false);

        Assert.True(changed);
        Assert.Single(state.Columns);
        Assert.Equal("a.txt", state.SelectionAt(0));
        Assert.Equal(0, state.FocusIndex);
    }

    [Fact]
    public void 在同一列换选会截断右侧所有列()
    {
        var state = new FinderColumnState(Root);
        state.Select(0, "Docs", isDirectory: true);
        state.Select(1, "Work", isDirectory: true);
        Assert.Equal(3, state.Count);

        state.Select(0, "Pictures", isDirectory: true);

        Assert.Equal(new[] { Root, @"C:\Users\me\Pictures" }, state.Columns);
        Assert.Equal("Pictures", state.SelectionAt(0));
        Assert.Null(state.SelectionAt(1));
    }

    [Fact]
    public void 重复选中已展开的同一文件夹不会重建右侧列()
    {
        var state = new FinderColumnState(Root);
        state.Select(0, "Docs", isDirectory: true);
        state.Select(1, "Work", isDirectory: true);
        var before = state.Columns.ToArray();

        var changed = state.Select(0, "Docs", isDirectory: true);

        Assert.False(changed);
        Assert.Equal(before, state.Columns);
        Assert.Equal(2, state.FocusIndex);
    }

    [Fact]
    public void 清空某列选择会截断右侧并返回是否有变化()
    {
        var state = new FinderColumnState(Root);
        state.Select(0, "Docs", isDirectory: true);
        state.Select(1, "a.txt", isDirectory: false);

        Assert.True(state.ClearFrom(0));
        Assert.Single(state.Columns);
        Assert.Null(state.SelectionAt(0));

        // 已经空且没有右侧列：第二次清空是 no-op
        Assert.False(state.ClearFrom(0));
    }

    [Fact]
    public void 越界索引的选择与清空都返回false且不改状态()
    {
        var state = new FinderColumnState(Root);

        Assert.False(state.Select(-1, "x", isDirectory: true));
        Assert.False(state.Select(1, "x", isDirectory: true));
        Assert.False(state.ClearFrom(5));
        Assert.Null(state.SelectionAt(3));
        Assert.Single(state.Columns);
    }

    [Fact]
    public void 后退逐级丢掉最右列_只剩一列时返回false()
    {
        var state = new FinderColumnState(Root);
        state.Select(0, "Docs", isDirectory: true);
        state.Select(1, "Work", isDirectory: true);

        Assert.True(state.Back());
        Assert.Equal(2, state.Count);
        Assert.Equal(1, state.FocusIndex);

        Assert.True(state.Back());
        Assert.Single(state.Columns);

        Assert.False(state.Back());
        Assert.Single(state.Columns);
    }

    [Fact]
    public void 焦点左右移动在两端返回false()
    {
        var state = new FinderColumnState(Root);
        state.Select(0, "Docs", isDirectory: true);

        Assert.True(state.FocusLeft());
        Assert.Equal(0, state.FocusIndex);
        Assert.False(state.FocusLeft());

        Assert.True(state.FocusRight());
        Assert.Equal(1, state.FocusIndex);
        Assert.False(state.FocusRight());
    }

    [Fact]
    public void 列数超过上限时丢弃最左列且焦点落在新的最右列()
    {
        var state = new FinderColumnState(@"C:\", maxColumns: 3);
        state.Select(0, "a", isDirectory: true);
        state.Select(1, "b", isDirectory: true);
        state.Select(2, "c", isDirectory: true);

        Assert.Equal(3, state.Count);
        Assert.Equal(@"C:\a\b\c", state.Current);
        Assert.Equal(@"C:\a", state.Columns[0]);
        Assert.Equal(2, state.FocusIndex);
    }

    [Fact]
    public void 设置祖先链会整体替换列栈并把焦点放在末列()
    {
        var state = new FinderColumnState(@"C:\");
        var chain = new[] { @"C:\", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Docs" };

        Assert.True(state.SetChain(chain));
        Assert.Equal(chain, state.Columns);
        Assert.Equal(3, state.FocusIndex);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Null(state.SelectionAt(i)));
    }

    [Fact]
    public void 按祖先链重建时父列高亮展开的子目录()
    {
        var state = new FinderColumnState(@"C:\");

        Assert.True(state.SetChainWithSelections(
            new[] { @"C:\", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Docs" }));

        Assert.Equal("Users", state.SelectionAt(0));
        Assert.Equal("me", state.SelectionAt(1));
        Assert.Equal("Docs", state.SelectionAt(2));
        Assert.Null(state.SelectionAt(3));       // 末列（当前目录）自身没有选中项
        Assert.Equal(3, state.FocusIndex);
    }

    [Fact]
    public void 空链或含空路径的链被整条拒绝()
    {
        var state = new FinderColumnState(Root);

        Assert.False(state.SetChain(Array.Empty<string>()));
        Assert.False(state.SetChain([@"C:\", "  "]));
        Assert.Single(state.Columns);
        Assert.Equal(Root, state.Current);
    }

    [Fact]
    public void 祖先链从盘符根开始且末项是目标目录()
    {
        var chain = FinderColumnChain.Build(@"C:\Users\me\Docs", maxColumns: 6);

        Assert.Equal(new[] { @"C:\", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Docs" }, chain);
    }

    [Fact]
    public void 祖先链受列数上限约束时保留靠近目标的列()
    {
        var chain = FinderColumnChain.Build(@"C:\Users\me\Docs\Work\2026", maxColumns: 3);

        Assert.Equal(new[] { @"C:\Users\me\Docs\Work\2026", @"C:\Users\me\Docs\Work", @"C:\Users\me\Docs" }
            .Reverse().ToArray(), chain);
    }

    [Fact]
    public void 相对路径与空路径的链不伪造绝对路径()
    {
        Assert.Empty(FinderColumnChain.Build(null));
        Assert.Empty(FinderColumnChain.Build("   "));
        Assert.Equal(new[] { "relative\\dir" }, FinderColumnChain.Build("relative\\dir"));
    }

    [Fact]
    public void UNC共享根的上溯在共享根停止()
    {
        var chain = FinderColumnChain.Build(@"\\server\share\docs", maxColumns: 8);

        Assert.Equal(new[] { @"\\server\share", @"\\server\share\docs" }, chain);
    }

    [Fact]
    public void 中间列固定宽而最右列吃掉剩余宽度()
    {
        Assert.Equal(FinderColumnMetrics.ColumnWidth, FinderColumnMetrics.WidthAt(0, 3, 1200));
        Assert.Equal(1200 - (2 * (FinderColumnMetrics.ColumnWidth + FinderColumnMetrics.SeparatorWidth)),
            FinderColumnMetrics.WidthAt(2, 3, 1200), precision: 3);
    }

    [Fact]
    public void 视口过窄时最右列不低于最小宽度()
    {
        Assert.Equal(FinderColumnMetrics.MinLastColumnWidth, FinderColumnMetrics.WidthAt(1, 2, 300));
        Assert.Equal(FinderColumnMetrics.MinLastColumnWidth, FinderColumnMetrics.WidthAt(0, 1, 0));
    }
}
