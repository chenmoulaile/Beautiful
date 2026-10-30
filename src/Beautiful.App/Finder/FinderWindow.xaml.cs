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
/// 访达窗口：macOS Big Sur 风格的三段式窗口——工具栏 / 侧栏 + 内容区 / 状态栏。
/// <para>
/// 窗口形态与 <see cref="Settings.SettingsWindow"/> 一致：非分层窗口 + WindowChrome + DWM 圆角，
/// 保留 ClearType 与 DWM 阴影（AllowsTransparency 保持 false，见 XAML）。CaptionHeight=0，
/// 顶部条带由 <see cref="TitleBar_MouseLeftButtonDown"/> 自绘拖动。
/// </para>
/// <para>
/// 三种视图（Phase 6-b）：列表（<see cref="FileList"/>）/ 分栏（<see cref="ColumnScroll"/>，
/// 见 <c>FinderWindow.Columns.cs</c>）/ 图标（<see cref="IconList"/>，见 <c>FinderWindow.Icons.cs</c>）。
/// 视图模式、排序列、图标尺寸、隐藏项开关按文件夹持久化到 <c>config/finder-view-options.json</c>，
/// 归一化与容量上限由 Core 的 <see cref="FinderViewOptionRules"/> 负责。
/// </para>
/// <para>
/// 导航/键位/排序/列栈/网格全部复用 <c>Beautiful.Core.Finder</c> 的纯函数，UI 只负责把状态映射到控件：
/// <see cref="NavigationHistory"/> 管后退/前进，<see cref="FinderKeyMap"/> 认 Ctrl+[ / ] / W / Escape，
/// <see cref="FinderSorting"/> 给列表定序，<see cref="FinderColumnState"/> 管分栏列栈，
/// <see cref="FinderIconGrid"/> 管图标网格与框选命中。目录读取在后台线程完成，
/// 最多取 <see cref="MaxEntries"/> 项；访问失败只清空列表并写状态栏/日志，绝不让窗口崩溃。
/// </para>
/// </summary>
public partial class FinderWindow : Window
{
    /// <summary>单次目录读取的条目上限（目录过大时保证首屏与滚动仍可用）。</summary>
    private const int MaxEntries = 1500;

    /// <summary>后台图标提取的行数上限：超出部分保留字形回退，避免 1500 项一次性抓 shell 图标。</summary>
    private const int MaxIconRows = 300;

    /// <summary>图标像素尺寸上限：Shell 图标资源最大帧通常就是 256。</summary>
    private const int MaxIconPixels = 256;

    private static FinderWindow? _current;

    private readonly NavigationHistory _history = new();
    private readonly DispatcherTimer _statusHintTimer;
    private readonly FinderViewOptionsStore _viewStore = new();

