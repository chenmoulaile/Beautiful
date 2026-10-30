using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口（Phase 6-a 骨架）：macOS Big Sur 风格的三段式窗口——工具栏 / 侧栏 + 列表 / 状态栏。
/// <para>
/// 窗口形态与 <see cref="Settings.SettingsWindow"/> 一致：非分层窗口 + WindowChrome + DWM 圆角，
/// 保留 ClearType 与 DWM 阴影（AllowsTransparency 保持 false，见 XAML）。CaptionHeight=0，
/// 顶部条带由 <see cref="TitleBar_MouseLeftButtonDown"/> 自绘拖动。
/// </para>
/// <para>
/// 导航/键位/排序全部复用 <c>Beautiful.Core.Finder</c> 的纯函数，UI 只负责把状态映射到控件：
/// <see cref="NavigationHistory"/> 管后退/前进，<see cref="FinderKeyMap"/> 认 Ctrl+[ / ] / W / Escape，
/// <see cref="FinderSorting"/> 给列表定序，<see cref="FinderPaths"/> 负责路径拼接与根判定。
/// 目录读取在后台线程完成（<see cref="Task.Run{TResult}(Func{TResult})"/> + <see cref="CancellationTokenSource"/>），
/// 最多取 <see cref="MaxEntries"/> 项；访问失败只清空列表并写状态栏/日志，绝不让窗口崩溃。
/// </para>
/// <para>
/// 这是骨架：列视图 / 图标视图 / 搜索索引 / 文件操作都留到 Phase 6-b；工具栏的对应按钮可点，
/// 只在状态栏给出非模态提示，不做假切换。搜索框的过滤是真实行为（只筛当前目录已载入的名称）。
/// </para>
/// </summary>
public partial class FinderWindow : Window
{
    /// <summary>单次目录读取的条目上限（目录过大时保证首屏与滚动仍可用）。</summary>
    private const int MaxEntries = 1500;

    /// <summary>后台图标提取的行数上限：超出部分保留字形回退，避免 1500 项一次性抓 shell 图标。</summary>
    private const int MaxIconRows = 300;

    /// <summary>图标提取尺寸：16 DIP 在 200% DPI 下是 32 物理像素。</summary>
    private const int IconPixelSize = 32;

    private static FinderWindow? _current;

    private readonly NavigationHistory _history = new();
    private readonly DispatcherTimer _statusHintTimer;

    private List<FinderRow> _allRows = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _iconCts;
    private string _currentPath = string.Empty;
    private string? _loadError;
    private string? _freeSpaceText;
    private bool _capped;
    private bool _suppressSidebar;
    private bool _isLoading;
    private int _loadGeneration;

