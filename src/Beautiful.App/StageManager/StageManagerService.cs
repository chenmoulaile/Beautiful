using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using Beautiful.Core.StageManager;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度服务（wp-macos-parity r5，macOS Stage Manager 的 Windows 等效）：维护"可切换窗口"
/// 的 MRU 快照、用 Core 的纯函数分组、驱动左侧舞台条窗口，并把点击真正切到目标窗口。
/// <para>
/// 【数据流】与切换器同一套（<see cref="ForegroundWatcher"/> 共享前台钩子 + 2s EnumWindows
/// 兜底对账 + <see cref="WindowEligibility"/> 统一资格），快照彼此独立——本仓库的既定形态就是
/// "Dock/切换器各自 2s 对账、快照独立暂未合并"（见 App.EnsureSwitcher 注释），台前调度按同一
/// 形态加入第三份。对账产物经 <see cref="StageGrouper.Build"/>（前台组置顶）→
/// <see cref="StageGrouper.Limit"/>（8 卡截断）交给 <see cref="StageBarWindow"/> 渲染。
/// </para>
/// <para>
/// 【开关与无残留】<see cref="ApplySettings"/> 是唯一开关入口（设置页 UserControl 与启动装配都走它）：
/// <c>StageManagerEnabled</c>（默认关）开 = 启动跟踪 + 惰性建窗（收起的贴边小条）；关 = 停跟踪 +
/// 关窗 + 注销全部缩略图句柄，不留常驻 HWND / 定时器 / 订阅。装配失败降级为"无台前调度"，
/// 绝不连坐主 UI（与顶栏/切换器同一处置）。
/// </para>
/// <para>
/// 【切换】点击卡片 → <see cref="SwitchTo"/>：优先走切换器已验证的前台提交链
/// （App.CommitWindowForeground → SwitcherService.Commit：SW_RESTORE + SetForegroundWindow +
/// AttachThreadInput 兜底），切换器未装配时退化为 restore + SetForegroundWindow 直调。
/// </para>
/// <para>
/// 【线程模型】全部公开成员仅限 WPF UI 线程（钩子回调投递在该线程消息泵上，与 SwitcherService 同）。
/// </para>
/// </summary>
public sealed class StageManagerService : IDisposable
{
    private const int SwRestore = 9; // SW_RESTORE

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

    private readonly Dispatcher _ui;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly NativeMethods.EnumWindowsProc _collectProc; // 缓存：对账不重复分配委托

    // EnumWindows 回调复用同一对实例字段（每轮 Reconcile 先 Clear）：与 SwitcherService 同一手法。
    private readonly List<IntPtr> _pendingList = new();
    private readonly HashSet<IntPtr> _pendingSet = new();

    private readonly List<TrackedWindow> _tracked = new();        // MRU，首 = 最近
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _reconcileTimer;
    private IDisposable? _foregroundSubscription;
    private StageBarWindow? _bar;
    private bool _running;
    private bool _disposed;

