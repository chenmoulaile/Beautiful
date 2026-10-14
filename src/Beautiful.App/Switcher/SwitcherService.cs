using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Switcher;

namespace Beautiful.App.Switcher;

/// <summary>
/// 切换器服务（Task 11）：维护"可切换窗口"的 MRU 列表、用 Task 10 的纯函数分组、并把选中项真正切到前台。
///
/// 【数据流】
/// <list type="number">
/// <item><description><see cref="ForegroundWatcher"/>（进程内共享的前台钩子）+ 初始 <c>EnumWindows</c>
/// Z 序快照 → <see cref="_tracked"/>（MRU，首元素=最近使用）。</description></item>
/// <item><description>2s 兜底对账定时器：补入漏报窗口、剔除已销毁/不再合格的窗口、刷新标题与最小化态
/// （与 Dock 的 WindowTracker 同一策略，两者互不依赖）。</description></item>
/// <item><description><see cref="AltTabHook.OpenRequested"/> → 由 <see cref="_tracked"/> 生成
/// <see cref="WindowEntry"/> 序列（已是 Z/MRU 序）→ <see cref="WindowListMerger.GroupByApp"/> 分组
/// → 图标**只查缓存**（未命中给占位符，绝不在按键路径提取——磁盘 IO 全部收敛到 2s 对账预热，
/// 评审发现 I4）。**按键序列里不出现 EnumWindows 与图标提取**。</description></item>
/// <item><description>Tab/Shift+Tab/方向键 → <see cref="WindowListMerger.NextIndex"/> 步进；
/// 松开触发键 → <see cref="CommitSelected"/>；Esc → <see cref="Cancel"/>。</description></item>
/// </list>
///
/// 【与 Task 12 面板的分工】状态机（选中哪个 App/窗口、步进、提交、取消）在本服务里，面板只负责渲染
/// <see cref="Apps"/>/<see cref="SelectedAppIndex"/> 并把鼠标事件转成 <see cref="StepBy"/>/
/// <see cref="CommitSelected"/>/<see cref="Cancel"/>。这样没有面板时 Alt+Tab 也已可用（本任务的实测依赖它），
/// 面板也能直接复用同一套逻辑而不必重写。本服务在构造时即订阅钩子事件，且面板稍后才订阅同一事件源——
/// 委托按订阅顺序执行，故 <see cref="Opened"/> 触发时 <see cref="Apps"/> 一定已经重建完毕。
///
/// 【线程模型】全部公开成员仅限 WPF UI 线程（钩子回调投递在该线程消息泵上，排空回调亦在该线程）。
/// </summary>
public sealed class SwitcherService : IDisposable
{
    private const int SwRestore = 9; // SW_RESTORE

    private readonly Dispatcher _ui;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly NativeMethods.EnumWindowsProc _collectProc; // 缓存：对账不重复分配委托

    // EnumWindows 回调复用同一对实例字段（每轮 Reconcile 先 Clear）：避免每次对账都分配集合。
    private readonly List<IntPtr> _pendingList = new();
    private readonly HashSet<IntPtr> _pendingSet = new();

    private readonly List<TrackedWindow> _tracked = new();        // MRU，首=最近
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SwitcherApp> _apps = new();

    private IDisposable? _foregroundSubscription;
    private DispatcherTimer? _reconcileTimer;
    private bool _started;
    private bool _disposed;

    /// <summary>Alt+Tab 事件源（面板直接订阅其 Next/Previous 等事件做动画；提交动作仍回调本服务）。</summary>
    public AltTabHook Hook { get; }

    /// <summary>当前分组快照（仅在 <see cref="Opened"/> 之后保证新鲜；面板打开期间不重排，避免高亮跳动）。</summary>
    public IReadOnlyList<SwitcherApp> Apps => _apps;

    /// <summary>选中的 App 下标，-1 = 未打开/未选中。</summary>
    public int SelectedAppIndex { get; private set; } = -1;

    /// <summary>选中 App 内的窗口下标（macOS 二次展开语义，由面板调用 <see cref="SelectWindow"/> 驱动）。</summary>
    public int SelectedWindowIndex { get; private set; }

    /// <summary>面板是否处于打开态（本服务在 Open/Cancel/Commit 时维护）。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>快照已重建、选择已初始化：面板据此显示。</summary>
    public event Action? Opened;
    /// <summary>高亮位置变化。</summary>
    public event Action? SelectionChanged;
    /// <summary>会话结束（提交或取消）：面板据此关闭。</summary>
    public event Action? Closed;