    /// <summary>单例入口（--finder / 将来的 Dock 入口共用）。关闭后自动重置，可再次打开。</summary>
    public static void ShowSingleton(string? initialPath = null)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Activate();
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                _current.NavigateTo(initialPath!);
            }

            return;
        }

        var window = new FinderWindow(initialPath);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }
        };
        window.Show();
        window.Activate();
        Logger.Info($"访达窗口已打开：{window.CurrentPath}");
    }

    public FinderWindow()
        : this(null)
    {
    }

    public FinderWindow(string? initialPath)
    {
        InitializeComponent();

        _statusHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusHintTimer.Tick += (_, _) =>
        {
            _statusHintTimer.Stop();
            UpdateStatus();
        };

        var path = string.IsNullOrWhiteSpace(initialPath) ? DefaultDirectory() : initialPath!;
        _currentPath = path;
        _history.Visit(path);

        PopulateSidebar();
        SyncSidebarSelection();
        ApplyPath(path);
        UpdateNavButtons();

        Loaded += (_, _) => UpdateNameColumnWidth();
        SizeChanged += (_, _) => UpdateNameColumnWidth();
        Closed += (_, _) =>
        {
            // 关闭即作废：既取消在跑的后台枚举/图标任务，也顺手释放 CTS（Dispose 幂等）。
            _loadGeneration++;
            _statusHintTimer.Stop();
            CancelPendingWork();
        };

        StartLoad(path);
    }

    /// <summary>当前目录（日志/外部查询用；页面状态以 <see cref="_currentPath"/> 为准）。</summary>
    public string CurrentPath => _currentPath;

    // ---------- 初始化 ----------

    private static string DefaultDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? "C:\\" : desktop;
    }

    /// <summary>侧栏固定项（Brief 指定的六个位置）；环境变量取不到的项直接省略，不生成空白行。</summary>
    private void PopulateSidebar()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var items = new List<SidebarItem>
        {
            new("最近使用", string.IsNullOrWhiteSpace(appData)
                ? string.Empty
                : Path.Combine(appData, "Microsoft", "Windows", "Recent")),
            new("应用程序", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            new("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            new("文稿", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("下载", string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile, "Downloads")),
            new("这台电脑", Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
        };

        Sidebar.ItemsSource = items.Where(i => !string.IsNullOrWhiteSpace(i.Path)).ToList();
    }

    /// <summary>把侧栏选中项同步到当前路径（程序化赋值期间屏蔽 SelectionChanged，避免二次导航）。</summary>
    private void SyncSidebarSelection()
    {
        if (Sidebar.ItemsSource is not IEnumerable<SidebarItem> items)
        {
            return;
        }

        _suppressSidebar = true;
        try
        {
            Sidebar.SelectedItem = items.FirstOrDefault(i => SamePath(i.Path, _currentPath));
        }
        finally
        {
            _suppressSidebar = false;
        }
    }

    private void ApplyPath(string path)
    {
        PathText.Text = path;
        PathText.ToolTip = path;
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _history.CanGoBack;
        ForwardButton.IsEnabled = _history.CanGoForward;
    }

    /// <summary>名称列吃掉剩余宽度：窗口从 760 到 1100 之间变化时，其余三列保持固定。</summary>
    private void UpdateNameColumnWidth()
    {
        if (NameColumn is null || FileList is null)
        {
            return;
        }

        // 150(修改日期) + 90(大小) + 110(种类) + 24(纵向滚动条/边距余量)
        NameColumn.Width = Math.Max(180, FileList.ActualWidth - 374);
    }

    // ---------- 导航 ----------

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        var a = left.TrimEnd('\\', '/');
        var b = right.TrimEnd('\\', '/');
        if (a.Length == 0)
        {
            a = left; // 纯根 "\" / "/"
        }

        if (b.Length == 0)
        {
            b = right;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void Sidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebar)
        {
            return;
        }

        if (Sidebar.SelectedItem is SidebarItem item)
        {
            NavigateTo(item.Path);
        }
    }

    /// <summary>侧栏/双击文件夹的唯一导航入口：历史入栈（Core 判重）→ 统一换目录。</summary>
    private void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || SamePath(path, _currentPath))
        {
            return;
        }

        try
        {
            _history.Visit(path);
        }
        catch (ArgumentException)
        {
            return; // Core 契约：空白路径非法；UI 侧不把异常抛给用户
        }

        ShowDirectory(path);
    }

    private void GoBack()
    {
        var path = _history.Back();
        if (path is null)
        {
            return;
        }

        ShowDirectory(path);
    }

    private void GoForward()
    {
        var path = _history.Forward();
        if (path is null)
        {
            return;
        }

        ShowDirectory(path);
    }

    /// <summary>
    /// 换到另一个目录：先切换标题/历史按钮，再进入载入态（清空旧行、关闭行交互、状态栏"正在载入…"），
    /// 最后清掉搜索词（否则新目录会被旧查询静默过滤）。F5 刷新同一目录不走这里，搜索词保留。
    /// </summary>
    private void ShowDirectory(string path)
    {
        _currentPath = path;
        ApplyPath(path);
        SyncSidebarSelection();
        UpdateNavButtons();
        StartLoad(path);
        ResetSearch();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    // ---------- 目录读取 ----------

    /// <summary>
    /// 进入载入态并启动一次目录读取。每次导航（含 F5）都走这里：
    /// 先取消/释放上一轮载入与图标任务，再用新的代次号清空旧行并写入"正在载入…"。
    /// 旧载入的完成回调会因代次号不匹配被丢弃，绝不覆盖新目录。
    /// </summary>
    private void StartLoad(string path)
    {
        _loadGeneration++;
        var generation = _loadGeneration;

        CancelPendingWork();

        _isLoading = true;
        _loadError = null;
        _freeSpaceText = null;
        _capped = false;
        _allRows = [];
        FileList.ItemsSource = Array.Empty<FinderRow>();
        FileList.IsEnabled = false;   // 载入完成前不允许双击/键盘打开旧行
        _statusHintTimer.Stop();
        StatusText.Text = "正在载入…";

        _ = LoadDirectoryAsync(path, generation);
    }

    /// <summary>取消并释放当前载入 / 图标任务的 CTS（窗口关闭或新导航取代旧载入时调用）。</summary>
    private void CancelPendingWork()
    {
        var load = _loadCts;
        _loadCts = null;
        if (load is not null)
        {
            load.Cancel();
            load.Dispose();
        }

        var icons = _iconCts;
        _iconCts = null;
        if (icons is not null)
        {
            icons.Cancel();
            icons.Dispose();
        }
    }

    /// <summary>
    /// 后台读取当前目录并把结果映射到 UI。代次号 + token 双重校验，防止"旧目录结果覆盖新目录"；
    /// 本次载入的 CTS 在成功/失败/取消三条路径的 finally 里释放（被新导航取代时 StartLoad 已提前释放）。
    /// 所有异常在方法内收口（窗口不因磁盘/权限问题崩溃）。
    /// </summary>
    private async Task LoadDirectoryAsync(string path, int generation)
    {
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        try
        {
            var result = await Task.Run(() => ReadDirectory(path, token), token);
            if (generation != _loadGeneration || token.IsCancellationRequested || !SamePath(path, _currentPath))
            {
                return;
            }

            ApplyLoadResult(path, result, generation);
        }
        catch (OperationCanceledException)
        {
            // 新导航已接管（或窗口已关闭）：丢弃旧结果即可
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达读取目录失败：{path} {ex.GetType().Name} {ex.Message}");
            if (generation == _loadGeneration && !token.IsCancellationRequested && SamePath(path, _currentPath))
            {
                ApplyError(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }

            cts.Dispose();   // 成功/失败/取消都释放；被取代时这里的 Dispose 与 StartLoad 的重复调用是幂等的
        }
    }

    private static LoadResult ReadDirectory(string path, CancellationToken token)
    {
        // 可用空间在后台枚举线程上算一次（IO），状态栏与搜索按键都只复用这个字符串，绝不再碰 DriveInfo。
        var freeSpace = FreeSpaceGb(path);

        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return LoadResult.Failed("文件夹不存在或无法访问", freeSpace);
            }

            var entries = new List<FinderEntry>(128);
            var capped = false;
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries)
                {
                    capped = true;
                    break;
                }

                try
                {
                    var isDirectory = info is DirectoryInfo;
                    var size = 0L;
                    if (!isDirectory && info is FileInfo file)
                    {
                        size = file.Length;
                    }

                    var kind = isDirectory ? "文件夹" : KindOf(info.Name);
                    entries.Add(new FinderEntry(info.Name, isDirectory, size, info.LastWriteTimeUtc, kind));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 枚举途中被删除/改权限的单个条目：跳过，不让一项毁掉整份列表。
                }
            }

            // 文件夹恒在前 + 名称自然序由 Core 固化（file2 < file10）；UI 不再写第二套排序。
            var sorted = FinderSorting.Sort(entries, FinderSortKey.Name);
            return LoadResult.Success(sorted, capped, freeSpace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException
                                      or IOException or System.Security.SecurityException
                                      or ArgumentException or NotSupportedException)
        {
            return LoadResult.Failed(ex.Message, freeSpace);
        }
    }

    private void ApplyLoadResult(string path, LoadResult result, int generation)
    {
        if (result.Error is not null)
        {
            ApplyError(result.Error);
            Logger.Warn($"访达无法读取：{path}：{result.Error}");
            return;
        }

        _isLoading = false;
        _loadError = null;
        _capped = result.Capped;
        _freeSpaceText = result.FreeSpaceText;
        _allRows = result.Entries.Select(entry => new FinderRow(path, entry)).ToList();
        FileList.IsEnabled = true;
        ApplyFilter();
        UpdateStatus();
        StartIconLoad(_allRows, generation);
        Logger.Info($"访达目录已载入：{path}，条目={_allRows.Count}" +
                    (result.Capped ? "（已截断）" : string.Empty));
    }

    /// <summary>错误路径：列表清空 + 状态栏给出原因（日志由调用方写）；保留当前路径，后退依然可用。</summary>
    private void ApplyError(string reason)
    {
        _isLoading = false;
        _loadError = reason;
        _capped = false;
        _freeSpaceText = null;
        _allRows.Clear();
        FileList.ItemsSource = Array.Empty<FinderRow>();
        FileList.IsEnabled = true;
        UpdateStatus();
    }

    // ---------- 图标 ----------

    /// <summary>
    /// 载入结果应用后，在后台顺序提取前 <see cref="MaxIconRows"/> 行的图标（32px），每拿到一个就回 UI 线程
    /// 贴到行上；未拿到的行继续显示字形。图标任务有独立 CTS，新导航/关闭会取消它，代次号再兜一层防串。
    /// </summary>
    private void StartIconLoad(IReadOnlyList<FinderRow> rows, int generation)
    {
        var previous = _iconCts;
        _iconCts = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var count = Math.Min(rows.Count, MaxIconRows);
        if (count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _iconCts = cts;
        _ = LoadIconsAsync(rows, count, generation, cts);
    }

    private async Task LoadIconsAsync(IReadOnlyList<FinderRow> rows, int count, int generation,
        CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                var icon = await Task.Run(() => IconExtractor.GetIcon(row.FullPath, IconPixelSize), token);
                if (icon is null || token.IsCancellationRequested || generation != _loadGeneration)
                {
                    continue;   // 空结果保留字形；过期结果直接丢弃
                }

                // await 回到 UI 线程：PropertyChanged 由 WPF 绑定消费，行对象只在 UI 线程改。
                row.SetIcon(icon);
            }
        }
        catch (OperationCanceledException)
        {
            // 新导航/关闭：停止提取，未贴的图标不再贴
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达图标提取失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_iconCts, cts))
            {
                _iconCts = null;
            }

            cts.Dispose();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // XAML 解析期控件字段尚未全部就绪：只在窗口完成初始化后响应。
        if (!IsInitialized || SearchPlaceholder is null || FileList is null || StatusText is null)
        {
            return;
        }

        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
        UpdateStatus();
    }

    /// <summary>换目录时清空搜索词；F5 刷新同一目录不调用，保留用户当前筛选。</summary>
    private void ResetSearch()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;   // TextChanged 会顺带恢复占位符并重新过滤
        }
    }

    /// <summary>名称过滤（当前目录已载入的行；真实行为，不是 Phase 6-b 的索引搜索）。</summary>
    private void ApplyFilter()
    {
        if (_isLoading)
        {
            FileList.ItemsSource = Array.Empty<FinderRow>();
            return;
        }

        var query = SearchBox.Text.Trim();
        IEnumerable<FinderRow> source = _allRows;
        if (query.Length > 0)
        {
            source = _allRows.Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FileList.ItemsSource = source.ToList();
    }

    private void UpdateStatus()
    {
        if (StatusText is null)
        {
            return;
        }

        if (_isLoading)
        {
            StatusText.Text = "正在载入…";
            return;
        }

        if (_loadError is not null)
        {
            StatusText.Text = $"无法读取：{_loadError}";
            return;
        }

        var total = _allRows.Count;
        var shown = FileList?.Items.Count ?? 0;
        var text = _capped
            ? $"仅显示前 {total} 个项目（目录内容过多）"
            : $"{total} 个项目";
        if (shown != total)
        {
            text += $"（筛选出 {shown} 个）";
        }

        // 可用空间是载入时后台算好的字符串：搜索每次按键都不再访问磁盘。
        if (_freeSpaceText is not null)
        {
            text += $"，可用 {_freeSpaceText} GB";
        }

        StatusText.Text = text;
    }

    /// <summary>
    /// 路径所在盘的可用空间（GB，一位小数）；在后台枚举线程上每次载入调用一次。
    /// UNC/未就绪/无权限/盘符消失一律返回 null（状态栏只显示项目数），绝不让载入失败。
    /// </summary>
    private static string? FreeSpaceGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return (drive.AvailableFreeSpace / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    private static string KindOf(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) ? "文件" : extension.TrimStart('.').ToLowerInvariant();
    }

    // ---------- 打开 ----------

    private void Row_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: FinderRow row })
        {
            e.Handled = true;
            OpenRow(row);
        }
    }

    private void OpenSelected()
    {
        if (FileList.SelectedItem is FinderRow row)
        {
            OpenRow(row);
        }
    }

    private void OpenRow(FinderRow row)
    {
        if (_isLoading)
        {
            return;   // 载入态下列表行不可交互（旧行也已清空，这里再兜一层）
        }

        if (row.IsDirectory)
        {
            NavigateTo(row.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 文件可能已被删除、无关联程序、路径过长……形态太多，窗口一律降级为状态栏提示。
            Logger.Warn($"访达打开文件失败：{row.FullPath} {ex.GetType().Name} {ex.Message}");
            ShowStatusHint($"无法打开：{ex.Message}");
        }
    }

    // ---------- 键盘 ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            StartLoad(_currentPath);   // 同一目录刷新：保留搜索词
            return;
        }

        // Enter 只在列表获得焦点时打开选中项：搜索框/按钮里的 Enter 保持原生语义。
        if (e.Key == Key.Enter && FileList.IsKeyboardFocusWithin
            && e.OriginalSource is not TextBox && Keyboard.FocusedElement is not Button)
        {
            e.Handled = true;
            OpenSelected();
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        string? gesture = ctrl && e.Key == Key.OemOpenBrackets ? "ctrl+["
            : ctrl && e.Key == Key.OemCloseBrackets ? "ctrl+]"
            : ctrl && e.Key == Key.W ? "ctrl+w"
            : e.Key == Key.Escape ? "escape"
            : null;
        if (gesture is null)
        {
            return;
        }

        switch (FinderKeyMap.Map(gesture))
        {
            case FinderCommand.Back:
                e.Handled = true;
                GoBack();
                break;
            case FinderCommand.Forward:
                e.Handled = true;
                GoForward();
                break;
            case FinderCommand.CloseTab:
            case FinderCommand.Cancel:
                e.Handled = true;
                Close();
                break;
        }
    }

    // ---------- 工具栏 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已在 DragMove 前抬起：无操作
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void List_Click(object sender, RoutedEventArgs e)
        => ShowStatusHint("当前为列表视图");

    /// <summary>列视图 / 图标视图是 Phase 6-b 的实现项：按钮可点，但只给状态栏提示，不做假切换。</summary>
    private void DeferredView_Click(object sender, RoutedEventArgs e)
        => ShowStatusHint("列视图 / 图标视图将在 Phase 6-b 实现（当前仍为列表视图）");

    /// <summary>非模态提示：占用状态栏 3 秒后恢复项目数/可用空间，不弹窗、不阻塞操作。</summary>
    private void ShowStatusHint(string message)
    {
        _statusHintTimer.Stop();
        StatusText.Text = message;
        _statusHintTimer.Start();
    }

    // ---------- 窗口圆角 ----------

    /// <summary>WindowStyle=None 后系统默认给方角窗口；显式请求 DWM ROUND，客户区由 DWM 裁剪，无需分层窗口。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle,
            NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    // ---------- 行视图模型 ----------

    /// <summary>
    /// 列表行：展示字段来自 Core 的 <see cref="FinderEntry"/>。图标不再由绑定 getter 同步提取——
    /// 载入结果应用后由窗口在后台批量提取前 300 行，拿到一个就在 UI 线程调用 <see cref="SetIcon"/>；
    /// 在此之前行显示字形回退，滚动进入视口也绝不触发磁盘/Shell 调用。
    /// 提取尺寸 32px：16 DIP 在 200% DPI 下正好是 32 物理像素，高分屏不糊。
    /// </summary>
    private sealed class FinderRow : INotifyPropertyChanged
    {
        private readonly FinderEntry _entry;
        private string _glyph;
        private ImageSource? _icon;

        public FinderRow(string directory, FinderEntry entry)
        {
            _entry = entry;
            FullPath = FinderPaths.Combine(directory, entry.Name);
            _glyph = entry.IsDirectory ? "▤" : "▣";
            ModifiedText = entry.ModifiedUtc.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            SizeText = entry.IsDirectory ? "—" : FormatSize(entry.Size);
            KindText = string.IsNullOrEmpty(entry.Kind) ? "文件" : entry.Kind;
        }

        public string Name => _entry.Name;

        public string FullPath { get; }

        public bool IsDirectory => _entry.IsDirectory;

        public string ModifiedText { get; }

        public string SizeText { get; }

        public string KindText { get; }

        public string Glyph
        {
            get => _glyph;
            private set
            {
                _glyph = value;
                OnPropertyChanged();
            }
        }

        public ImageSource? Icon => _icon;

        /// <summary>后台提取完成后回 UI 线程贴图：先撤字形再通知 Icon，避免两者闪现叠加。</summary>
        public void SetIcon(ImageSource icon)
        {
            Glyph = string.Empty;
            _icon = icon;
            OnPropertyChanged(nameof(Icon));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>字节数 → B/KB/MB/GB（一位小数，向上取整不必要；目录显示为 "—"）。</summary>
        private static string FormatSize(long bytes)
        {
            const double kb = 1024;
            if (bytes < kb)
            {
                return $"{bytes} B";
            }

            if (bytes < kb * kb)
            {
                return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            if (bytes < kb * kb * kb)
            {
                return (bytes / (kb * kb)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            return (bytes / (kb * kb * kb)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
    }

    /// <summary>后台读取结果：成功（已排序 + 是否截断 + 可用空间）或失败原因；可用空间与枚举同批算好。</summary>
    private sealed record LoadResult(
        IReadOnlyList<FinderEntry> Entries, bool Capped, string? Error, string? FreeSpaceText)
    {
        public static LoadResult Success(IReadOnlyList<FinderEntry> entries, bool capped, string? freeSpaceText)
            => new(entries, capped, null, freeSpaceText);

        public static LoadResult Failed(string error, string? freeSpaceText)
            => new(Array.Empty<FinderEntry>(), false, error, freeSpaceText);
    }

    /// <summary>侧栏一项：显示名 + 导航路径。</summary>
    private sealed record SidebarItem(string Label, string Path);
}
