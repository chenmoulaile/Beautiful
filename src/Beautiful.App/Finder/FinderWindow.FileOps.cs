using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MessageBox = System.Windows.MessageBox;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 文件操作（Phase 6-c）：拷贝/剪切/粘贴、移到废纸篓、立即删除、新建文件夹、就地重命名、右键菜单。
/// <para>
/// 所有落盘动作都经 <see cref="FileOperationService"/>（<c>IFileOperation</c>），因此回收站语义、
/// 跨卷移动、长路径、冲突处理都是系统的；本文件只负责"选了什么 → 生成什么计划 → 报告什么结果"。
/// 命名避让与"复制到同目录生成 copy 名"由 Core 的 <see cref="FinderPastePlanner"/> 决定，
/// 界面不自己拼名字。
/// </para>
/// <para>
/// 剪贴板：进程内保留 <see cref="FinderClipboard"/>（含剪切语义），同时把文件列表写进系统剪贴板
/// （<c>CF_HDROP</c> + <c>Preferred DropEffect</c>），这样与资源管理器互拷也能用；
/// 粘贴时优先用进程内记录，没有则读系统剪贴板的文件列表——**不做"跨进程剪切"**，
/// 从资源管理器剪切过来会被当成复制（诚实降级，写进 README）。
/// </para>
/// </summary>
public partial class FinderWindow
{
    private readonly FileOperationService _fileOps = new();

    private FinderClipboard _clipboard = FinderClipboard.Empty;
    private bool _fileOpRunning;

    private Popup? _renamePopup;
    private TextBox? _renameBox;
    private FinderRow? _renameRow;

    // ---------- 选中集合 ----------

    /// <summary>当前视图的选中行（顺序即控件顺序；分栏视图只有当前列的一项，画廊视图单选）。</summary>
    private List<FinderRow> SelectedRows()
    {
        switch (_viewMode)
        {
            case FinderViewMode.Icons:
                return IconList.SelectedItems.OfType<FinderRow>().ToList();
            case FinderViewMode.Columns:
                return ColumnSelectedRow() is { } columnRow ? [columnRow] : [];
            case FinderViewMode.Gallery:
                return _gallerySelection is { } galleryRow ? [galleryRow] : [];
            default:
                return FileList.SelectedItems.OfType<FinderRow>().ToList();
        }
    }

    // ---------- 拷贝 / 剪切 / 粘贴 ----------