    /// <summary>已载入的整表视图选项（路径 → 选项）；改动后立即落盘。</summary>
    private IReadOnlyDictionary<string, FinderViewOptions> _viewMap =
        new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);

    private List<FinderRow> _allRows = [];
    private List<FinderRow> _visibleRows = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _iconCts;
    private string _currentPath = string.Empty;
    private string? _loadError;
    private string? _freeSpaceText;
    private bool _capped;
    private bool _suppressSidebar;
    private bool _isLoading;
    private int _loadGeneration;

    // 当前文件夹的视图选项（每次导航从 _viewMap 取，改动后写回）
    private FinderViewMode _viewMode = FinderViewMode.List;
    private FinderSortKey _sortKey = FinderSortKey.Name;
    private bool _descending;
    private bool _showHidden;
    private double _iconSize = 64;
    private int _iconPixelSize = 32;

    /// <summary>诊断用（<c>--select &lt;name&gt;</c>）：载入完成后自动选中该条目，截图/验收时省掉鼠标。</summary>
    private string? _pendingSelection;

    /// <summary>是否已经应用过一次视图选项（首次不继承，之后未记录的目录继承当前视图）。</summary>
    private bool _viewApplied;

    /// <summary>
    /// 单例入口（--finder / Dock 入口共用）。关闭后自动重置，可再次打开。
    /// </summary>
    /// <param name="initialPath">初始目录；null 用用户主目录。</param>
    /// <param name="view">
    /// 强制初始视图（<c>--view</c> 截图/验收入口）。null = 沿用该文件夹上次记住的视图；
    /// 传值时只在内存里覆盖，**不写回**持久化选项。
    /// </param>
    public static void ShowSingleton(string? initialPath = null, FinderViewMode? view = null,
        string? select = null)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Activate();
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                _current.NavigateTo(initialPath!);
            }

            if (view is not null)
            {
                _current.SetViewMode(view.Value, persist: false);
            }

            if (!string.IsNullOrWhiteSpace(select))
            {
                _current.SelectByName(select!);
            }

            return;
        }

        var window = new FinderWindow(initialPath, view, select);
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
        : this(null, null)
    {
    }

    public FinderWindow(string? initialPath)
        : this(initialPath, null)
    {
    }

    public FinderWindow(string? initialPath, FinderViewMode? view, string? select = null)
    {
        InitializeComponent();

        _statusHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusHintTimer.Tick += (_, _) =>
        {
            _statusHintTimer.Stop();
            UpdateStatus();
        };

        _viewMap = _viewStore.Load();

        var path = string.IsNullOrWhiteSpace(initialPath) ? DefaultDirectory() : initialPath!;
        _currentPath = path;
        _history.Visit(path);
        ApplyViewOptionsFor(path);
        if (view is not null)
        {
            _viewMode = view.Value;   // 仅内存覆盖：不写回该文件夹的持久化选项
        }

        _pendingSelection = string.IsNullOrWhiteSpace(select) ? null : select;

        PopulateSidebar();
        SyncSidebarSelection();
        ApplyPath(path);
        UpdateNavButtons();
        SetViewMode(_viewMode, persist: false);

        Loaded += (_, _) => UpdateNameColumnWidth();
        SizeChanged += (_, _) =>
        {
            UpdateNameColumnWidth();
            RefreshColumnWidths();
        };
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

    /// <summary>
    /// 诊断入口（<c>--shot</c>）：把访达窗口带到前台并返回其 HWND；没有窗口返回 <see cref="IntPtr.Zero"/>。
    /// 只为"截图证据只拍目标窗口"服务（配合 PrintWindow 可与前台无关），不参与产品逻辑。
    /// </summary>
    public static IntPtr GetDiagnosticsHandle()
    {
        if (_current is not { IsLoaded: true } window)
        {
            return IntPtr.Zero;
        }

        try
        {
            window.Activate();
            return new WindowInteropHelper(window).Handle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

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

    // ---------- 视图选项 ----------

    /// <summary>
    /// 从整表里取出本文件夹的选项并应用到当前状态（导航时调用）。
    /// <para>
    /// 未记录过的文件夹**继承当前视图**（模式/排序/图标尺寸/隐藏开关），而不是跳回出厂默认：
    /// macOS 上新开一个从未浏览过的文件夹不会突然从分栏变回列表，用户的心智是"我选过的显示方式继续用"。
    /// 继承只在内存里生效，不写盘——否则一次浏览就会给每个目录留一条默认记录。
    /// </para>
    /// </summary>
    private void ApplyViewOptionsFor(string path)
    {
        var key = FinderViewOptionRules.KeyOf(path);
        var stored = key.Length > 0 && _viewMap.ContainsKey(key);
        var options = FinderViewOptionRules.For(_viewMap, path);
        if (!stored && _viewApplied)
        {
            options = options with
            {
                Mode = _viewMode,
                SortKey = _sortKey,
                Descending = _descending,
                ShowHidden = _showHidden,
                IconSize = _iconSize
            };
        }

        _viewApplied = true;
        _viewMode = options.Mode;
        _sortKey = options.SortKey;
        _descending = options.Descending;
        _showHidden = options.ShowHidden;
        _iconSize = options.IconSize;
        _iconPixelSize = IconPixelsFor(_iconSize);
        UpdateSortHeaders();
    }

    /// <summary>当前状态 → 选项记录，写回整表并落盘（每次用户改动视图都调用）。</summary>
    private void PersistViewOptions()
    {
        var options = new FinderViewOptions(_viewMode, _sortKey, _descending, _showHidden, _iconSize);
        _viewMap = FinderViewOptionRules.With(_viewMap, _currentPath, options);
        _viewStore.Save(_viewMap);
    }

    /// <summary>
    /// 图标提取的物理像素：按图标 DIP 尺寸 × DPI 缩放，上限 256（Shell 资源里没有更大的帧）。
    /// 列表视图只要 16 DIP，但同一次提取供两处复用，因此下限 32 已经足够清晰。
    /// </summary>
    private int IconPixelsFor(double iconSize)
    {
        var scale = 1.0;
        try
        {
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未完成初始化（构造期）：按 100% 处理，Loaded 后会随视图切换重算。
        }

        var pixels = (int)Math.Ceiling(Math.Max(iconSize, 32) * Math.Max(1.0, scale));
        return Math.Clamp(pixels, 32, MaxIconPixels);
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
        // 分栏视图里"后退"优先退一列（macOS 列视图语义），退无可退才走导航历史。
        if (_viewMode == FinderViewMode.Columns && StepBackColumn())
        {
            return;
        }

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

    /// <summary>上一级（⌘↑ / Ctrl+↑）：根目录上再按无操作。</summary>
    private void GoUp()
    {
        var parent = FinderPaths.Parent(_currentPath);
        if (parent is null)
        {
            ShowStatusHint("已经在最上层");
            return;
        }

        NavigateTo(parent);
    }

    /// <summary>
    /// 换到另一个目录：先切换标题/历史按钮/视图选项，再进入载入态（清空旧行、关闭行交互、
    /// 状态栏"正在载入…"），最后清掉搜索词（否则新目录会被旧查询静默过滤）。
    /// F5 刷新同一目录不走这里，搜索词与视图选项都保留。
    /// </summary>
    private void ShowDirectory(string path)
    {
        _currentPath = path;
        ApplyPath(path);
        SyncSidebarSelection();
        UpdateNavButtons();
        ApplyViewOptionsFor(path);
        SyncViewChrome();
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
        _visibleRows = [];
        FileList.ItemsSource = Array.Empty<FinderRow>();
        IconList.ItemsSource = Array.Empty<FinderRow>();
        FileList.IsEnabled = false;   // 载入完成前不允许双击/键盘打开旧行
        _statusHintTimer.Stop();
        StatusText.Text = "正在载入…";
        RefreshColumns();

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
        var sortKey = _sortKey;
        var descending = _descending;

        try
        {
            var result = await Task.Run(() => ReadDirectory(path, sortKey, descending, token), token);
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

    /// <summary>
    /// 后台枚举目录（静态方法：不碰实例状态，便于将来下沉到 Core/单测）。
    /// 隐藏项按属性标记进 <see cref="FinderEntry.IsHidden"/>，是否显示由视图过滤决定（⇧⌘.）。
    /// </summary>
    private static LoadResult ReadDirectory(string path, FinderSortKey sortKey, bool descending,
        CancellationToken token)
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

                    var attributes = info.Attributes;
                    var hidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    var kind = isDirectory ? "文件夹" : KindOf(info.Name);
                    entries.Add(new FinderEntry(info.Name, isDirectory, size, info.LastWriteTimeUtc, kind, hidden));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 枚举途中被删除/改权限的单个条目：跳过，不让一项毁掉整份列表。
                }
            }

            // 文件夹恒在前 + 选中列的排序由 Core 固化（file2 < file10）；UI 不再写第二套排序。
            var sorted = FinderSorting.Sort(entries, sortKey, descending);
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
        foreach (var row in _allRows)
        {
            row.SetIconBoxSize(_iconSize);
        }

        RememberDirectory(path, _allRows);
        FileList.IsEnabled = true;
        ApplyFilter();
        UpdateStatus();
        StartIconLoad(_allRows, generation);

        if (_pendingSelection is { } pending)
        {
            _pendingSelection = null;
            SelectByName(pending);
        }
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
        _visibleRows = [];
        FileList.ItemsSource = Array.Empty<FinderRow>();
        IconList.ItemsSource = Array.Empty<FinderRow>();
        FileList.IsEnabled = true;
        RefreshColumns();
        UpdateStatus();
    }

    // ---------- 图标 ----------

    /// <summary>
    /// 载入结果应用后，在后台顺序提取前 <see cref="MaxIconRows"/> 行的图标，每拿到一个就回 UI 线程
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
        _ = LoadIconsAsync(rows, count, generation, _iconPixelSize, cts);
    }

    private async Task LoadIconsAsync(IReadOnlyList<FinderRow> rows, int count, int generation,
        int pixelSize, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                if (row.HasIcon)
                {
                    continue;   // 列视图缓存里的行已经带图标：不重复抓 shell
                }

                var icon = await Task.Run(() => IconExtractor.GetIcon(row.FullPath, pixelSize), token);
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

    /// <summary>
    /// 名称/隐藏项过滤（当前目录已载入的行）。结果同时喂给列表与图标视图；
    /// 分栏视图的每一列有各自的过滤（见 <c>FinderWindow.Columns.cs</c>）。
    /// </summary>
    private void ApplyFilter()
    {
        if (_isLoading)
        {
            _visibleRows = [];
            FileList.ItemsSource = Array.Empty<FinderRow>();
            IconList.ItemsSource = Array.Empty<FinderRow>();
            return;
        }

        var query = SearchBox.Text.Trim();
        IEnumerable<FinderRow> source = _allRows;
        if (!_showHidden)
        {
            source = source.Where(row => !row.IsHidden);
        }

        if (query.Length > 0)
        {
            source = source.Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _visibleRows = source.ToList();
        FileList.ItemsSource = _visibleRows;
        IconList.ItemsSource = _visibleRows;

        // 分栏视图的每一列也吃同一套过滤条件（搜索词/隐藏项），载入完成后必须重画一次，
        // 否则列里会一直停在 StartLoad 时清空的空集合。
        if (_viewMode == FinderViewMode.Columns)
        {
            RefreshColumns();
        }
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
        var visible = _visibleRows.Count;
        var text = _capped
            ? $"仅显示前 {total} 个项目（目录内容过多）"
            : $"{total} 个项目";

        // 过滤原因分开写：隐藏项是"开关"，搜索是"临时筛选"，混成一个数字用户分不清少了什么。
        if (!_showHidden)
        {
            var hidden = _allRows.Count(row => row.IsHidden);
            if (hidden > 0)
            {
                text += $"，已隐藏 {hidden} 个";
            }
        }

        if (SearchBox is not null && SearchBox.Text.Trim().Length > 0)
        {
            text += $"，筛选出 {visible} 个";
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
        var row = SelectedRow();
        if (row is not null)
        {
            OpenRow(row);
        }
    }

    /// <summary>当前视图下"选中的第一行"（三种视图各有一套选择，统一从这里取）。</summary>
    private FinderRow? SelectedRow() => _viewMode switch
    {
        FinderViewMode.Icons => IconList.SelectedItem as FinderRow,
        FinderViewMode.Columns => ColumnSelectedRow(),
        _ => FileList.SelectedItem as FinderRow
    };

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

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // 视图切换：⌘1/2/3（Ctrl 充当 ⌘）
        if (ctrl && !shift && e.Key is Key.D1 or Key.NumPad1)
        {
            e.Handled = true;
            SetViewMode(FinderViewMode.List);
            return;
        }

        if (ctrl && !shift && e.Key is Key.D2 or Key.NumPad2)
        {
            e.Handled = true;
            SetViewMode(FinderViewMode.Columns);
            return;
        }

        if (ctrl && !shift && e.Key is Key.D3 or Key.NumPad3)
        {
            e.Handled = true;
            SetViewMode(FinderViewMode.Icons);
            return;
        }

        // ⇧⌘. 显示/隐藏隐藏项（macOS 语义）
        if (ctrl && shift && (e.Key == Key.OemPeriod || e.Key == Key.Decimal))
        {
            e.Handled = true;
            ToggleHiddenFiles();
            return;
        }

        // ⇧⌘[ / ] 图标尺寸（macOS 语义，只在图标视图有意义）
        if (ctrl && shift && e.Key == Key.OemOpenBrackets)
        {
            e.Handled = true;
            StepIconSize(-1);
            return;
        }

        if (ctrl && shift && e.Key == Key.OemCloseBrackets)
        {
            e.Handled = true;
            StepIconSize(1);
            return;
        }

        // ⌘I 显示简介：有选中项看选中项，没有就看当前文件夹（搜索框里不抢 Ctrl+I）
        if (ctrl && !shift && e.Key == Key.I && !SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            FinderInfoWindow.ShowFor(SelectedRow()?.FullPath ?? _currentPath);
            return;
        }

        // ⌘↑ 上一级
        if (ctrl && e.Key == Key.Up)
        {
            e.Handled = true;
            GoUp();
            return;
        }

        // 空格 = Quick Look：搜索框里打字时空格必须原样输入；按钮/文本框上的空格保留原生语义。
        if (e.Key == Key.Space && !SearchBox.IsKeyboardFocusWithin && !_isLoading
            && e.OriginalSource is not TextBox && Keyboard.FocusedElement is not Button)
        {
            if (QuickLookFromSelection())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            RefreshCurrentDirectory();   // 同一目录刷新：保留搜索词与视图选项
            return;
        }

        // 分栏视图：←/→ 在列间移动焦点（选中文件夹时 → 直接开新列）
        if (_viewMode == FinderViewMode.Columns && e.Key is Key.Left or Key.Right
            && e.OriginalSource is not TextBox)
        {
            if (HandleColumnArrow(e.Key == Key.Right))
            {
                e.Handled = true;
                return;
            }
        }

        // 图标视图：方向键按网格移动（左右不换行），Ctrl+A 全选
        if (_viewMode == FinderViewMode.Icons && e.OriginalSource is not TextBox)
        {
            if (HandleIconArrow(e.Key))
            {
                e.Handled = true;
                return;
            }
        }

        // Enter 只在内容区获得焦点时打开选中项：搜索框/按钮里的 Enter 保持原生语义。
        if (e.Key == Key.Enter && e.OriginalSource is not TextBox && Keyboard.FocusedElement is not Button
            && (FileList.IsKeyboardFocusWithin || IconList.IsKeyboardFocusWithin || IsColumnFocusWithin()))
        {
            e.Handled = true;
            OpenSelected();
            return;
        }

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

    /// <summary>F5：同一目录重新枚举（保留搜索词/视图选项），并让图标与列缓存跟着刷新。</summary>
    private void RefreshCurrentDirectory()
    {
        ForgetDirectory(_currentPath);
        StartLoad(_currentPath);
    }

    /// <summary>⇧⌘.：切换隐藏项显示并记住到本文件夹。</summary>
    private void ToggleHiddenFiles()
    {
        _showHidden = !_showHidden;
        PersistViewOptions();
        ApplyFilter();
        RefreshColumns();
        UpdateStatus();
        ShowStatusHint(_showHidden ? "已显示隐藏项目" : "已隐藏隐藏项目");
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

    private void List_Click(object sender, RoutedEventArgs e) => SetViewMode(FinderViewMode.List);

    private void Columns_Click(object sender, RoutedEventArgs e) => SetViewMode(FinderViewMode.Columns);

    private void Grid_Click(object sender, RoutedEventArgs e) => SetViewMode(FinderViewMode.Icons);

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
    /// 内容区的一行：展示字段来自 Core 的 <see cref="FinderEntry"/>。图标不再由绑定 getter 同步提取——
    /// 载入结果应用后由窗口在后台批量提取，拿到一个就在 UI 线程调用 <see cref="SetIcon"/>；
    /// 在此之前行显示字形回退，滚动进入视口也绝不触发磁盘/Shell 调用。
    /// 提取尺寸按图标视图的 DIP 尺寸 × DPI 计算（上限 256），列表视图共用同一张位图。
    /// </summary>
    private sealed class FinderRow : INotifyPropertyChanged
    {
        private readonly FinderEntry _entry;
        private string _glyph;
        private ImageSource? _icon;
        private double _iconBoxSize = 64;

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

        public bool IsHidden => _entry.IsHidden;

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

        /// <summary>图标视图里图标方框的边长（DIP）；列表视图不绑定它。</summary>
        public double IconBoxSize => _iconBoxSize;

        /// <summary>是否已经有位图（列视图缓存复用时避免重复抓 shell）。</summary>
        public bool HasIcon => _icon is not null;

        /// <summary>后台提取完成后回 UI 线程贴图：先撤字形再通知 Icon，避免两者闪现叠加。</summary>
        public void SetIcon(ImageSource icon)
        {
            Glyph = string.Empty;
            _icon = icon;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }

        /// <summary>图标尺寸变化（滑杆/⇧⌘[/]）：只改布局框大小，不重新提取位图。</summary>
        public void SetIconBoxSize(double size)
        {
            if (Math.Abs(_iconBoxSize - size) < 0.5)
            {
                return;
            }

            _iconBoxSize = size;
            OnPropertyChanged(nameof(IconBoxSize));
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
