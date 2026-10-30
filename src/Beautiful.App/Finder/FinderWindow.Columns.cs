using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using ListBox = System.Windows.Controls.ListBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 分栏视图（Phase 6-b）：Miller columns。
/// <para>
/// 视觉结构是"一串等宽列 + 最右预览列"，横向滚动；列栈/选中/焦点全部由 Core 的
/// <see cref="FinderColumnState"/> 决定，本文件只做三件事：把状态渲染成列、把点击翻译成状态变更、
/// 把异步载入的目录内容填进对应列。**每次状态变更都整列重建**（最多 8 列），
/// 换来的是不需要维护"视觉列索引 ↔ 状态列索引"的映射——那正是列视图最容易错位的地方。
/// </para>
/// <para>
/// 目录内容走 <see cref="_dirCache"/>：主视图已载入过的目录直接复用行对象（含已提取的图标），
/// 没载入过的在后台枚举并按代次号回填。缓存上限 <see cref="MaxCachedDirectories"/> 个目录。
/// </para>
/// </summary>
public partial class FinderWindow
{
    /// <summary>列视图缓存上限（目录数）：够覆盖一条深层路径的浏览历史，又不会长期占内存。</summary>
    private const int MaxCachedDirectories = 32;

    /// <summary>预览列宽度（macOS 列视图最右预览约 240 DIP）。</summary>
    private const double PreviewColumnWidth = 240;

    /// <summary>文本预览列读的行数上限。</summary>
    private const int PreviewTextLines = 12;

    private readonly Dictionary<string, IReadOnlyList<FinderRow>> _dirCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _columnLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ColumnVisual> _columnVisuals = [];

    private FinderColumnState? _columnState;
    private bool _suppressColumnSelection;

    /// <summary>列视图里的一列：路径 + 列表控件 + 当前行集合。</summary>
    private sealed record ColumnVisual(int Index, string Path, ListBox List, IReadOnlyList<FinderRow> Rows);

    // ---------- 目录缓存 ----------

    /// <summary>记住一个已载入目录的行集合（主视图载入成功后调用，列视图据此免二次枚举）。</summary>
    private void RememberDirectory(string path, IReadOnlyList<FinderRow> rows)
    {
        var key = FinderViewOptionRules.KeyOf(path);
        if (key.Length == 0)
        {
            return;
        }

        _dirCache[key] = rows;
        while (_dirCache.Count > MaxCachedDirectories)
        {
            var oldest = _dirCache.Keys.FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            _dirCache.Remove(oldest);
        }
    }

    /// <summary>丢掉某目录的缓存（F5 刷新用：下次读取拿到真实的最新内容）。</summary>
    private void ForgetDirectory(string path)
    {
        var key = FinderViewOptionRules.KeyOf(path);
        if (key.Length > 0)
        {
            _dirCache.Remove(key);
        }
    }

    // ---------- 视图切换 ----------

    /// <summary>
    /// 切换视图模式：只改可见性与各视图的数据源，不重新读盘（数据在 <see cref="_allRows"/> 与
    /// <see cref="_dirCache"/> 里）。<paramref name="persist"/> 为 false 用于构造期/导航后应用已存选项，
    /// 避免"打开窗口就把默认值写回磁盘"。
    /// </summary>
    private void SetViewMode(FinderViewMode mode, bool persist = true)
    {
        _viewMode = mode;
        SyncViewChrome();

        if (mode == FinderViewMode.Columns)
        {
            EnsureColumnState(deriveSelections: true);
            RefreshColumns();
        }
        else if (mode == FinderViewMode.Icons)
        {
            ApplyIconLayout();
        }

        if (persist)
        {
            PersistViewOptions();
        }

        UpdateStatus();
    }

