using System.Windows;
using System.Windows.Controls;
using Beautiful.Core.Finder;
using ListBox = System.Windows.Controls.ListBox;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 排序与快速查看入口（Phase 6-b）。
/// <para>
/// 排序：点列表头切换排序列，重复点同一列翻转方向；箭头显示在表头文字后（▲升 ▼降）。
/// 排序口径完全由 <see cref="FinderSorting"/> 决定（文件夹恒在前），UI 不另写比较器；
/// 换序后重新枚举一次当前目录（OS 目录缓存命中，代价是几十毫秒），避免在行对象上维护第二份排序状态。
/// </para>
/// <para>
/// 快速查看：空格键把"当前视图的多选集合 + 起点索引"交给
/// <see cref="QuickLookWindow.ShowFor(IReadOnlyList{string}, int)"/>，预览窗内可用 ←/→ 翻页；
/// 再按空格或 Esc 关闭。
/// </para>
/// </summary>
public partial class FinderWindow
{
    // ---------- 排序 ----------

    /// <summary>表头点击：同列翻转方向，换列则按该列升序重排。</summary>
    private void Header_Click(object sender, RoutedEventArgs e)
    {
        // EventSetter 的 sender 是表头本身；OriginalSource 可能是表头里的 ContentPresenter，
        // 两者都试一次，避免"点了没反应"这种最难查的空操作。
        var header = sender as GridViewColumnHeader
                     ?? FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column?.Header is not string text)
        {
            return;
        }

        var key = SortKeyOfHeader(text);
        if (key is null)
        {
            return;
        }

        if (key == _sortKey)
        {
            _descending = !_descending;
        }
        else
        {
            _sortKey = key.Value;
            _descending = false;
        }

        UpdateSortHeaders();
        PersistViewOptions();
        RefreshCurrentDirectory();
    }

    /// <summary>表头文字 → 排序列（文字可能带 ▲/▼ 后缀，用前缀匹配）。</summary>
    private static FinderSortKey? SortKeyOfHeader(string text)
    {
        if (text.StartsWith("名称", StringComparison.Ordinal))
        {
            return FinderSortKey.Name;
        }

        if (text.StartsWith("修改日期", StringComparison.Ordinal))
        {
            return FinderSortKey.Modified;
        }

        if (text.StartsWith("大小", StringComparison.Ordinal))
        {
            return FinderSortKey.Size;
        }

        if (text.StartsWith("种类", StringComparison.Ordinal))
        {
            return FinderSortKey.Kind;
        }

        return null;
    }

    /// <summary>把当前排序列/方向写到四个表头上（只有当前列带箭头）。</summary>
    private void UpdateSortHeaders()
    {
        if (NameColumn is null || ModifiedColumn is null || SizeColumn is null || KindColumn is null)
        {
            return;
        }

        var arrow = _descending ? " ▼" : " ▲";
        NameColumn.Header = "名称" + (_sortKey == FinderSortKey.Name ? arrow : string.Empty);
        ModifiedColumn.Header = "修改日期" + (_sortKey == FinderSortKey.Modified ? arrow : string.Empty);
        SizeColumn.Header = "大小" + (_sortKey == FinderSortKey.Size ? arrow : string.Empty);
        KindColumn.Header = "种类" + (_sortKey == FinderSortKey.Kind ? arrow : string.Empty);
    }

    // ---------- 快速查看 ----------

    /// <summary>空格键入口：按当前视图取多选集合；没有选中项返回 false（让空格保持原生语义）。</summary>
    private bool QuickLookFromSelection()
    {
        var (paths, index) = CollectSelection();
        if (paths.Count == 0)
        {
            return false;
        }

        QuickLookWindow.ShowFor(paths, index);
        return true;
    }

    /// <summary>
    /// 按名称选中一个条目（<c>--select</c> 诊断入口 / 将来的"定位到文件"复用）。
    /// 分栏视图里选中文件夹会照常开新列，选中文件则显示预览列。
    /// </summary>
    private void SelectByName(string name)
    {
        var row = _visibleRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? _allRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            ShowStatusHint($"未找到：{name}");
            return;
        }

        if (_viewMode == FinderViewMode.Columns && _columnState is not null)
        {
            // 与鼠标点击同一语义：选中文件夹 = 导航（列栈由祖先链重建，新目录成为最右列）；
            // 选中文件 = 只开预览列，不动当前目录。
            if (row.IsDirectory)
            {
                NavigateTo(row.FullPath);
                return;
            }

            _columnState.Select(_columnState.Count - 1, row.Name, isDirectory: false);
            RefreshColumns();
            return;
        }

        if (_viewMode == FinderViewMode.Gallery)
        {
            SelectGalleryRow(row);
            return;
        }

        ListBox list = _viewMode == FinderViewMode.Icons ? IconList : FileList;
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        list.Focus();
    }

    /// <summary>
    /// 当前视图的选中集合（顺序即控件里的显示顺序）+ 预览起点索引。
    /// 起点取"控件当前项"在集合里的位置；不在集合里（例如多选后焦点漂移）则从第一项开始。
    /// </summary>
    private (IReadOnlyList<string> Paths, int Index) CollectSelection()
    {
        List<FinderRow> rows;
        FinderRow? current;
        switch (_viewMode)
        {
            case FinderViewMode.Icons:
                rows = IconList.SelectedItems.OfType<FinderRow>().ToList();
                current = IconList.SelectedItem as FinderRow;
                break;
            case FinderViewMode.Columns:
                current = ColumnSelectedRow();
                rows = current is null ? [] : [current];
                break;
            case FinderViewMode.Gallery:
                current = _gallerySelection;
                rows = current is null ? [] : [current];
                break;
            default:
                rows = FileList.SelectedItems.OfType<FinderRow>().ToList();
                current = FileList.SelectedItem as FinderRow;
                break;
        }

        var index = current is null ? 0 : Math.Max(0, rows.IndexOf(current));
        return (rows.Select(row => row.FullPath).ToList(), index);
    }
}
