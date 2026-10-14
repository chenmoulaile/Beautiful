using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 一个被跟踪的顶层窗口。首版策略：每个顶层窗口一个 item（不按进程合并）。
/// 由 <see cref="WindowTracker"/> 在 UI 线程上创建与更新；Title/IsMinimized 带属性变更通知，
/// 供 Dock 直接绑定刷新（Icon 为 null 时视图显示占位字形）。
/// </summary>
public sealed class WindowItem : INotifyPropertyChanged
{
    private string _title;
    private bool _isMinimized;

    internal WindowItem(IntPtr handle, string title, string exePath, BitmapSource? icon, bool isMinimized)
    {
        Handle = handle;
        _title = title;
        ExePath = exePath;
        Icon = icon;
        _isMinimized = isMinimized;
    }

    public IntPtr Handle { get; }
    public string ExePath { get; }
    public BitmapSource? Icon { get; }

    public string Title { get => _title; internal set => Set(ref _title, value); }
    public bool IsMinimized { get => _isMinimized; internal set => Set(ref _isMinimized, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 顶层窗口枚举与实时跟踪。
/// <list type="bullet">
/// <item><description>实时路径：内部自建的隐藏宿主窗口（ownerless、从不显示，不依赖 DockWindow 生命周期）上
/// RegisterShellHookWindow；SHELLHOOK 消息经 HwndSource.AddHook 在 UI 线程回调。</description></item>
/// <item><description>兜底路径：2s DispatcherTimer 全量 EnumWindows 对账，补挂漏报窗口（如创建初期尚无标题的）
/// 并移除失效项。</description></item>
/// </list>
/// 线程模型：必须在 WPF UI 线程上构造并调用 <see cref="Start"/> / <see cref="ActivateOrMinimize"/> /
/// <see cref="Dispose"/>。ShellHook 消息由宿主窗口的消息循环在 UI 线程投递，集合变更全部发生在 UI 线程，
/// 因此集合与图标缓存无需加锁。
/// </summary>
public sealed class WindowTracker : IDisposable
{
    // 可见性过滤规则已抽到 WindowEligibility（Dock 与切换器共用同一实现，避免两份规则漂移）
    private const long HshellRudeAppActivated = 0x8004;    // HSHELL_ACTIVATED(4) | HSHELL_HIGHBIT
    private const int SwMinimize = 6, SwRestore = 9;

    private readonly Dispatcher _dispatcher;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly ushort _shellHookMsg;
    private readonly ObservableCollection<WindowItem> _windows = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase); // UI 线程专用，无需加锁

    private HwndSource? _hookSource;
    private IntPtr _hookHwnd;
    private DispatcherTimer? _timer;
    private bool _started;
    private bool _disposed;

    public WindowTracker()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("WindowTracker 必须在 WPF UI 线程上构造。");
        _shellHookMsg = NativeMethods.RegisterWindowMessage("SHELLHOOK");
    }

    /// <summary>被跟踪的窗口集合。新窗口按枚举（Z）序插入，不随 Z 序实时重排，避免 Dock UI 抖动。</summary>
    public ObservableCollection<WindowItem> Windows => _windows;

    /// <summary>
    /// 创建专用隐藏宿主窗口并注册 ShellHook，随后做一次初始 EnumWindows 扫描并启动 2s 兜底对账定时器。
    /// 重复调用为 no-op；Dispose 之后调用抛 ObjectDisposedException。
    /// （brief 原签名 Start(Window hookHost)，按裁定改为跟踪器自建宿主窗口，避免与 DockWindow 生命周期耦合。）
    /// </summary>
    public void Start()
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(WindowTracker));
        if (_started) return;
        _started = true;