    /// <summary>把"当前视图模式/图标尺寸"同步到三个内容宿主、工具栏按钮与滑杆（不读盘、不排序）。</summary>
    private void SyncViewChrome()
    {
        if (FileList is null || ColumnScroll is null || IconList is null)
        {
            return;
        }

        FileList.Visibility = _viewMode == FinderViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        ColumnScroll.Visibility = _viewMode == FinderViewMode.Columns ? Visibility.Visible : Visibility.Collapsed;
        IconList.Visibility = _viewMode == FinderViewMode.Icons ? Visibility.Visible : Visibility.Collapsed;
        RubberBandLayer.Visibility = _viewMode == FinderViewMode.Icons ? Visibility.Visible : Visibility.Collapsed;
        if (_viewMode != FinderViewMode.Icons)
        {
            RubberBand.Visibility = Visibility.Collapsed;
        }

        IconSizeSlider.Visibility = _viewMode == FinderViewMode.Icons ? Visibility.Visible : Visibility.Collapsed;
        _suppressIconSlider = true;
        try
        {
            IconSizeSlider.Value = _iconSize;
        }
        finally
        {
            _suppressIconSlider = false;
        }

        foreach (var row in _allRows)
        {
            row.SetIconBoxSize(_iconSize);
        }

        UpdateViewButtons();
    }

