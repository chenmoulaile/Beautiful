using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// 分栏视图（Miller columns）的列栈纯状态机（Phase 6-b）。
/// <para>
/// macOS 访达的列视图本质是一串"目录列"：最左是祖先、最右是当前目录，选中文件夹会在右侧开新列，
/// 选中文件只在最右追加一列预览。本类型只维护**路径栈 + 每列选中名 + 键盘焦点列**，
/// 不读盘、不碰 UI：目录内容由 App 侧按 <see cref="Columns"/> 异步填充。
/// </para>
/// <para>
/// 三条不变量（全部由单测钉死）：
/// 1. <see cref="Columns"/> 首项恒为链的起点（盘符根/UNC 根或用户指定的目录），末项恒为当前目录；
/// 2. 任意时刻 <c>Selections.Count == Columns.Count</c>，越界读取返回 <c>null</c> 而不是抛异常；
/// 3. 在列 <c>i</c> 上做选择会**截断** <c>i</c> 之后的所有列（macOS 语义：换选即丢弃右侧历史），
///    选中文件夹时再追加一列子目录。截断是唯一会缩短栈的操作，<see cref="Back"/> 是唯一会缩短栈的导航操作。
/// </para>
/// <para>
/// 列数上限 <see cref="MaxColumns"/>（默认 8）：超出时丢弃最左列并整体左移，保证内存与渲染量有界。
/// 左移后所有列索引减一，因此 App 侧每次状态变化都按 <see cref="Columns"/> 整体重建视觉列，
/// 不做增量更新——重建 8 列的成本远低于维护索引映射的复杂度。
/// </para>
/// </summary>
public sealed class FinderColumnState
{
    private const int DefaultMaxColumns = 8;

    private readonly List<string> _columns = [];
    private readonly List<string?> _selections = [];
    private readonly int _maxColumns;

    /// <param name="rootPath">链的起点目录（通常是被导航到的目录本身），不能为 null/空白。</param>
    /// <param name="maxColumns">列数上限，小于 1 时按 1 处理。</param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> 为 null、空串或纯空白。</exception>
    public FinderColumnState(string rootPath, int maxColumns = DefaultMaxColumns)
    {
        _maxColumns = Math.Max(1, maxColumns);
        Reset(rootPath);
    }

    /// <summary>列栈（只读视图）：首项是链起点，末项是当前目录。</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>列数（恒 ≥ 1）。</summary>
    public int Count => _columns.Count;

    /// <summary>当前目录（列栈末项）。</summary>
    public string Current => _columns[^1];

    /// <summary>键盘焦点所在的列索引（恒在 <c>[0, Count-1]</c>）。</summary>
    public int FocusIndex { get; private set; }

    /// <summary>取某列的选中项名；越界返回 <c>null</c>。</summary>
    public string? SelectionAt(int index)
        => index >= 0 && index < _selections.Count ? _selections[index] : null;

    /// <summary>把状态重置为单列链（导航到新位置时调用）。</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为 null、空串或纯空白。</exception>
    public void Reset(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("分栏视图的根目录不能为空。", nameof(path));
        }