        // 专用宿主：WS_OVERLAPPED（未设 WS_VISIBLE → 从不显示）、无父无主（ownerless）的顶层窗口
        var parameters = new HwndSourceParameters("Beautiful_ShellHookHost")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = IntPtr.Zero
        };
        _hookSource = new HwndSource(parameters);
        _hookHwnd = _hookSource.Handle;
        _hookSource.AddHook(WndProc);
        // 注册失败不致命：仅损失增删实时性，2s 定时器兜底对账仍可用
        NativeMethods.RegisterShellHookWindow(_hookHwnd);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Reconcile();
        _timer.Start();

        Reconcile(); // 初始扫描
    }

    /// <summary>前台即该窗口 → 最小化；已最小化 → 还原并置前；否则置前。</summary>
    public void ActivateOrMinimize(WindowItem item)
    {
        EnsureUiThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var h = item.Handle;
        if (!NativeMethods.IsWindow(h)) return;
        if (NativeMethods.GetForegroundWindow() == h)
        {
            NativeMethods.ShowWindow(h, SwMinimize);
        }
        else if (NativeMethods.IsIconic(h))
        {
            NativeMethods.ShowWindow(h, SwRestore);
            NativeMethods.SetForegroundWindow(h);
        }
        else
        {
            NativeMethods.SetForegroundWindow(h);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 允许从任意线程安全释放：转回 UI 线程执行注销与销毁
        if (_dispatcher.CheckAccess()) Teardown();
        else _dispatcher.Invoke(Teardown);
    }

    // ---------- ShellHook ----------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shellHookMsg == 0 || msg != _shellHookMsg) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt64())
        {
            case NativeMethods.HshellWindowCreated:
                AddWindow(lParam);
                break;
            case NativeMethods.HshellWindowDestroyed:
                RemoveWindow(lParam);
                break;
            case NativeMethods.HshellActivated:
            case HshellRudeAppActivated: // 全屏切换/最大化激活也以 4|HSHELL_HIGHBIT 形式送达
            case NativeMethods.HshellGetMinRect:
                UpdateStates();
                break;
            // HSHELL_APPCOMMAND(12) 及其余码：忽略
        }
        return IntPtr.Zero;
    }

    private void AddWindow(IntPtr hwnd)
    {
        if (!WindowEligibility.IsEligibleWindow(hwnd, _ownPid)) return;
        if (IndexOf(hwnd) >= 0) return;
        var item = CreateItem(hwnd);
        if (item is null) return;
        _windows.Add(item);
    }

    private void RemoveWindow(IntPtr hwnd)
    {
        var idx = IndexOf(hwnd);
        if (idx >= 0) _windows.RemoveAt(idx);
    }

    /// <summary>hook 驱动的轻量刷新：更新 IsMinimized 并移除已销毁句柄。</summary>
    private void UpdateStates()
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var item = _windows[i];
            if (!NativeMethods.IsWindow(item.Handle))
            {
                _windows.RemoveAt(i);
                continue;
            }
            item.IsMinimized = NativeMethods.IsIconic(item.Handle);
        }
    }

    // ---------- 兜底对账 ----------

    /// <summary>全量 EnumWindows 重扫：移除失效/不再符合条件的项，按 Z 序补入新窗口，并刷新状态与标题。</summary>
    private void Reconcile()
    {
        if (_disposed) return;

        var eligible = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (WindowEligibility.IsEligibleWindow(h, _ownPid)) eligible.Add(h);
            return true;
        }, IntPtr.Zero);
        var eligibleSet = new HashSet<IntPtr>(eligible);

        for (int i = _windows.Count - 1; i >= 0; i--)
            if (!eligibleSet.Contains(_windows[i].Handle))
                _windows.RemoveAt(i);

        int anchor = -1; // 上一命中项在集合中的下标，新项插到其后（近似 Z 序，不动既有项）
        foreach (var hwnd in eligible)
        {
            var idx = IndexOf(hwnd);
            if (idx >= 0)
            {
                anchor = idx;
                RefreshItem(_windows[idx]);
            }
            else
            {
                var item = CreateItem(hwnd);
                if (item is null) continue;
                _windows.Insert(anchor + 1, item);
                anchor++;
            }
        }
    }

    private static void RefreshItem(WindowItem item)
    {
        item.IsMinimized = NativeMethods.IsIconic(item.Handle);
        // 标题会变（浏览器切标签等）：仅用非空标题覆盖，避免瞬时空标题闪断
        var title = WindowEligibility.GetWindowTitle(item.Handle);
        if (title.Length > 0) item.Title = title;
    }

    // ---------- 构建 / 过滤 ----------

    private WindowItem? CreateItem(IntPtr hwnd)
    {
        var title = WindowEligibility.GetWindowTitle(hwnd);
        if (title.Length == 0) return null; // 创建初期标题尚空的窗口：跳过，等下一次对账补入
        var exePath = WindowEligibility.GetWindowExePath(hwnd);
        return new WindowItem(hwnd, title, exePath, GetIconCached(exePath), NativeMethods.IsIconic(hwnd));
    }

    private BitmapSource? GetIconCached(string exePath)
    {
        if (exePath.Length == 0) return null;
        if (_iconCache.TryGetValue(exePath, out var cached)) return cached;
        var icon = IconExtractor.GetIcon(exePath); // 失败为 null → 视图回退占位字形
        _iconCache[exePath] = icon;
        return icon;
    }

    private int IndexOf(IntPtr hwnd)
    {
        for (int i = 0; i < _windows.Count; i++)
            if (_windows[i].Handle == hwnd) return i;
        return -1;
    }

    // ---------- 生命周期 ----------

    private void Teardown()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
        if (_hookHwnd != IntPtr.Zero)
        {
            NativeMethods.DeregisterShellHookWindow(_hookHwnd); // 必须在销毁 hwnd 之前注销
            _hookHwnd = IntPtr.Zero;
        }
        if (_hookSource is not null)
        {
            _hookSource.RemoveHook(WndProc);
            _hookSource.Dispose(); // 销毁宿主窗口
            _hookSource = null;
        }
        _iconCache.Clear();
        _windows.Clear();
    }

    private void EnsureUiThread()
    {
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException(
                "WindowTracker 仅允许在 UI 线程使用（构造/Start/ActivateOrMinimize/Dispose）：" +
                "ShellHook 回调与集合变更均绑定 UI 线程。");
    }
}