    public StageManagerService()
    {
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_ui.CheckAccess())
            throw new InvalidOperationException("StageManagerService 必须在 WPF UI 线程上构造。");
        _collectProc = CollectEligible;
    }

    /// <summary>是否处于"开关开 + 跟踪在线"状态（诊断用）。</summary>
    public bool IsRunning => _running;

    /// <summary>当前舞台条窗口（诊断 / 测试用；null = 未建或已关闭）。</summary>
    public StageBarWindow? Bar => _bar;

    // ---------- 开关（唯一入口） ----------

    /// <summary>
    /// 按当前设置对账启停（设置页开关与启动装配共用；幂等）。开：启动跟踪并惰性建窗；
    /// 关：停跟踪 + 关窗 + 清空缩略句柄——不留下任何常驻窗口 / 定时器 / 订阅。
    /// </summary>
    public void ApplySettings()
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StageManagerService));
        if (SettingsService.Current.StageManagerEnabled) Start();
        else Stop();
    }

    /// <summary>启动 MRU 跟踪并确保舞台条窗口在场（幂等）。</summary>
    public void Start()
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StageManagerService));
        if (_running) return;
        _running = true;
        _foregroundSubscription = ForegroundWatcher.Subscribe(OnForegroundChanged);
        Reconcile(); // 初始 EnumWindows 快照（也顺带确保窗口在场）
        _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconcileTimer.Tick += (_, _) => Reconcile();
        _reconcileTimer.Start();
        Logger.Info("台前调度：已启动（舞台条贴左缘，鼠标靠近展开）");
    }

    /// <summary>
    /// 停止并撤除（幂等）：关舞台条（窗口销毁即注销其缩略图句柄）、停对账定时器、退订前台钩子、
    /// 清跟踪表。开关关到一半进程退出、或退出链路 <see cref="Dispose"/> 重放，都安全。
    /// </summary>
    public void Stop()
    {
        EnsureUiThread();
        _reconcileTimer?.Stop();
        _reconcileTimer = null;
        _foregroundSubscription?.Dispose();
        _foregroundSubscription = null;
        CloseBar();
        _tracked.Clear();
        if (_running)
        {
            _running = false;
            Logger.Info("台前调度：已停止（舞台条已关闭，无残留）");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ui.CheckAccess()) Stop();
        else { try { _ui.Invoke(Stop); } catch { /* 关停竞态：句柄随进程销毁 */ } }
    }

    // ---------- MRU 跟踪（与 SwitcherService 同一算法，快照独立） ----------

    /// <summary>共享前台钩子通知：把新前台窗口移到 MRU 首位（事件路径保持极轻，不取图标）。</summary>
    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (_disposed || !_running || hwnd == IntPtr.Zero) return;
        ReconcileNow();   // 前台变了 → 高亮位与全屏让位都要跟上；对账本身极轻（复用 2s 管线）
    }

    /// <summary>2s 兜底对账：EnumWindows Z 序补漏 / 剔失效 / 刷新标题与最小化态（切换器同策略）。</summary>
    private void Reconcile()
    {
        if (_disposed || !_running) return;

        _pendingList.Clear();
        _pendingSet.Clear();
        EnumWindows(_collectProc, IntPtr.Zero);

        for (int i = _tracked.Count - 1; i >= 0; i--)
            if (!_pendingSet.Contains(_tracked[i].Handle))
                _tracked.RemoveAt(i);

        foreach (var hwnd in _pendingList)
        {
            var idx = IndexOf(hwnd);
            if (idx >= 0)
            {
                var item = _tracked[idx];
                item.IsMinimized = IsIconic(hwnd);
                var title = WindowEligibility.GetWindowTitle(hwnd);
                if (title.Length > 0) item.Title = title;
            }
            else
            {
                var created = CreateTracked(hwnd);
                if (created is null) continue;
                _tracked.Add(created);   // 首见按 Z 序追加（EnumWindows 自带 Z 序）
            }
        }

        // 图标预热（与切换器同一预算纪律）：批量磁盘 IO 只允许出现在这 2s 一拍里。
        int warmBudget = 8;
        for (int i = 0; i < _tracked.Count && warmBudget > 0; i++)
        {
            if (_iconCache.ContainsKey(_tracked[i].AppId)) continue;
            IconFor(_tracked[i].AppId);
            warmBudget--;
        }

        PublishSnapshot();
    }

    /// <summary>轻量重发快照（前台事件路径）：不做 EnumWindows，只用既有 MRU 表 + 当前前台。</summary>
    private void ReconcileNow()
    {
        if (_disposed || !_running) return;
        for (int i = 0; i < _tracked.Count; i++)
        {
            if (!IsWindow(_tracked[i].Handle)) continue;   // 死窗留给 2s 对账清理
            var item = _tracked[i];
            item.IsMinimized = IsIconic(item.Handle);
        }
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        var foreground = GetForegroundWindow();
        EnsureBar();

        // 全屏让位：真全屏前台（游戏/视频）盖住舞台条所在屏时整体退场，退出全屏下一拍回来。
        //（与全屏避让服务/屏幕圆角遮罩同一判据：Core FullscreenAvoidance，最大化不触发。）
        var suppressed = IsFullscreenCovering();
        _bar?.SetSuppressed(suppressed);
        if (suppressed) return;   // 全屏期间不发快照（窗口已隐藏，发了也是白画）

        var snapshot = new List<StageWindow>(_tracked.Count);
        for (int i = 0; i < _tracked.Count; i++)
        {
            var w = _tracked[i];
            if (!IsWindow(w.Handle)) continue;   // 对账间隔内已销毁
            snapshot.Add(new StageWindow(w.Handle, w.AppId, w.AppName, w.Title, w.IsMinimized));
        }
        var groups = StageGrouper.Limit(StageGrouper.Build(snapshot, foreground), StageGrouper.DefaultMaxGroups);
        _bar?.UpdateGroups(groups);
    }

    /// <summary>
    /// 舞台条所在屏是否被真全屏前台盖住（让位判据与 <c>FullscreenAvoidanceService.ShouldHide</c>
    /// 同构：窗口矩形中心找显示器 → Core 几何比较；读不到矩形/显示器按"不让位"处理）。
    /// </summary>
    private bool IsFullscreenCovering()
    {
        if (_bar is null) return false;
        var hwnd = _bar.WindowHandle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;
        var area = MonitorGeometry.AreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        if (area is null) return false;
        return !FullscreenAvoidance.Evaluate(area.Value.Monitor, FullscreenProbe.Read()).KeepTopmost;
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
            appId = "cls:" + GetWindowClassName(hwnd);
            appName = GetWindowClassName(hwnd);
        }
        // 此处不取图标：CreateTracked 也可能出现在 2s 对账批内，图标统一走批量预热（预算 8/轮）。
        return new TrackedWindow(hwnd, appId, appName, title, IsIconic(hwnd));
    }

    /// <summary>按 appId 查图标（只查缓存；未命中返回 null，最迟下一轮 2s 预热补齐）。</summary>
    public BitmapSource? GetIcon(string appId)
    {
        EnsureUiThread();
        if (string.IsNullOrEmpty(appId)) return null;
        return _iconCache.TryGetValue(appId, out var icon) ? icon : null;
    }

    /// <summary>图标预热：macOS 图标集优先（与 Dock/切换器同一张脸），无对应图回落原提取。</summary>
    private void IconFor(string appId)
    {
        var icon = appId.StartsWith("cls:", StringComparison.Ordinal)
            ? null
            : MacIconCatalog.TryGetFor(appId) ?? IconExtractor.GetIcon(appId);
        _iconCache[appId] = icon;
    }

    // ---------- 舞台条窗口 ----------

    private void EnsureBar()
    {
        if (_bar is not null || _disposed) return;
        StageBarWindow? bar = null;   // 提升出 try：catch 必须能收走半装配实例（EnsureSwitcher 同纪律）
        try
        {
            bar = new StageBarWindow(this);
            bar.ShowCollapsedSliver();   // 非激活显示：只露贴边小条，不夺焦点
            _bar = bar;
        }
        catch (Exception ex)
        {
            try { bar?.ForceClose(); } catch { /* 关不上就随进程退场，不留托管引用 */ }
            Logger.Warn($"台前调度：舞台条窗口装配失败（降级为无舞台条运行）: {ex.Message}");
        }
    }

    private void CloseBar()
    {
        if (_bar is null) return;
        var old = _bar;
        _bar = null;
        try { old.ForceClose(); }
        catch (Exception ex) { Logger.Warn($"台前调度：舞台条关闭失败: {ex.Message}"); }
    }

    // ---------- 切换 ----------

    /// <summary>
    /// 点击卡片 → 切到该窗口：优先切换器的提交链（SW_RESTORE + SetForegroundWindow +
    /// AttachThreadInput 兜底，经真机验证），切换器未装配时退化为直调（每步用
    /// GetForegroundWindow 实测判定）。返回是否成功（失败只记日志，不改前台）。
    /// </summary>
    public bool SwitchTo(IntPtr hwnd)
    {
        EnsureUiThread();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (App.CommitWindowForeground(hwnd)) return true;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        var ok = GetForegroundWindow() == hwnd;
        if (!ok) Logger.Warn($"台前调度：SetForegroundWindow 未能激活 0x{hwnd.ToInt64():X}");
        return ok;
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
            throw new InvalidOperationException("StageManagerService 仅允许在 WPF UI 线程使用。");
    }
}