        _columns.Clear();
        _selections.Clear();
        _columns.Add(path);
        _selections.Add(null);
        FocusIndex = 0;
    }

    /// <summary>
    /// 用一条祖先链替换当前列栈（例如"前往文件夹…"直接跳到深层路径时，
    /// 由 <see cref="FinderColumnChain.Build"/> 生成 <c>[C:\, C:\Users, C:\Users\me, ...]</c>）。
    /// </summary>
    /// <param name="chain">至少含一项的路径链；空集合视为非法调用，返回 false 且不改状态。</param>
    /// <returns>是否替换成功。</returns>
    public bool SetChain(IReadOnlyList<string>? chain)
    {
        if (chain is null || chain.Count == 0)
        {
            return false;
        }

        var start = Math.Max(0, chain.Count - _maxColumns);
        var kept = new List<string>(chain.Count - start);
        for (var i = start; i < chain.Count; i++)
        {
            var item = chain[i];
            if (string.IsNullOrWhiteSpace(item))
            {
                return false;   // 链中有空路径：整条拒绝，避免半截状态
            }

            kept.Add(item);
        }

        _columns.Clear();
        _columns.AddRange(kept);
        _selections.Clear();
        for (var i = 0; i < _columns.Count; i++)
        {
            _selections.Add(null);
        }

        FocusIndex = _columns.Count - 1;
        return true;
    }

    /// <summary>
    /// 用祖先链替换列栈，并按链推出每列选中项：列 <c>i</c> 的选中项 = 链中 <c>i+1</c> 的显示名。
    /// <para>
    /// 这是列视图导航后的视觉恢复逻辑：从 <c>C:\Users\me\Docs</c> 重建时，三列分别是
    /// <c>C:\</c>（选中 Users）、<c>C:\Users</c>（选中 me）、<c>C:\Users\me</c>（选中 Docs），
    /// 于是"当前目录"在父列里也是高亮的——正是 macOS 列视图的样子。
    /// </para>
    /// </summary>
    /// <param name="chain">至少含一项的路径链；空集合或含空路径返回 false 且不改状态。</param>
    public bool SetChainWithSelections(IReadOnlyList<string>? chain)
    {
        if (!SetChain(chain))
        {
            return false;
        }

        for (var i = 0; i + 1 < _columns.Count; i++)
        {
            _selections[i] = FinderPaths.DisplayName(_columns[i + 1]);
        }

        return true;
    }

    /// <summary>
    /// 在某列上选中一项（点击/键盘选择都走这里）。
    /// </summary>
    /// <param name="index">列索引，越界返回 false。</param>
    /// <param name="name">选中项名；<c>null</c> 表示清空该列选择（右侧列一并截断）。</param>
    /// <param name="isDirectory">选中项是否为文件夹：是则追加子目录列。</param>
    /// <returns>状态是否真的发生变化（用于避免无谓重建）。</returns>
    public bool Select(int index, string? name, bool isDirectory)
    {
        if (index < 0 || index >= _columns.Count)
        {
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            return ClearFrom(index);
        }

        if (isDirectory)
        {
            var childPath = FinderPaths.Combine(_columns[index], name);

            // 重复选中同一文件夹（点击已展开项）：完全 no-op——右侧列保留，焦点也不动，
            // 否则用户点一下已展开的父级就会把键盘焦点抢回中间列。
            if (index + 1 < _columns.Count
                && string.Equals(_selections[index], name, StringComparison.Ordinal)
                && string.Equals(_columns[index + 1], childPath, StringComparison.Ordinal))
            {
                return false;
            }

            TruncateAfter(index);
            _selections[index] = name;
            _columns.Add(childPath);
            _selections.Add(null);
            TrimFromLeft();
            FocusIndex = _columns.Count - 1;
            return true;
        }

        // 选中文件：右侧只保留预览（不进栈），焦点留在本列。
        TruncateAfter(index);
        if (string.Equals(_selections[index], name, StringComparison.Ordinal))
        {
            FocusIndex = index;
            return false;
        }

        _selections[index] = name;
        FocusIndex = index;
        return true;
    }

    /// <summary>
    /// 从某列起清空选择并丢弃右侧列（点空白处/按 Esc 的语义）。
    /// </summary>
    /// <returns>状态是否真的发生变化。</returns>
    public bool ClearFrom(int index)
    {
        if (index < 0 || index >= _columns.Count)
        {
            return false;
        }

        var hadSelection = _selections[index] is not null;
        var hadExtra = index + 1 < _columns.Count;
        if (!hadSelection && !hadExtra)
        {
            FocusIndex = index;
            return false;
        }

        TruncateAfter(index);
        _selections[index] = null;
        FocusIndex = index;
        return true;
    }

    /// <summary>后退一列（macOS 列视图的 ←：丢掉最右列，焦点落到新的最右列）。</summary>
    /// <returns>是否真的后退了一步（只剩一列时返回 false）。</returns>
    public bool Back()
    {
        if (_columns.Count <= 1)
        {
            return false;
        }

        _columns.RemoveAt(_columns.Count - 1);
        _selections.RemoveAt(_selections.Count - 1);
        FocusIndex = _columns.Count - 1;
        return true;
    }

    /// <summary>键盘焦点左移一列；已在最左列返回 false。</summary>
    public bool FocusLeft()
    {
        if (FocusIndex <= 0)
        {
            return false;
        }

        FocusIndex--;
        return true;
    }

    /// <summary>键盘焦点右移一列；已在最右列返回 false。</summary>
    public bool FocusRight()
    {
        if (FocusIndex >= _columns.Count - 1)
        {
            return false;
        }

        FocusIndex++;
        return true;
    }

    private void TruncateAfter(int index)
    {
        var removeFrom = index + 1;
        if (removeFrom >= _columns.Count)
        {
            return;
        }

        _columns.RemoveRange(removeFrom, _columns.Count - removeFrom);
        _selections.RemoveRange(removeFrom, _selections.Count - removeFrom);
    }

    private void TrimFromLeft()
    {
        while (_columns.Count > _maxColumns)
        {
            _columns.RemoveAt(0);
            _selections.RemoveAt(0);
        }
    }
}