    private sealed class TrackedWindow
    {
        internal TrackedWindow(IntPtr handle, string appId, string appName, string title, bool minimized)
        {
            Handle = handle; AppId = appId; AppName = appName; Title = title; IsMinimized = minimized;
        }
        public IntPtr Handle { get; }
        public string AppId { get; set; }
        public string AppName { get; set; }
        public string Title { get; set; }
        public bool IsMinimized { get; set; }
    }

    public SwitcherService() : this(new AltTabHook()) { }

    public SwitcherService(AltTabHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        Hook = hook;
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_ui.CheckAccess())
            throw new InvalidOperationException("SwitcherService 必须在 WPF UI 线程上构造。");
        _collectProc = CollectEligible;

        // 顺序很重要：本服务先于面板订阅钩子事件 → OpenRequested 时快照已重建完再轮到面板。
        hook.OpenRequested += OnOpenRequested;
        hook.NextRequested += () => StepBy(1);
        hook.PreviousRequested += () => StepBy(-1);
        hook.CommitRequested += CommitSelected;
        hook.CancelRequested += Cancel;
        hook.UnhookRequested = StopHook;
    }

    // ---------- 生命周期 ----------

    /// <summary>
    /// 启动 MRU 跟踪并按 <paramref name="enableHook"/> 决定是否安装键盘钩子（幂等）。
    /// 跟踪本身始终启动：面板与"窗口"菜单都可能需要列表，而钩子只有接管时才吞键。
    /// </summary>
    public void Start(bool enableHook)
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(SwitcherService));
        if (!_started)
        {
            _started = true;
            _foregroundSubscription = ForegroundWatcher.Subscribe(OnForegroundChanged);
            Reconcile(); // 初始 EnumWindows Z 序快照
            _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _reconcileTimer.Tick += (_, _) => Reconcile();
            _reconcileTimer.Start();
        }
        SetHookEnabled(enableHook);
    }

    /// <summary>装/摘 Alt+Tab 钩子（设置项开关与应急停用共用）。摘钩后原生 Alt+Tab 立即恢复。</summary>
    public void SetHookEnabled(bool enabled)
    {
        EnsureUiThread();
        if (_disposed) return;
        Hook.Enabled = enabled;
        if (enabled)
        {
            if (!Hook.IsStarted)
            {
                Hook.SetTriggerHotkey(SettingsService.Current.SwitcherHotkey);
                Hook.FullscreenPassthrough = SettingsService.Current.FullscreenPassthrough;
                if (!Hook.Start())
                    Logger.Warn("切换器：键盘钩子安装失败，Alt+Tab 保持系统原生行为");
                else
                    Logger.Info($"切换器：已接管 Alt+Tab（热键={SettingsService.Current.SwitcherHotkey}, " +
                                $"全屏直通={SettingsService.Current.FullscreenPassthrough}）");
            }
        }
        else if (Hook.IsStarted)
        {
            StopHook();
        }
    }

    /// <summary>卸载钩子（保留 MRU 跟踪）。</summary>
    public void StopHook()
    {
        EnsureUiThread();
        if (!Hook.IsStarted) return;
        Hook.Stop();
        Logger.Info("切换器：已卸载 Alt+Tab 钩子，系统原生切换恢复");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ui.CheckAccess()) Teardown();
        else { try { _ui.Invoke(Teardown); } catch { /* 关停竞态：句柄随进程销毁 */ } }
    }

    private void Teardown()
    {
        _reconcileTimer?.Stop();
        _reconcileTimer = null;
        _foregroundSubscription?.Dispose();
        _foregroundSubscription = null;
        Cancel();            // 会话若在进行中：关掉状态，不留悬空高亮
        Hook.Dispose();      // 卸钩 + 断事件
        _tracked.Clear();
        _apps.Clear();
        _iconCache.Clear();
    }

    // ---------- MRU 跟踪 ----------

    /// <summary>共享前台钩子通知：把新前台窗口移到 MRU 首位（不重建列表、不取图标——事件路径保持极轻）。</summary>
    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        var idx = IndexOf(hwnd);
        if (idx > 0)
        {
            var item = _tracked[idx];
            _tracked.RemoveAt(idx);
            _tracked.Insert(0, item);
            item.IsMinimized = NativeMethods.IsIconic(hwnd);
            var t = WindowEligibility.GetWindowTitle(hwnd);
            if (t.Length > 0) item.Title = t;
            return;
        }
        if (idx == 0) return; // 已是首位
        if (!WindowEligibility.IsEligibleWindow(hwnd, _ownPid)) return;      // 我们的面板/菜单等自身窗口：忽略
        var created = CreateTracked(hwnd);
        if (created is not null) _tracked.Insert(0, created);
    }

    /// <summary>
    /// 2s 兜底对账：以 <c>EnumWindows</c> 的 Z 序为准补入漏报窗口、移除失效项、刷新标题/最小化态。
    /// 既有项位置不动（MRU 由前台事件决定，Z 序只是初始快照），新项插到其 Z 序前驱之后——
    /// 与 Dock 的 WindowTracker 同一策略，避免每次对账都把列表打乱。
    /// </summary>
    private void Reconcile()
    {
        if (_disposed) return;

        _pendingList.Clear();
        _pendingSet.Clear();
        NativeMethods.EnumWindows(_collectProc, IntPtr.Zero);

        for (int i = _tracked.Count - 1; i >= 0; i--)
            if (!_pendingSet.Contains(_tracked[i].Handle))
                _tracked.RemoveAt(i);

        int anchor = -1;
        foreach (var hwnd in _pendingList)
        {
            var idx = IndexOf(hwnd);
            if (idx >= 0)
            {
                anchor = idx;
                var item = _tracked[idx];
                item.IsMinimized = NativeMethods.IsIconic(hwnd);
                var title = WindowEligibility.GetWindowTitle(hwnd);
                if (title.Length > 0) item.Title = title;
            }
            else
            {
                var created = CreateTracked(hwnd);
                if (created is null) continue;
                _tracked.Insert(anchor + 1, created);
                anchor++;
            }
        }

        // 图标预热（评审发现 I4）：磁盘 IO 只允许出现在这个 2s 批处理里——前台事件路径与按键
        // 路径都不再提取。每轮限量 8 个，防一批新应用同时取图卡住 UI 线程；漏网的下一轮补齐。
        int warmBudget = 8;
        for (int i = 0; i < _tracked.Count && warmBudget > 0; i++)
        {
            if (_iconCache.ContainsKey(_tracked[i].AppId)) continue;
            IconFor(_tracked[i].AppId);
            warmBudget--;
        }
    }

    private bool CollectEligible(IntPtr hwnd, IntPtr lParam)
    {
        if (WindowEligibility.IsEligibleWindow(hwnd, _ownPid))
        {
            _pendingList.Add(hwnd);
            _pendingSet.Add(hwnd);
        }
        return true; // 继续枚举
    }

    private TrackedWindow? CreateTracked(IntPtr hwnd)
    {
        var title = WindowEligibility.GetWindowTitle(hwnd);
        if (title.Length == 0) return null;
        var exe = WindowEligibility.GetWindowExePath(hwnd);
        string appId, appName;
        if (exe.Length > 0)
        {
            appId = exe;
            appName = Path.GetFileNameWithoutExtension(exe);
        }
        else
        {
            // 取不到路径（权限受限/UWP 宿主）：用类名兜底分组，至少同宿主的窗口聚在一起
            appId = "cls:" + NativeMethods.GetWindowClassName(hwnd);
            appName = NativeMethods.GetWindowClassName(hwnd);
        }
        // 此处**不**取图标：CreateTracked 也在前台事件路径上（见 OnForegroundChanged），
        // 图标提取统一挪到 Reconcile 的批量预热（评审发现 I4：键路径/事件路径零磁盘）。
        return new TrackedWindow(hwnd, appId, appName, title, NativeMethods.IsIconic(hwnd));
    }

    /// <summary>
    /// 按 exe 路径缓存图标。<b>唯一调用点是 <see cref="Reconcile"/> 的批量预热</b>（每轮限量），
    /// 前台事件路径与按键路径都不再触盘（评审发现 I4）。
    /// </summary>
    private BitmapSource? IconFor(string appId)
    {
        if (_iconCache.TryGetValue(appId, out var cached)) return cached;
        var icon = appId.StartsWith("cls:", StringComparison.Ordinal) ? null : IconExtractor.GetIcon(appId);
        _iconCache[appId] = icon;
        return icon;
    }

    /// <summary>
    /// 面板用：图标只查缓存，未命中返回 null（面板画占位符，最迟下一轮 2s 对账预热补齐）。
    /// 这里刻意<b>不</b>回源提取——"键路径零磁盘 IO"必须由结构保证，而不是靠"预热通常已完成"
    /// 的时序巧合（评审发现 I4 附带项）。
    /// </summary>
    public BitmapSource? GetIcon(string appId)
    {
        EnsureUiThread();
        if (string.IsNullOrEmpty(appId)) return null;
        if (_iconCache.TryGetValue(appId, out var icon)) return icon;
        Logger.Info($"切换器：图标未预热（本轮以占位符显示，待对账补齐）: {appId}");
        return null;
    }

    // ---------- 会话（打开/步进/提交/取消） ----------

    /// <summary>
    /// OpenRequested：由 MRU 生成 Z 序 <see cref="WindowEntry"/> 序列 → 分组（Task 10）→ 可选最小化惩罚
    /// → 把选择初始化到"当前前台 App"，随后到达的 NextRequested 即落到上一个应用（原生/macOS 手感）。
    /// </summary>
    private void OnOpenRequested()
    {
        if (_disposed) return;
        _apps.Clear();
        _apps.AddRange(WindowListMerger.GroupByApp(BuildEntries()));
        // 全最小化的应用沉底：Alt+Tab 的首要目的是把"看得见的窗口"提到前面
        var penalized = WindowListMerger.ApplyMinimizedPenalty(_apps, NativeMethods.IsIconic);
        _apps.Clear();
        _apps.AddRange(penalized);

        if (_apps.Count == 0)
        {
            Logger.Info("切换器：无可切换窗口（列表为空），本次不接管显示");
            IsOpen = false; SelectedAppIndex = -1;
            return;
        }

        IsOpen = true;
        SelectedWindowIndex = 0;
        SelectedAppIndex = IndexOfApp(NativeMethods.GetForegroundWindow());
        if (SelectedAppIndex < 0) SelectedAppIndex = 0;
        Logger.Info($"切换器：打开，应用 {(_apps.Count)} 个 / 窗口 {_tracked.Count} 个");
        Opened?.Invoke();
    }

    /// <summary>MRU → WindowEntry（Priority = Z/MRU 序号，透传给面板显示与调试）。</summary>
    private List<WindowEntry> BuildEntries()
    {
        var list = new List<WindowEntry>(_tracked.Count);
        for (int i = 0; i < _tracked.Count; i++)
        {
            var w = _tracked[i];
            if (!NativeMethods.IsWindow(w.Handle)) continue; // 对账间隔内已销毁
            list.Add(new WindowEntry(w.Handle, w.AppId, w.AppName, w.Title, i));
        }
        return list;
    }

    private int IndexOfApp(IntPtr hwnd)
    {
        for (int a = 0; a < _apps.Count; a++)
            for (int w = 0; w < _apps[a].Windows.Count; w++)
                if (_apps[a].Windows[w].Handle == hwnd) return a;
        return -1;
    }

    /// <summary>循环步进（forward=1 前进、-1 后退）。列表为空时 no-op。</summary>
    public void StepBy(int forward)
    {
        if (_disposed || !IsOpen || _apps.Count == 0) return;
        var next = WindowListMerger.NextIndex(SelectedAppIndex, _apps.Count, forward < 0);
        if (next == SelectedAppIndex) return;
        SelectedAppIndex = next;
        SelectedWindowIndex = 0;
        SelectionChanged?.Invoke();
    }

    /// <summary>选中 App 内的窗口步进（macOS 二次展开：同一 App 多窗口）。direction=±1，回绕。</summary>
    public void StepWindow(int direction)
    {
        if (_disposed || !IsOpen) return;
        var app = CurrentApp;
        if (app is null || app.Windows.Count == 0) return;
        SelectedWindowIndex = WindowListMerger.NextIndex(SelectedWindowIndex, app.Windows.Count, direction < 0);
        SelectionChanged?.Invoke();
    }

    /// <summary>面板鼠标点选/悬停：直接定位到某 App 的某个窗口。</summary>
    public void Select(int appIndex, int windowIndex = 0)
    {
        if (_disposed || _apps.Count == 0) return;
        if (appIndex < 0 || appIndex >= _apps.Count) return;
        SelectedAppIndex = appIndex;
        SelectedWindowIndex = Math.Clamp(windowIndex, 0, Math.Max(0, _apps[appIndex].Windows.Count - 1));
        IsOpen = true;
        SelectionChanged?.Invoke();
    }

    /// <summary>面板窗口内点选用（同 <see cref="Select"/> 但只改窗口位）。</summary>
    public void SelectWindow(int windowIndex)
    {
        if (_disposed || CurrentApp is null) return;
        SelectedWindowIndex = Math.Clamp(windowIndex, 0, Math.Max(0, CurrentApp.Windows.Count - 1));
        SelectionChanged?.Invoke();
    }

    private SwitcherApp? CurrentApp
        => SelectedAppIndex >= 0 && SelectedAppIndex < _apps.Count ? _apps[SelectedAppIndex] : null;

    /// <summary>当前选中的窗口（无选中返回 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr SelectedHandle
    {
        get
        {
            var app = CurrentApp;
            if (app is null || app.Windows.Count == 0) return IntPtr.Zero;
            var i = Math.Clamp(SelectedWindowIndex, 0, app.Windows.Count - 1);
            return app.Windows[i].Handle;
        }
    }

    /// <summary>提交当前选中项（键盘松开触发键 / 面板点击共用）。</summary>
    public void CommitSelected()
    {
        if (_disposed || !IsOpen) return;
        var hwnd = SelectedHandle;
        IsOpen = false;
        SelectedAppIndex = -1;
        SelectedWindowIndex = 0;
        if (hwnd != IntPtr.Zero) Commit(hwnd);
        Closed?.Invoke();
    }

    /// <summary>取消会话（Esc / 面板关闭）：不改变前台。</summary>
    public void Cancel()
    {
        if (_disposed) return;
        var wasOpen = IsOpen;
        IsOpen = false;
        SelectedAppIndex = -1;
        SelectedWindowIndex = 0;
        if (wasOpen) Closed?.Invoke();
    }

    // ---------- 提交切换 ----------

    /// <summary>
    /// 把窗口切到前台：①已最小化先 <c>SW_RESTORE</c>；②<c>SetForegroundWindow</c>；
    /// ③失败（系统前台锁：非前台进程通常被压制）则 <c>AttachThreadInput</c> 挂到当前前台线程的输入队列
    /// 再试一次——**detach 放在 finally**，配对失败会让两个线程的输入队列永久绑定（本项目早前评审就在
    /// 别处抓到过这种泄漏风险，绝不重犯）；④最后一招合成一次 Alt 抬起（keybd_event），让系统在
    /// "Alt 按住"的合法窗口里接受这次激活。每步都用 <c>GetForegroundWindow</c> 实测判定，返回是否成功。
    /// </summary>
    public bool Commit(IntPtr hwnd)
    {
        EnsureUiThread();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, SwRestore);

        NativeMethods.SetForegroundWindow(hwnd);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        var fg = NativeMethods.GetForegroundWindow();
        var curTid = NativeMethods.GetCurrentThreadId();
        var fgTid = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0u;
        var attached = fgTid != 0 && fgTid != curTid &&
                       NativeMethods.AttachThreadInput(curTid, fgTid, true);
        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.BringWindowToTop(hwnd);
            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            // 最后一招：合成 Alt 点一下（无前台锁限制的系统会在此状态下接受激活）
            TapAlt();
            NativeMethods.SetForegroundWindow(hwnd);
            var ok = NativeMethods.GetForegroundWindow() == hwnd;
            if (!ok) Logger.Warn($"切换器：SetForegroundWindow 未能激活 0x{hwnd.ToInt64():X}");
            return ok;
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(curTid, fgTid, false);
        }
    }

    private static void TapAlt()
    {
        NativeMethods.keybd_event((byte)NativeMethods.VkLMenu, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event((byte)NativeMethods.VkLMenu, 0, NativeMethods.KeyeventfKeyup, UIntPtr.Zero);
    }

    private int IndexOf(IntPtr hwnd)
    {
        for (int i = 0; i < _tracked.Count; i++)
            if (_tracked[i].Handle == hwnd) return i;
        return -1;
    }

    private void EnsureUiThread()
    {
        if (!_ui.CheckAccess())
            throw new InvalidOperationException("SwitcherService 仅允许在 WPF UI 线程使用（钩子回调与列表变更均绑定该线程）。");
    }
}