    private void CopySelection(bool cut)
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            return;
        }

        var paths = rows.Select(row => row.FullPath).ToList();
        _clipboard = FinderClipboard.Of(paths, cut ? FinderClipboardMode.Cut : FinderClipboardMode.Copy);
        WriteSystemClipboard(paths, cut);
        ShowStatusHint(cut ? $"已剪切 {rows.Count} 项" : $"已拷贝 {rows.Count} 项");
    }

    /// <summary>把文件列表写进系统剪贴板（CF_HDROP）；剪切时附 Preferred DropEffect=Move。</summary>
    private static void WriteSystemClipboard(IReadOnlyList<string> paths, bool cut)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.Cast<string>().ToArray());
            if (cut)
            {
                // Explorer 认这个字节流：2 = Move，5 = Copy|Link 之外最常用的 Copy
                using var stream = new MemoryStream([2, 0, 0, 0]);
                data.SetData("Preferred DropEffect", stream);
            }

            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                      or System.Runtime.InteropServices.ExternalException
                                      or InvalidOperationException)
        {
            // 剪贴板被别的进程锁住是常态：进程内记录仍然有效，只是不能跨应用粘贴
            Logger.Warn($"写入系统剪贴板失败：{ex.GetType().Name}");
        }
    }

    /// <summary>读系统剪贴板的文件列表（进程内剪贴板为空时的兜底；一律按复制处理）。</summary>
    private static List<string> ReadSystemClipboardFiles()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                return Clipboard.GetFileDropList().Cast<string>().Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                      or System.Runtime.InteropServices.ExternalException
                                      or InvalidOperationException)
        {
            Logger.Warn($"读取系统剪贴板失败：{ex.GetType().Name}");
        }

        return [];
    }

    private async void PasteIntoCurrent()
    {
        if (_fileOpRunning)
        {
            ShowStatusHint("上一个文件操作还在进行");
            return;
        }

        var clipboard = _clipboard.IsEmpty
            ? FinderClipboard.Of(ReadSystemClipboardFiles(), FinderClipboardMode.Copy)
            : _clipboard;

        if (clipboard.IsEmpty)
        {
            ShowStatusHint("剪贴板里没有文件");
            return;
        }

        var existing = SafeNames(_currentPath);
        var plan = FinderPastePlanner.Plan(
            clipboard,
            _currentPath,
            existing,
            exists: path => File.Exists(path) || Directory.Exists(path),
            isDirectory: Directory.Exists);

        var runnable = plan.Where(item => item.WillExecute).ToList();
        if (runnable.Count == 0)
        {
            var reason = plan.FirstOrDefault(item => item.Status != FinderPasteStatus.Ready).Status;
            ShowStatusHint(reason switch
            {
                FinderPasteStatus.SameDirectory => "这些项目已经在这个文件夹里",
                FinderPasteStatus.IntoItself => "不能把文件夹粘贴到它自己里面",
                FinderPasteStatus.Missing => "源项目已不存在",
                _ => "没有可粘贴的项目"
            });
            return;
        }

        var items = runnable.Select(item => new FileOpItem(item.SourcePath, item.TargetPath)).ToList();
        var verb = clipboard.Mode == FinderClipboardMode.Cut ? "移动" : "拷贝";
        await ExecuteAsync(verb, items.Count, (progress, token, hwnd) => clipboard.Mode == FinderClipboardMode.Cut
            ? _fileOps.MoveAsync(items, progress, token, hwnd)
            : _fileOps.CopyAsync(items, progress, token, hwnd));

        if (clipboard.Mode == FinderClipboardMode.Cut)
        {
            _clipboard = FinderClipboard.Empty;   // 剪切是一次性的
            ClearSystemClipboard();
        }
    }

    private static void ClearSystemClipboard()
    {
        try
        {
            Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Logger.Warn($"清空剪贴板失败：{ex.GetType().Name}");
        }
    }

    /// <summary>目标目录现有名称（用于命名避让）；读失败返回空集合（宁可撞名让系统报错，也不静默跳过）。</summary>
    private static IReadOnlyList<string> SafeNames(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos().Select(info => info.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return [];
        }
    }

    // ---------- 删除 ----------

    /// <summary>移到废纸篓（⌘⌫ / Delete）。macOS 语义：不弹确认，可从回收站还原。</summary>
    private async void RecycleSelection()
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || _fileOpRunning)
        {
            return;
        }

        var paths = rows.Select(row => row.FullPath).ToList();
        await ExecuteAsync("移到废纸篓", paths.Count,
            (progress, token, hwnd) => _fileOps.RecycleAsync(paths, progress, token, hwnd));
    }

    /// <summary>立即删除（⇧⌫）：不可恢复，因此**必须**二次确认。</summary>
    private async void DeleteSelectionPermanent()
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || _fileOpRunning)
        {
            return;
        }

        var confirm = MessageBox.Show(this,
            $"要永久删除 {rows.Count} 个项目吗？此操作无法撤销。",
            "立即删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var paths = rows.Select(row => row.FullPath).ToList();
        await ExecuteAsync("删除", paths.Count, (progress, token, hwnd) => Task.Run(() =>
        {
            // 永久删除**不进回收站**：这里用托管 API 直接删（IFileOperation 的删除恒走回收站语义，
            // 想绕过只能清 FOF_ALLOWUNDO，但那样会同时失去"删除失败可继续"的 Shell 容错）。
            // 大目录递归删除可能耗时，因此整段跑在线程池上，UI 只收进度。
            var completed = 0;
            var failed = 0;
            string? lastError = null;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }

                    completed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
                {
                    failed++;
                    lastError = ex.Message;
                }

                progress?.Report(new FileOperationProgress(completed + failed, paths.Count, Path.GetFileName(path)));
            }

            return failed == 0
                ? FileOperationResult.Ok(completed)
                : FileOperationResult.Fail(lastError ?? "部分项目删除失败", completed, failed);
        }, token));
    }

    // ---------- 新建文件夹 ----------

    private async void CreateFolder()
    {
        if (_fileOpRunning)
        {
            return;
        }

        var name = FinderNameRules.Unique("未命名文件夹", SafeNames(_currentPath));
        await ExecuteAsync("新建文件夹", 1,
            (progress, token, hwnd) => _fileOps.NewFolderAsync(_currentPath, name, hwnd),
            selectAfter: name);
    }

    // ---------- 重命名（就地编辑） ----------

    /// <summary>
    /// Enter / F2：在当前选中项上浮出编辑框（Popup 定位到条目容器上，四种视图通用）。
    /// 提交走 Shell 的 <c>RenameItem</c>（保留扩展名提示、处理只读/占用等系统错误）。
    /// </summary>
    private void BeginRename()
    {
        var row = SelectedRow();
        if (row is null || _fileOps is null)
        {
            return;
        }

        var container = ContainerOf(row);
        if (container is null)
        {
            ShowStatusHint("该条目尚未进入可视区域");
            return;
        }

        CloseRenamePopup();
        _renameRow = row;

        var box = new TextBox
        {
            Text = row.Name,
            FontSize = 12,
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 120,
            Background = (Brush)FindResource("MacWindowBackground"),
            Foreground = (Brush)FindResource("MacTextPrimary"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF)),
            BorderThickness = new Thickness(1),
            CaretBrush = (Brush)FindResource("MacTextPrimary")
        };
        box.KeyDown += RenameBox_KeyDown;
        box.LostKeyboardFocus += (_, _) => CommitRename(confirm: true);
        _renameBox = box;

        _renamePopup = new Popup
        {
            PlacementTarget = container,
            Placement = PlacementMode.Relative,
            HorizontalOffset = 20,
            VerticalOffset = 0,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = box
        };
        _renamePopup.IsOpen = true;
        box.Focus();
        box.SelectAll();
    }

    private void RenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(confirm: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CommitRename(confirm: false);
        }
    }

    private async void CommitRename(bool confirm)
    {
        var row = _renameRow;
        var box = _renameBox;
        if (row is null || box is null)
        {
            CloseRenamePopup();
            return;
        }

        var newName = box.Text.Trim();
        CloseRenamePopup();

        if (!confirm || newName.Length == 0 || string.Equals(newName, row.Name, StringComparison.Ordinal))
        {
            return;
        }

        var invalid = FinderNameRules.Validate(newName);
        if (invalid is not null)
        {
            ShowStatusHint($"无法重命名：{invalid}");
            return;
        }

        await ExecuteAsync("重命名", 1,
            (progress, token, hwnd) => _fileOps.RenameAsync(row.FullPath, newName, progress, token, hwnd),
            selectAfter: newName);
    }

    private void CloseRenamePopup()
    {
        if (_renamePopup is not null)
        {
            _renamePopup.IsOpen = false;
            _renamePopup = null;
        }

        _renameBox = null;
        _renameRow = null;
    }

    /// <summary>找到某行对应的可视容器（四种视图各自的可视容器）。</summary>
    private FrameworkElement? ContainerOf(FinderRow row)
    {
        switch (_viewMode)
        {
            case FinderViewMode.Icons:
                return IconList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
            case FinderViewMode.Columns:
                foreach (var visual in _columnVisuals)
                {
                    if (visual.List.Items.Contains(row))
                    {
                        return visual.List.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
                    }
                }

                return null;
            case FinderViewMode.Gallery:
                return _galleryThumbs.TryGetValue(row, out var thumb) ? thumb : null;
            default:
                return FileList.ItemContainerGenerator.ContainerFromItem(row) as ListViewItem;
        }
    }

    // ---------- 统一执行 ----------

    /// <summary>
    /// 跑一次文件操作：进度卡 + 取消 + 完成刷新 + 结果提示。所有入口共用，保证行为一致。
    /// </summary>
    /// <param name="verb">动作名（"拷贝"/"移动"/"移到废纸篓"…），用于标题与状态栏。</param>
    /// <param name="total">总项数。</param>
    /// <param name="run">真正的操作（拿到进度回调、取消令牌、窗口句柄）。</param>
    /// <param name="selectAfter">操作成功后要选中的新名称（新建/重命名用）。</param>
    private async Task ExecuteAsync(string verb, int total,
        Func<IProgress<FileOperationProgress>?, CancellationToken, IntPtr, Task<FileOperationResult>> run,
        string? selectAfter = null)
    {
        if (_fileOpRunning)
        {
            ShowStatusHint("上一个文件操作还在进行");
            return;
        }

        _fileOpRunning = true;
        var window = FileProgressWindow.Show($"正在{verb} {total} 项…", total, this);
        var progress = new Progress<FileOperationProgress>(window.Report);
        var hwnd = new WindowInteropHelper(this).Handle;

        try
        {
            var result = await run(progress, window.Token, hwnd);
            window.Finish(result, verb);

            if (result.Success)
            {
                ShowStatusHint($"{verb}完成：{result.Completed} 项");
                // 先登记"完成后要选中的名字"，再刷新：载入完成回调在后台线程触发，
                // 反过来的顺序会有竞态（快目录可能在赋值前就载入完）。
                if (selectAfter is not null)
                {
                    _pendingSelection = selectAfter;
                }

                RefreshCurrentDirectory();
            }
            else if (!result.Cancelled)
            {
                ShowStatusHint($"{verb}失败：{result.Error}");
                Logger.Warn($"访达{verb}失败：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达{verb}异常：{ex.GetType().Name} {ex.Message}");
            window.Finish(FileOperationResult.Fail(ex.Message), verb);
            ShowStatusHint($"{verb}失败：{ex.Message}");
        }
        finally
        {
            _fileOpRunning = false;
        }
    }

    /// <summary>全选（⌘A）：列表/图标视图支持多选，分栏视图按列语义只选当前列全部。</summary>
    private void SelectAllRows()
    {
        switch (_viewMode)
        {
            case FinderViewMode.Icons:
                IconList.SelectAll();
                IconList.Focus();
                break;
            case FinderViewMode.Columns:
                if (_columnState is not null)
                {
                    var visual = _columnVisuals.FirstOrDefault(v => v.Index == _columnState.FocusIndex);
                    visual?.List.SelectAll();
                    visual?.List.Focus();
                }

                break;
            case FinderViewMode.Gallery:
                // 画廊是单选视图：不谎报"全选了"，直接说明边界
                ShowStatusHint("画廊视图为单选（Ctrl+1/2/3 切到其它视图可多选）");
                break;
            default:
                FileList.SelectAll();
                FileList.Focus();
                break;
        }
    }

    // ---------- 右键菜单 ----------

    /// <summary>
    /// 四视图共用的 macOS 右键菜单。每次打开时按当前选中状态重建（项目数、剪贴板状态都会变），
    /// 不用命令绑定那套——菜单项只有十来个，重建比维护 CanExecute 便宜。
    /// </summary>
    private ContextMenu BuildContextMenu(FinderRow? row)
    {
        var menu = new ContextMenu();

        if (row is not null)
        {
            menu.Items.Add(Item("打开", () => OpenRow(row), !row.IsDirectory));
            menu.Items.Add(Item("在新窗口中打开", () => FinderWindow.ShowSingleton(row.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("快速查看", () => QuickLookFromSelection()));
            menu.Items.Add(Item("显示简介", () => FinderInfoWindow.ShowFor(row.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("拷贝", () => CopySelection(cut: false)));
            menu.Items.Add(Item("剪切", () => CopySelection(cut: true)));
            menu.Items.Add(Item("重命名", BeginRename));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("移到废纸篓", RecycleSelection));
        }
        else
        {
            menu.Items.Add(Item("粘贴", PasteIntoCurrent, !_clipboard.IsEmpty || Clipboard.ContainsFileDropList()));
            menu.Items.Add(Item("新建文件夹", CreateFolder));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("显示简介", () => FinderInfoWindow.ShowFor(_currentPath)));
            menu.Items.Add(Item("刷新", RefreshCurrentDirectory));
        }

        return menu;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>给三个视图挂上右键菜单（构造函数末尾调用一次）。</summary>
    private void AttachContextMenus()
    {
        FileList.ContextMenuOpening += (_, e) =>
        {
            FileList.ContextMenu = BuildContextMenu(FileList.SelectedItem as FinderRow);
            if (FileList.ContextMenu is null)
            {
                e.Handled = true;
            }
        };

        IconList.ContextMenuOpening += (_, e) =>
        {
            IconList.ContextMenu = BuildContextMenu(IconList.SelectedItem as FinderRow);
            if (IconList.ContextMenu is null)
            {
                e.Handled = true;
            }
        };
    }
}
