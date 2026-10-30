using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beautiful.Core.Finder;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Point = System.Windows.Point;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 图标视图（Phase 6-b）。
/// <para>
/// 网格位置、命中、框选、方向键四件事全部走 <see cref="FinderIconGrid"/>，面板
/// <see cref="FinderIconPanel"/> 也用同一套度量排布子元素——因此框选矩形盖住的格子
/// 与实际被选中的条目严格一致，不会出现"框住了却没选中"的经典错位。
/// </para>
/// <para>
/// 框选只在空白处起手（点在条目上交给 ListBox 的原生选中/拖拽语义）；拖动过程实时按
/// <see cref="FinderIconGrid.ItemsInRect"/> 重算命中集合，按住 Ctrl/Shift 时与起始选中集合并。
/// 图标尺寸由滑杆或 ⇧⌘[ / ] 调整，吸附到 Core 的档位并记住到当前文件夹。
/// </para>
/// </summary>
public partial class FinderWindow
{
    private Point? _rubberStart;
    private List<FinderRow> _rubberBase = [];
    private bool _suppressIconSlider;

    /// <summary>把当前图标尺寸应用到网格面板与所有行（视图切换/尺寸变化时调用）。</summary>
    private void ApplyIconLayout()
    {
        if (IconList is null)
        {
            return;
        }

        var (cellWidth, cellHeight) = FinderIconGrid.CellFor(_iconSize);
        var panel = FindIconPanel();
        if (panel is not null)
        {
            panel.CellWidth = cellWidth;
            panel.CellHeight = cellHeight;
        }

        foreach (var row in _allRows)
        {
            row.SetIconBoxSize(_iconSize);
        }
    }

    /// <summary>当前网格度量（方向键/框选用）：优先取面板的实际度量，未布局时按可视宽度现算。</summary>
    private FinderGridMetrics CurrentIconMetrics()
    {
        var panel = FindIconPanel();
        if (panel is not null && panel.Metrics.Columns > 0)
        {
            return panel.Metrics;
        }

        var (cellWidth, cellHeight) = FinderIconGrid.CellFor(_iconSize);
        return FinderIconGrid.Measure(IconList.ActualWidth, IconList.Items.Count, cellWidth, cellHeight);
    }

    /// <summary>在视觉树里找网格面板（ItemsPanelTemplate 里的实例不是字段，只能向下找一次）。</summary>
    private FinderIconPanel? FindIconPanel()
    {
        if (IconList is null)
        {
            return null;
        }

        return FindDescendant<FinderIconPanel>(IconList);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------- 图标尺寸 ----------

    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressIconSlider || !IsInitialized || IconList is null)
        {
            return;
        }

        SetIconSize(e.NewValue);
    }

    /// <summary>
    /// 改图标尺寸：吸附档位 → 更新面板与行 → 记住到当前文件夹。
    /// 尺寸变大时若已有位图分辨率不够，重新提取一轮（Shell 最多给 256px，不会无限重试）。
    /// </summary>
    private void SetIconSize(double requested)
    {
        var size = FinderIconGrid.SnapIconSize(requested);
        if (Math.Abs(size - _iconSize) < 0.5)
        {
            return;
        }

        _iconSize = size;
        ApplyIconLayout();
        PersistViewOptions();

        _suppressIconSlider = true;
        try
        {
            IconSizeSlider.Value = size;
        }
        finally
        {
            _suppressIconSlider = false;
        }

        var pixels = IconPixelsFor(size);
        if (pixels > _iconPixelSize)
        {
            _iconPixelSize = pixels;
            StartIconLoad(_allRows, _loadGeneration);
        }
    }

    /// <summary>⇧⌘[ / ]：在 Core 的档位表里上下走一格（图标视图专用）。</summary>
    private void StepIconSize(int delta)
    {
        if (_viewMode != FinderViewMode.Icons)
        {
            ShowStatusHint("图标尺寸只在图标视图可调（Ctrl+3 切换）");
            return;
        }

        var steps = FinderIconGrid.IconSizeSteps;
        var current = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            if (Math.Abs(steps[i] - _iconSize) < 0.5)
            {
                current = i;
                break;
            }
        }

        var next = Math.Clamp(current + delta, 0, steps.Count - 1);
        if (Math.Abs(steps[next] - _iconSize) < 0.5)
        {
            return;
        }

        SetIconSize(steps[next]);
    }

    // ---------- 方向键 ----------

    /// <summary>
    /// 图标视图方向键：按网格移动（左右不换行、上下越界不动）。
    /// 返回 true 表示按键已被处理（含"到了边界什么都不做"），避免 ListBox 再按自己的索引逻辑跳。
    /// </summary>
    private bool HandleIconArrow(Key key)
    {
        var (deltaColumn, deltaRow) = key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0)
        };

        if (deltaColumn == 0 && deltaRow == 0)
        {
            return false;
        }

        // Shift/Ctrl 组合键保留给扩展选择（ListBox 原生行为）
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0)
        {
            return false;
        }

        var count = IconList.Items.Count;
        if (count == 0)
        {
            return false;
        }

        var next = FinderIconGrid.Move(IconList.SelectedIndex, deltaColumn, deltaRow, CurrentIconMetrics(), count);
        if (next < 0)
        {
            return true;   // 边界：吞掉按键，不换行也不清空选择
        }

        IconList.SelectedIndex = next;
        if (IconList.Items[next] is object item)
        {
            IconList.ScrollIntoView(item);
        }

        return true;
    }

    // ---------- 框选 ----------

    private void IconList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在条目上：交给 ListBox 原生选中/双击逻辑
        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is not null)
        {
            return;
        }

        _rubberStart = e.GetPosition(IconList);
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _rubberBase = additive
            ? IconList.SelectedItems.OfType<FinderRow>().ToList()
            : [];

        if (!additive)
        {
            IconList.SelectedItems.Clear();
        }

        Canvas.SetLeft(RubberBand, _rubberStart.Value.X);
        Canvas.SetTop(RubberBand, _rubberStart.Value.Y);
        RubberBand.Width = 0;
        RubberBand.Height = 0;
        RubberBand.Visibility = Visibility.Visible;

        IconList.CaptureMouse();
        e.Handled = true;
    }

    private void IconList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_rubberStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(IconList);
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(RubberBand, left);
        Canvas.SetTop(RubberBand, top);
        RubberBand.Width = width;
        RubberBand.Height = height;

        var panel = FindIconPanel();
        var metrics = CurrentIconMetrics();
        if (panel is null)
        {
            return;
        }

        // 把窗口坐标换算到面板坐标（面板随滚动偏移）
        var origin = panel.TranslatePoint(new Point(0, 0), IconList);
        var hits = FinderIconGrid.ItemsInRect(
            left - origin.X, top - origin.Y,
            left + width - origin.X, top + height - origin.Y,
            metrics, IconList.Items.Count);

        IconList.SelectedItems.Clear();
        foreach (var row in _rubberBase)
        {
            IconList.SelectedItems.Add(row);
        }

        foreach (var index in hits)
        {
            if (IconList.Items[index] is FinderRow row && !IconList.SelectedItems.Contains(row))
            {
                IconList.SelectedItems.Add(row);
            }
        }
    }

    private void IconList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rubberStart is null)
        {
            return;
        }

        _rubberStart = null;
        _rubberBase = [];
        RubberBand.Visibility = Visibility.Collapsed;
        if (IconList.IsMouseCaptured)
        {
            IconList.ReleaseMouseCapture();
        }
    }

    private void IconItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: FinderRow row })
        {
            e.Handled = true;
            OpenRow(row);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