/// <summary>
/// 把任意目录展开成"从祖先到自身"的路径链（Phase 6-b 分栏视图入口）。
/// <para>
/// 分栏视图需要一串祖先列，而导航 API 只给目标目录。本函数用 <see cref="FinderPaths.Parent"/>
/// 逐级上溯到根（盘符根/UNC 共享根），再反转为 <c>[根, ..., 目标]</c>，并按
/// <paramref name="maxColumns"/> 截掉最左的祖先（保留靠近目标的那几列）。
/// </para>
/// <para>
/// 相对路径/空路径/非法串一律返回只含原串的单元素链（不去猜它相对谁）——UI 侧照常显示，
/// 由目录读取失败给出真实原因，绝不在这里伪造绝对路径。
/// </para>
/// </summary>
public static class FinderColumnChain
{
    /// <summary>生成祖先链。</summary>
    /// <param name="target">目标目录；null/空白返回空数组。</param>
    /// <param name="maxColumns">最多保留的列数（默认 6），小于 1 时按 1 处理。</param>
    public static IReadOnlyList<string> Build(string? target, int maxColumns = 6)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Array.Empty<string>();
        }

        var limit = Math.Max(1, maxColumns);
        if (!IsRooted(target))
        {
            // 相对路径不猜它相对谁：单元素链，由目录读取给出真实失败原因。
            return new[] { target };
        }

        var chain = new List<string> { target };
        var cursor = target;
        while (chain.Count < limit)
        {
            var parent = FinderPaths.Parent(cursor);
            if (parent is null || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            chain.Add(parent);
            cursor = parent;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>是否绝对路径：盘符前缀（<c>C:</c>）、UNC（<c>\\server</c>）或以分隔符开头。</summary>
    private static bool IsRooted(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/'))
        {
            return true;
        }

        return path.Length >= 1 && (path[0] == '\\' || path[0] == '/');
    }
}

/// <summary>
/// 分栏视图的列宽算术（Phase 6-b）。
/// <para>
/// macOS 列视图：中间列固定宽度（用户可拖，本版固定），**最右列吃掉剩余宽度**。
/// 容器比"列数 × 固定宽"还窄时不做压缩（改为水平滚动），因此这里只负责给出
/// 每列应设的宽度，压缩/滚动策略由 UI 的 ScrollViewer 决定。
/// </para>
/// </summary>
public static class FinderColumnMetrics
{
    /// <summary>中间列默认宽度（Big Sur 列视图约 260 DIP）。</summary>
    public const double ColumnWidth = 260;

    /// <summary>最右列的最小宽度：再窄就无法显示"名称 + 图标"一行。</summary>
    public const double MinLastColumnWidth = 200;

    /// <summary>列间 1px 分隔线宽度。</summary>
    public const double SeparatorWidth = 1;

    /// <summary>
    /// 第 <paramref name="index"/> 列的宽度。
    /// </summary>
    /// <param name="index">列索引。</param>
    /// <param name="count">总列数（含预览列）。</param>
    /// <param name="viewportWidth">可视宽度。</param>
    /// <returns>最右列 = 剩余宽度（不小于 <see cref="MinLastColumnWidth"/>），其余列 = <see cref="ColumnWidth"/>。</returns>
    public static double WidthAt(int index, int count, double viewportWidth)
    {
        if (count <= 0 || index < count - 1)
        {
            return ColumnWidth;
        }

        var used = Math.Max(0, count - 1) * (ColumnWidth + SeparatorWidth);
        var remaining = viewportWidth - used;
        return Math.Max(MinLastColumnWidth, remaining);
    }
}
