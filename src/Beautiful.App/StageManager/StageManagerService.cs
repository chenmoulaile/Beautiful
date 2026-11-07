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
/// 台前调度服务（wp-macos-parity r6，macOS Stage Manager 的 Windows 等效）：维护"可切换窗口"
/// 的 MRU 快照、用 Core 的纯函数分组、按显示器驱动<strong>每屏一条</strong>舞台条窗口，并把点击
/// 真正切到目标窗口。
/// <para>
/// 【数据流】与切换器同一套（<see cref="ForegroundWatcher"/> 共享前台钩子 + 2s EnumWindows
/// 兜底对账 + <see cref="WindowEligibility"/> 统一资格），快照彼此独立——本仓库的既定形态就是
/// "Dock/切换器各自 2s 对账、快照独立暂未合并"（见 App.EnsureSwitcher 注释），台前调度按同一
/// 形态加入第三份。对账产物先经 <see cref="StageGrouper.SplitByMonitor"/> 按屏拆分（MRU 序稳定
/// 划分，见下【每屏一条】），每屏子列再走 <see cref="StageGrouper.Build"/>（该屏前台组置顶）→
/// <see cref="StageGrouper.Limit"/>（8 卡截断）交给各自的 <see cref="StageBarWindow"/> 渲染。
/// </para>
/// <para>
/// 【每屏一条（r6）】<see cref="MonitorGeometry.Surfaces"/> 枚举在屏显示器，每块屏一条
/// 舞台条（<c>_bars</c> 按显示器句柄持有；拔屏对账时收掉消失屏的条）。窗口→屏归属表
/// <c>_monitorOf</c>：非最小化窗口按 <c>MonitorFromWindow</c> 实查并缓存，<b>最小化窗口沿用
/// 最后一次非最小化的归属</b>（最小化矩形在 -32000，实查会串到主屏）；无归属的窗口不出现在
/// 任何一条上。全屏让位改为按屏对账：哪屏被真全屏盖住，哪屏的条整体退场，别的屏照常更新。
/// </para>
/// <para>
/// 【开关与无残留】<see cref="ApplySettings"/> 是唯一开关入口（设置页 UserControl 与启动装配都走它）：
/// <c>StageManagerEnabled</c>（默认关）开 = 启动跟踪 + 惰性建条；关 = 停跟踪 + 关全部条 +
/// 注销全部缩略图句柄 + 卸 Esc 钩子，不留常驻 HWND / 定时器 / 订阅。单条装配失败降级为
/// "该屏无条"，绝不连坐其他屏与主 UI（与顶栏/切换器同一处置）。
/// </para>
/// <para>
/// 【切换】点击卡片/弹层行 → <see cref="SwitchTo"/>：优先走切换器已验证的前台提交链
/// （App.CommitWindowForeground → SwitcherService.Commit：SW_RESTORE + SetForegroundWindow +
/// AttachThreadInput 兜底），切换器未装配时退化为 restore + SetForegroundWindow 直调。
/// </para>
/// <para>
/// 【全局 Esc（r6）】"悬停展开→焦点还在原应用"时也要 Esc 能收条：任一条展开（或二级弹层在场）
/// 时挂一条 WH_KEYBOARD_LL（<see cref="LowLevelKeyboardHook"/>，收起即卸——常态零开销），回调
/// 只认 Esc 且<b>永远放行</b>（不吞键：Esc 同时属于前台应用；与 AltTabHook 的会话内吞 Esc 链式
/// 共存、互不干扰）。不并进 AltTabHook 那条钩子的原因：那条的生命周期归切换器（开关/应急停用
/// 会整条卸下），挂上去互相牵连；本钩子只在展开态在场，代价更小、归属清晰。回调纪律与
/// AltTabHook 相同：只累加计数 + 投递排空，收条动作在 UI 线程排空回调里做。
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
    private readonly Action _escDrain;                           // 缓存委托：Esc 排空零分配（AltTabHook 同法）

    // EnumWindows 回调复用同一对实例字段（每轮 Reconcile 先 Clear）：与 SwitcherService 同一手法。
    private readonly List<IntPtr> _pendingList = new();
    private readonly HashSet<IntPtr> _pendingSet = new();

    private readonly List<TrackedWindow> _tracked = new();        // MRU，首 = 最近
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<IntPtr, StageBarWindow> _bars = new(); // 显示器句柄 → 该屏舞台条（每屏一条）
    private readonly Dictionary<IntPtr, IntPtr> _monitorOf = new();    // 窗口句柄 → 归属屏（最小化沿用最后归属）

    private DispatcherTimer? _reconcileTimer;
    private IDisposable? _foregroundSubscription;
    private LowLevelKeyboardHook? _escHook;        // 全局 Esc（仅展开态在场；见类型注释）
    private volatile bool _escArm;                 // 钩子回调读：展开态才认 Esc
    private int _pendingEsc;
    private int _escDrainScheduled;
    private bool _running;
    private bool _disposed;

    public StageManagerService()
    {
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_ui.CheckAccess())
            throw new InvalidOperationException("StageManagerService 必须在 WPF UI 线程上构造。");
        _collectProc = CollectEligible;
        _escDrain = EscDrain;
    }

    /// <summary>是否处于"开关开 + 跟踪在线"状态（诊断用）。</summary>
    public bool IsRunning => _running;

    /// <summary>在屏舞台条（显示器句柄 → 条；诊断 / 测试用）。</summary>
    public IReadOnlyDictionary<IntPtr, StageBarWindow> Bars => _bars;

    // ---------- 开关（唯一入口） ----------

    /// <summary>
    /// 按当前设置对账启停（设置页开关与启动装配共用；幂等）。开：启动跟踪并惰性建条；
    /// 关：停跟踪 + 关全部条 + 清空缩略句柄 + 卸 Esc 钩子——不留下任何常驻窗口 / 定时器 / 订阅。
    /// </summary>
    public void ApplySettings()
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StageManagerService));
        if (SettingsService.Current.StageManagerEnabled) Start();
        else Stop();
    }

    /// <summary>启动 MRU 跟踪并确保每屏舞台条在场（幂等）。</summary>
    public void Start()
    {
        EnsureUiThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StageManagerService));
        if (_running) return;
        _running = true;
        _foregroundSubscription = ForegroundWatcher.Subscribe(OnForegroundChanged);
        Reconcile(); // 初始 EnumWindows 快照（也顺带确保每屏条在场）
        _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconcileTimer.Tick += (_, _) => Reconcile();
        _reconcileTimer.Start();
        Logger.Info("台前调度：已启动（每屏一条舞台条贴左缘，鼠标靠近展开）");
    }

    /// <summary>
    /// 停止并撤除（幂等）：关全部舞台条（窗口销毁即注销其缩略图句柄）、停对账定时器、退订前台钩子、
    /// 卸 Esc 钩子、清跟踪表。开关关到一半进程退出、或退出链路 <see cref="Dispose"/> 重放，都安全。
    /// </summary>
    public void Stop()
    {
        EnsureUiThread();
        _reconcileTimer?.Stop();
        _reconcileTimer = null;
        _foregroundSubscription?.Dispose();
        _foregroundSubscription = null;
        CloseBars();
        UninstallEscHook();
        _tracked.Clear();
        _monitorOf.Clear();
        if (_running)
        {
            _running = false;
            Logger.Info("台前调度：已停止（舞台条已全部关闭，无残留）");
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
        {
            if (_pendingSet.Contains(_tracked[i].Handle)) continue;
            _monitorOf.Remove(_tracked[i].Handle);   // 归属表与跟踪表同生共死
            _tracked.RemoveAt(i);
        }

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
        EnsureBars();

        // 窗口→屏归属：非最小化实查（MonitorFromWindow），最小化沿用缓存；前台此刻必非最小化，
        // 立即刷新——切到另一屏的窗口后，那一屏的高亮位当拍跟上。
        UpdateMonitorMap(foreground);

        // 全屏让位按屏对账：哪屏被真全屏盖住哪屏退场，别的屏照常更新（r6 前是一票否决全退场）。
        foreach (var bar in _bars.Values)
            bar.SetSuppressed(IsFullscreenCovering(bar));

        var snapshot = new List<StageWindow>(_tracked.Count);
        for (int i = 0; i < _tracked.Count; i++)
        {
            var w = _tracked[i];
            if (!IsWindow(w.Handle)) continue;   // 对账间隔内已销毁
            snapshot.Add(new StageWindow(w.Handle, w.AppId, w.AppName, w.Title, w.IsMinimized));
        }

        // 按屏拆分（MRU 稳定划分）→ 每条各自分组渲染；让位屏不发快照（窗口已隐藏，发了也是白画）。
        var split = StageGrouper.SplitByMonitor(snapshot, _monitorOf);
        foreach (var (monitor, bar) in _bars)
        {
            if (bar.IsSuppressed) continue;
            var subset = split.TryGetValue(monitor, out var s) ? s : null;
            var foregroundHere = foreground != IntPtr.Zero
                && _monitorOf.TryGetValue(foreground, out var fgMonitor) && fgMonitor == monitor
                    ? foreground
                    : IntPtr.Zero;
            var groups = StageGrouper.Limit(
                StageGrouper.Build(subset ?? (IEnumerable<StageWindow>)Array.Empty<StageWindow>(), foregroundHere),
                StageGrouper.DefaultMaxGroups);
            bar.UpdateGroups(groups);
        }
    }

    /// <summary>
    /// 刷新窗口→屏归属表（<see cref="MonitorGeometry.TryMonitorForWindow"/>）。死窗顺手清出；
    /// 最小化窗口<b>不实查</b>（矩形在 -32000 会串屏），沿用最后一次非最小化的缓存归属——
    /// 从未有过归属的最小化窗口不显示在任何一条上（宁可少一张卡，不猜屏）。
    /// </summary>
    private void UpdateMonitorMap(IntPtr foreground)
    {
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            var item = _tracked[i];
            if (!IsWindow(item.Handle))
            {
                _monitorOf.Remove(item.Handle);
                continue;
            }
            if (item.IsMinimized) continue;
            if (MonitorGeometry.TryMonitorForWindow(item.Handle, out var monitor))
                _monitorOf[item.Handle] = monitor;
        }
        if (foreground != IntPtr.Zero && MonitorGeometry.TryMonitorForWindow(foreground, out var fgMonitor))
            _monitorOf[foreground] = fgMonitor;
    }

    /// <summary>
    /// 舞台条所在屏是否被真全屏前台盖住（让位判据与 <c>FullscreenAvoidanceService.ShouldHide</c>
    /// 同构：条窗口矩形中心找显示器 → Core 几何比较；读不到矩形/显示器按"不让位"处理）。
    /// </summary>
    private bool IsFullscreenCovering(StageBarWindow bar)
    {
        var hwnd = bar.WindowHandle;
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

    // ---------- 舞台条窗口（每屏一条） ----------

    /// <summary>
    /// 按在屏显示器对账条数（幂等）：新屏补建、消失的屏收条。单条装配失败只降级那一屏
    /// （记日志继续），绝不连坐其他屏与主 UI。
    /// </summary>
    private void EnsureBars()
    {
        if (_disposed) return;
        var surfaces = MonitorGeometry.Surfaces();
        if (surfaces.Count == 0) return;   // 拔屏瞬间/远程会话：保持现状，下一拍回来

        var present = new HashSet<IntPtr>();
        foreach (var surface in surfaces) present.Add(surface.Handle);
        List<IntPtr>? stale = null;
        foreach (var (monitor, bar) in _bars)
            if (!present.Contains(monitor)) (stale ??= new List<IntPtr>()).Add(monitor);
        if (stale is not null)
            foreach (var monitor in stale) CloseAndRemoveBar(monitor);

        foreach (var surface in surfaces)
        {
            if (_bars.ContainsKey(surface.Handle)) continue;
            StageBarWindow? bar = null;   // 提升出 try：catch 必须能收走半装配实例（EnsureSwitcher 同纪律）
            try
            {
                bar = new StageBarWindow(this, surface.Handle);
                bar.ShowCollapsedSliver();   // 非激活显示：只露贴边小条，不夺焦点
                _bars[surface.Handle] = bar;
            }
            catch (Exception ex)
            {
                try { bar?.ForceClose(); } catch { /* 关不上就随进程退场，不留托管引用 */ }
                Logger.Warn($"台前调度：舞台条窗口装配失败（该屏降级为无舞台条）: {ex.Message}");
            }
        }
    }

    private void CloseAndRemoveBar(IntPtr monitor)
    {
        if (!_bars.Remove(monitor, out var bar)) return;
        try { bar.ForceClose(); }
        catch (Exception ex) { Logger.Warn($"台前调度：舞台条关闭失败: {ex.Message}"); }
    }

    private void CloseBars()
    {
        foreach (var monitor in new List<IntPtr>(_bars.Keys))
            CloseAndRemoveBar(monitor);
    }

    // ---------- 展开/收起状态对账（Esc 钩子的武装判定） ----------

    /// <summary>
    /// 舞台条在展开/弹层/收起后回调：重算"是否有条在场且展开"，据此挂/卸全局 Esc 钩子。
    /// 幂等、无副作用（钩子只在状态翻越 0↔1 边界时装配/卸载）。
    /// </summary>
    internal void NotifyExpansionChanged()
    {
        if (_disposed || !_running) { _escArm = false; return; }
        var needed = false;
        foreach (var bar in _bars.Values)
        {
            if (bar.IsSuppressed) continue;   // 让位中的条已隐藏：Esc 无处可收，不武装
            if (bar.IsExpanded || bar.IsFlyoutOpen) { needed = true; break; }
        }
        _escArm = needed;
        if (needed && _escHook is null)
        {
            var hook = new LowLevelKeyboardHook(OnEscKey);
            if (hook.Install())
            {
                _escHook = hook;
                Logger.Info("台前调度：全局 Esc 钩子已挂（展开态收条用，收起即卸）");
            }
            else
            {
                hook.Dispose();   // 装配失败降级：Esc 收条退化为"条/弹层持焦"场景（各自的 WPF 兜底仍有效）
                Logger.Warn("台前调度：全局 Esc 钩子装配失败（Esc 收起退化为持焦场景）");
            }
        }
        else if (!needed && _escHook is not null)
        {
            UninstallEscHook();
        }
    }

    /// <summary>钩子回调（热路径）：只认"展开态的真实 Esc 按下"，累加计数后投递排空。<b>永远放行</b>。</summary>
    private bool OnEscKey(int msg, uint vk, uint flags)
    {
        if (!_escArm) return false;
        if (msg is not (NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)) return false;
        if (vk != NativeMethods.VkEscape) return false;
        Interlocked.Add(ref _pendingEsc, 1);
        ScheduleEscDrain();
        return false;   // 永远放行：Esc 同时属于前台应用（我们只搭车收条，不替它做主）
    }

    private void ScheduleEscDrain()
    {
        if (Interlocked.Exchange(ref _escDrainScheduled, 1) == 1) return; // 已在队列里
        try { _ui.BeginInvoke(DispatcherPriority.Input, _escDrain); }
        catch { Interlocked.Exchange(ref _escDrainScheduled, 0); }        // Dispatcher 关停：放弃本次投递
    }

    private void EscDrain()
    {
        Interlocked.Exchange(ref _escDrainScheduled, 0);
        if (Interlocked.Exchange(ref _pendingEsc, 0) == 0) return;
        foreach (var bar in _bars.Values)
            bar.CollapseFromEsc();
    }

    private void UninstallEscHook()
    {
        _escArm = false;
        if (_escHook is null) return;
        var hook = _escHook;
        _escHook = null;
        hook.Dispose();
    }

    // ---------- 切换 ----------

    /// <summary>
    /// 点击卡片/弹层行 → 切到该窗口：优先切换器的提交链（SW_RESTORE + SetForegroundWindow +
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