    /// <summary>三个视图按钮的"当前项"高亮（ToolButton 模板用 TemplateBinding Background）。</summary>
    private void UpdateViewButtons()
    {
        if (ListViewButton is null)
        {
            return;
        }

        var active = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x7A, 0xFF));
        active.Freeze();
        var normal = Brushes.Transparent;

        ListViewButton.Background = _viewMode == FinderViewMode.List ? active : normal;
        ColumnsButton.Background = _viewMode == FinderViewMode.Columns ? active : normal;
        GridButton.Background = _viewMode == FinderViewMode.Icons ? active : normal;
    }

    // ---------- 列栈 ----------

    /// <summary>
    /// 确保列栈与当前路径一致：不一致就用祖先链重建（<see cref="FinderColumnChain.Build"/>），
    /// 并按链推出每列选中项——父列高亮它展开的那个子目录，这正是 macOS 列视图的视觉语言。
    /// </summary>
    private void EnsureColumnState(bool deriveSelections)
    {
        if (_columnState is not null && SamePath(_columnState.Current, _currentPath))
        {
            return;
        }

        var chain = FinderColumnChain.Build(_currentPath, maxColumns: 4);
        if (chain.Count == 0)
        {
            _columnState = new FinderColumnState(_currentPath);
            return;
        }

        _columnState ??= new FinderColumnState(_currentPath);
        _columnState.SetChainWithSelections(chain);
        if (!deriveSelections)
        {
            return;
        }
    }

    /// <summary>把列栈状态渲染成视觉列（每次状态变更整列重建）。</summary>
    private void RefreshColumns()
    {
        if (ColumnStack is null)
        {
            return;
        }

        _columnVisuals.Clear();
        ColumnStack.Children.Clear();

        if (_viewMode != FinderViewMode.Columns)
        {
            return;
        }

        // 导航后列栈可能还停在旧路径：先对齐，再渲染。
        EnsureColumnState(deriveSelections: true);
        var state = _columnState;
        if (state is null)
        {
            return;
        }

        for (var i = 0; i < state.Count; i++)
        {
            var path = state.Columns[i];
            var rows = RowsForColumn(path);
            var list = CreateColumnList(rows, i, state.SelectionAt(i));
            var border = new Border
            {
                Width = FinderColumnMetrics.WidthAt(i, state.Count + 1, ColumnScroll.ActualWidth),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Background = Brushes.Transparent
            };
            border.Child = list;
            ColumnStack.Children.Add(border);
            _columnVisuals.Add(new ColumnVisual(i, path, list, rows));
        }

        var preview = BuildPreviewColumn();
        if (preview is not null)
        {
            ColumnStack.Children.Add(preview);
        }

        RefreshColumnWidths();
    }

    /// <summary>列宽：中间列固定、最右列吃剩余宽度（Core <see cref="FinderColumnMetrics"/>）。</summary>
    private void RefreshColumnWidths()
    {
        if (ColumnStack is null || _viewMode != FinderViewMode.Columns || ColumnStack.Children.Count == 0)
        {
            return;
        }

        var count = ColumnStack.Children.Count;
        var viewport = ColumnScroll.ActualWidth > 0 ? ColumnScroll.ActualWidth : ActualWidth - 190;
        for (var i = 0; i < count; i++)
        {
            if (ColumnStack.Children[i] is Border border)
            {
                border.Width = FinderColumnMetrics.WidthAt(i, count, viewport);
            }
        }
    }

    /// <summary>取某目录在当前过滤条件下的行集合；缓存缺失时返回空集合并启动后台载入。</summary>
    private IReadOnlyList<FinderRow> RowsForColumn(string path)
    {
        var key = FinderViewOptionRules.KeyOf(path);
        if (key.Length == 0)
        {
            return Array.Empty<FinderRow>();
        }

        if (_dirCache.TryGetValue(key, out var cached))
        {
            return FilterRows(cached);
        }

        StartColumnLoad(path);
        return Array.Empty<FinderRow>();
    }

    /// <summary>列视图的过滤：隐藏项开关 + 搜索词，与主视图同一套语义。</summary>
    private IReadOnlyList<FinderRow> FilterRows(IReadOnlyList<FinderRow> rows)
    {
        var query = SearchBox?.Text.Trim() ?? string.Empty;
        if (_showHidden && query.Length == 0)
        {
            return rows;
        }

        IEnumerable<FinderRow> source = rows;
        if (!_showHidden)
        {
            source = source.Where(row => !row.IsHidden);
        }

        if (query.Length > 0)
        {
            source = source.Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return source.ToList();
    }

    /// <summary>
    /// 后台载入一个尚未缓存的目录（列视图专用）。代次号用主视图的 <see cref="_loadGeneration"/>：
    /// 导航会自增它，旧结果自然被丢弃；窗口关闭后结果也不会再碰 UI。
    /// </summary>
    private void StartColumnLoad(string path)
    {
        var key = FinderViewOptionRules.KeyOf(path);
        if (key.Length == 0 || !_columnLoading.Add(key))
        {
            return;
        }

        var generation = _loadGeneration;
        _ = Task.Run(() =>
        {
            var result = ReadDirectory(path, _sortKey, _descending, CancellationToken.None);
            return result;
        }).ContinueWith(task =>
        {
            _columnLoading.Remove(key);
            if (task.IsFaulted || task.IsCanceled)
            {
                return;
            }

            var result = task.Result;
            if (result.Error is not null)
            {
                Logger.Warn($"访达列视图读取失败：{path}：{result.Error}");
                return;
            }

            if (generation != _loadGeneration || !IsLoaded || _viewMode != FinderViewMode.Columns)
            {
                return;   // 已经换了目录/视图：不把旧结果塞进新列
            }

            var rows = result.Entries.Select(entry => new FinderRow(path, entry)).ToList();
            foreach (var row in rows)
            {
                row.SetIconBoxSize(_iconSize);
            }

            RememberDirectory(path, rows);
            RefreshColumns();
            StartColumnIconLoad(rows);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 列视图专用图标加载：**不占用**主视图的 <see cref="_iconCts"/>——那一支是"新导航即取消"的语义，
    /// 多列并发时后一列会把前一列的图标任务取消掉，屏幕上就是"只有最后一列有图标"。
    /// 这里改为按列各自后台跑，最多 <see cref="MaxIconRows"/> 个，窗口关闭后自然停下（IsLoaded 判定）。
    /// </summary>
    private void StartColumnIconLoad(IReadOnlyList<FinderRow> rows)
    {
        var pending = rows.Where(row => !row.HasIcon).Take(MaxIconRows).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var pixels = _iconPixelSize;
        _ = Task.Run(async () =>
        {
            foreach (var row in pending)
            {
                if (!IsLoaded)
                {
                    return;
                }

                ImageSource? icon;
                try
                {
                    icon = IconExtractor.GetIcon(row.FullPath, pixels);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"访达列图标提取失败：{row.FullPath} {ex.GetType().Name}");
                    continue;
                }

                if (icon is null)
                {
                    continue;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!row.HasIcon)
                    {
                        row.SetIcon(icon);
                    }
                });
            }
        });
    }

    /// <summary>建一个列的列表控件（样式/模板全部来自 XAML 资源，事件在代码里挂）。</summary>
    private ListBox CreateColumnList(IReadOnlyList<FinderRow> rows, int index, string? selectedName)
    {
        var list = new ListBox
        {
            Style = (Style)FindResource("ColumnList"),
            ItemTemplate = (DataTemplate)FindResource("ColumnRowTemplate"),
            ItemContainerStyle = (Style)FindResource("ColumnListItem"),
            ItemsSource = rows
        };

        list.SelectionChanged += (_, _) =>
        {
            if (_suppressColumnSelection || _columnState is null)
            {
                return;
            }

            if (list.SelectedItem is not FinderRow row)
            {
                return;
            }

            var changed = _columnState.Select(index, row.Name, row.IsDirectory);
            if (!changed)
            {
                return;
            }

            if (row.IsDirectory)
            {
                // 选中文件夹 = 导航（列栈重建为新的祖先链，父列高亮由链推出）
                NavigateTo(row.FullPath);
                return;
            }

            RefreshColumns();
        };

        list.MouseDoubleClick += (_, e) =>
        {
            if (list.SelectedItem is FinderRow row && !row.IsDirectory)
            {
                e.Handled = true;
                OpenRow(row);
            }
        };

        _suppressColumnSelection = true;
        try
        {
            if (selectedName is not null)
            {
                list.SelectedItem = rows.FirstOrDefault(r => r.Name == selectedName);
            }
        }
        finally
        {
            _suppressColumnSelection = false;
        }

        return list;
    }

    /// <summary>
    /// 最右预览列：选中文件时显示大图标/图片缩略/文本头几行 + 元数据；选中文件夹或未选中则不显示。
    /// 这是 macOS 列视图"最右列即预览"的行为。
    /// </summary>
    private Border? BuildPreviewColumn()
    {
        if (_columnState is null)
        {
            return null;
        }

        var index = _columnState.Count - 1;
        var name = _columnState.SelectionAt(index);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var directory = _columnState.Columns[index];
        var rows = _dirCache.TryGetValue(FinderViewOptionRules.KeyOf(directory), out var cached)
            ? cached
            : null;
        var row = rows?.FirstOrDefault(r => r.Name == name);
        if (row is null || row.IsDirectory)
        {
            return null;
        }

        var panel = new StackPanel { Margin = new Thickness(14, 18, 14, 14) };

        var previewHost = BuildPreviewContent(row);
        if (previewHost is not null)
        {
            panel.Children.Add(previewHost);
        }

        panel.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 4),
            Foreground = (Brush)FindResource("MacTextPrimary")
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.KindText} · {row.SizeText}",
            FontSize = 11,
            Foreground = (Brush)FindResource("MacTextSecondary")
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.ModifiedText,
            FontSize = 11,
            Foreground = (Brush)FindResource("MacTextTertiary")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "空格键快速查看",
            FontSize = 10,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = (Brush)FindResource("MacTextTertiary")
        });

        return new Border
        {
            Width = PreviewColumnWidth,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel
        };
    }

    /// <summary>预览列的内容区：图片给缩略图、文本给头几行、其余给 96px 图标。</summary>
    private FrameworkElement? BuildPreviewContent(FinderRow row)
    {
        var kind = QuickLookRouter.KindOf(row.FullPath);
        try
        {
            if (kind == QuickLookKind.Image)
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(row.FullPath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 200;   // 只解到预览列宽度，避免大图吃内存
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();
                return new Image
                {
                    Source = bitmap,
                    MaxHeight = 150,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }

            if (kind == QuickLookKind.Text)
            {
                var lines = ReadHeadLines(row.FullPath, PreviewTextLines);
                return new TextBlock
                {
                    Text = lines,
                    FontFamily = new FontFamily("Consolas, SF Mono, Courier New"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 170,
                    Foreground = (Brush)FindResource("MacTextSecondary")
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达预览列渲染失败：{row.FullPath} {ex.GetType().Name} {ex.Message}");
        }

        return row.Icon is null
            ? null
            : new Image
            {
                Source = row.Icon,
                Width = 96,
                Height = 96,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
    }

    /// <summary>读文本文件头几行（最多 4KB），失败返回空串——预览列永远不因坏文件报错。</summary>
    private static string ReadHeadLines(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>(maxLines);
            for (var i = 0; i < maxLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Add(line.Length > 120 ? line[..120] + "…" : line);
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    // ---------- 列视图交互 ----------

    /// <summary>列视图当前选中的文件行（用于 Enter/空格/简介）。</summary>
    private FinderRow? ColumnSelectedRow()
    {
        if (_columnState is null || _columnVisuals.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp(_columnState.FocusIndex, 0, _columnVisuals.Count - 1);
        return _columnVisuals[index].List.SelectedItem as FinderRow;
    }

    /// <summary>是否有列列表持有键盘焦点（Enter 打开的前置条件）。</summary>
    private bool IsColumnFocusWithin()
        => _columnVisuals.Any(visual => visual.List.IsKeyboardFocusWithin);

    /// <summary>
    /// 列视图 ←/→：→ 打开选中文件夹（无选中则把焦点右移一列），← 焦点左移一列。
    /// 返回 false 表示这次按键没有可做的事，交回默认处理。
    /// </summary>
    private bool HandleColumnArrow(bool right)
    {
        if (_columnState is null)
        {
            return false;
        }

        if (!right)
        {
            if (!_columnState.FocusLeft())
            {
                return false;
            }

            RefreshColumns();
            FocusColumn(_columnState.FocusIndex);
            return true;
        }

        var index = _columnState.FocusIndex;
        var visual = _columnVisuals.FirstOrDefault(v => v.Index == index);
        if (visual?.List.SelectedItem is FinderRow { IsDirectory: true } folder)
        {
            NavigateTo(folder.FullPath);
            return true;
        }

        if (_columnState.FocusRight())
        {
            RefreshColumns();
            FocusColumn(_columnState.FocusIndex);
            return true;
        }

        return false;
    }

    /// <summary>把键盘焦点放到第 index 列的列表上（列重建后焦点会丢，必须显式还回去）。</summary>
    private void FocusColumn(int index)
    {
        var visual = _columnVisuals.FirstOrDefault(v => v.Index == index);
        visual?.List.Focus();
    }

    /// <summary>分栏视图的后退：丢掉最右列（不改变当前目录时只退一列视觉层级）。</summary>
    private bool StepBackColumn()
    {
        if (_columnState is null || _columnState.Count <= 1)
        {
            return false;
        }

        var parent = FinderPaths.Parent(_columnState.Current);
        if (parent is null)
        {
            return false;
        }

        // 退一列 = 导航到父目录；列栈由祖先链重建，父目录成为新的最右列。
        NavigateTo(parent);
        return true;
    }

    private void ColumnScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 视口宽度变化（窗口缩放/滚动条出现）时列宽要跟着重算。
        if (Math.Abs(e.ViewportWidthChange) > 0.5 || Math.Abs(e.ExtentWidthChange) > 0.5)
        {
            RefreshColumnWidths();
        }
    }
}
