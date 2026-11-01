using System.Diagnostics;
using System.Runtime.InteropServices;   // ExternalException / Marshal.SizeOf
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Animation;
using Beautiful.Core.Layout;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;   // UseWindowsForms 隐式 using 与 System.Drawing.Rectangle 冲突
using static Beautiful.App.Interop.NativeMethods;   // RECT / SetWindowPos / Swp* 与其余原生声明同前缀

namespace Beautiful.App.Animation;

/// <summary>
/// macOS 风格的最小化 / 最大化动画。
/// <para>
/// 【为什么用 WinEvent 而不是钩子拦 <c>SC_MINIMIZE</c>】要"抢在"系统最小化之前拦，只能上全局 CBT 钩子，
/// 而全局钩子必须把回调放进一个注入到别人进程的 DLL 里 —— 为一个动画引入注入组件完全不值。
/// <c>EVENT_SYSTEM_MINIMIZESTART</c> 在最小化开始的那一刻回调；<b>但这个回调到达我们手里时窗口通常已经缩下去了</b>
/// （v4.0 真机实测：回调里 <c>IsIconic=True</c>、rect=-16000），所以画面必须在别处先留好：
/// Dock 路径在 <c>ShowWindow</c> 前同步预抓，其余三条路径靠前台帧缓存兜底（见 <see cref="ForegroundFrameCache"/>）。
/// </para>
/// <para>
/// 【alpha.25 换思路：遮挡重播】24H2 真机实测：SPI_SETANIMATION 不响应、FORCEDISABLED 报
/// E_HANDLE、CLOAK 报 E_ACCESSDENIED——"把原生动画从源头关掉"的三条路在这台机器上全部不存在，
/// 双动画叠加无法靠 DWM 治。改为遮挡：替身第一帧 = 原矩形上的不透明全尺寸画面，Show 出来
/// 就把正在缩放的真窗口完全盖住，原生动画在替身底下播，用户只看得见 genie 一条。
/// 开窗动画走自家窗口自导（PlayOpenForSelf：Opacity=0 → 替身落位 → 现身），全程不碰 DWM 可见性。
/// </para>
/// <para>
/// 【为什么替身用分层窗口 + 条带 Rectangle / 网格】逐像素透明只有分层窗口这一条路，genie 的弯曲感
/// 来自"每条带各自向目标收拢"。几何全在 <see cref="GenieMath"/>/<see cref="GenieLamp"/>（纯函数 + 单测），
/// 这里只负责每帧把它乘回像素。
/// </para>
/// <para>
/// 【v3.0 冲刺改了什么】① 全部时长/比例改读 <see cref="AnimParams"/>(热加载)；② 放行判定收口到
/// <see cref="AnimPolicy"/>（风暴/并发/节流/排除/全屏）；③ 每条早退都产出结构化
/// <c>动画未播：源= 层= 因=</c>，每次成功都产出 <c>动画触发：… 帧数= P95=</c>；
/// ④ 挂载自检把"钩子句柄 + 设置实值 + 生效引擎"写进日志，且对外暴露降级原因（设置页可见）。
/// </para>
/// </summary>
public sealed class MinimizeAnimator : IDisposable
{
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectLocationChange = 0x8004;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectDestroy = 0x8001;   // alpha.27：窗口销毁（关闭动画触发源）
    private const uint ObjidWindow = 0;
    private const uint WinEventOutOfContext = 0x00000000;
    private const uint WinEventSkipOwnProcess = 0x00000002;

    /// <summary>收拢后的宽度相对源窗口的比例（≈ Dock 图标宽 / 普通窗口宽）。</summary>
    private const double EndWidthRatio = 0.10;

    /// <summary>小于这个边长（物理像素）的窗口不套动画：下拉框/浮动提示条抖一下只会显得廉价。</summary>
    private const int MinShellSize = 120;

    /// <summary>Dock 上该窗口图标那一格的物理矩形；null = 这条窗口没进 Dock。</summary>
    private readonly Func<IntPtr, Rect?> _dockTarget;
    private readonly Dispatcher _dispatcher;

    // 委托与钩子句柄都必须留在字段里：回调桩被 GC 的话，原生侧回调就是随机崩溃
    // （与 ForegroundWatcher / LowLevelKeyboardHook 同一套纪律）
    private NativeMethods.WinEventDelegate? _proc;
    private IntPtr _minimizeHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;
    private IntPtr _showHook = IntPtr.Zero;
    private IntPtr _destroyHook = IntPtr.Zero;

    /// <summary>窗口过渡接管器（alpha.21）：原生动画的开关与可见性代管都归它。可为 null（极端降级）。</summary>
    private readonly WindowTransitionGuard? _guard;

    /// <summary>genie 收尾时要解斗篷的窗口：还原/最大化/打开动画结束后真窗口才现身。</summary>
    private IntPtr _pendingUncloak = IntPtr.Zero;

    /// <summary>自家开窗动画（PlayOpenForSelf）收尾时要执行的现身回调（fail-open 见方法注释）。</summary>
    private Action? _pendingReveal;

    /// <summary>已尝试过打开动画的窗口（同一 hwnd 只播一次；上限整体重置）。</summary>
    private readonly HashSet<IntPtr> _openAttempted = new();

    private GhostLayer? _ghost;
    private GenieMeshGhost? _meshGhost;
    private bool _disabledByEnvironment;

    /// <summary>前台帧缓存（兜底捕获层）。采样在后台线程，查询在 UI 线程。</summary>
    private readonly ForegroundFrameCache _frames = new();

    /// <summary>
    /// alpha.26 新引擎：<b>DWM 实况缩略图 genie</b>（零抓帧、零预渲染，事件到手即起播）。
    /// 惰性创建；连续失败 3 次自动停用，本会话退回旧图像链路。26H2 真机日志结论：
    /// PrintWindow 半瘫、mesh 预渲染迟到 ~1s，而这台机器上唯一被证明可靠的实时画面通道
    /// 就是 DWM 缩略图（悬停预览 hr=0 全通过）——动画引擎因此长在它上面。
    /// </summary>
    private DwmLiveGhost? _live;
    private int _liveFailures;

    /// <summary>
    /// hwnd → 最后一次<b>非图标</b>的窗口矩形（物理像素）。事件走系统队列送达时窗口总是已
    /// iconic、GetWindowRect 只会给出 (-32000) 哨兵——这是 alpha.25 及以前
    /// <c>几何对账=无幽灵（未起播）</c>的根因。本表是事件路径唯一的可信几何来源：
    /// LOCATIONCHANGE / OBJECT_SHOW / 启动播种三处喂入，最小化哨兵矩形绝不进表。
    /// </summary>
    private readonly Dictionary<IntPtr, RECT> _lastGoodRect = new();

    /// <summary>放行门禁（风暴/并发/节流/排除/全屏）。</summary>
    private readonly AnimPolicy _policy = new(AnimParamsStore.Current);

    /// <summary>hwnd → 上一次的 (矩形, 是否最大化)。只为识别"从非最大化跳到最大化"那一下。</summary>
    private readonly Dictionary<IntPtr, (RECT Rect, bool Zoomed)> _lastRect = new();

    // alpha.27：窗口身份表（类名 + 进程号，一一对应 hwnd）。EVENT_OBJECT_DESTROY 到手时
    // 窗口已经销毁——类名/进程号/矩形全部查不出来，关闭动画的"值不值得播"判定
    // 只能靠生前留下的身份证（与 _lastRect 同一个 512 上限、同一次整体重来）。
    private readonly Dictionary<IntPtr, (string Class, uint Pid)> _identityByHwnd = new();

    /// <summary>pid → 进程名。排除清单与日志都要它；每 3-4 分钟才有新 pid，缓存足够。</summary>
    private readonly Dictionary<uint, string> _processNames = new();

    private DispatcherTimer? _sampleTimer;
    private string _exclusionSnapshot = "";

    public MinimizeAnimator(Func<IntPtr, Rect?> dockTargetProvider, WindowTransitionGuard? guard = null)
    {
        _dockTarget = dockTargetProvider;
        _guard = guard;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _disabledByEnvironment = Environment.GetEnvironmentVariable("BEAUTIFUL_NO_ANIMATION") == "1";
        if (_disabledByEnvironment)
        {
            DisabledReason = "环境开关 BEAUTIFUL_NO_ANIMATION=1";
            MountStatus = $"动画子系统未启用：{DisabledReason}";
            Logger.Info(MountStatus);
            return;
        }

        _proc = OnWinEvent;
        // 一条钩子覆盖 MINIMIZESTART..MINIMIZEEND 这一段：最小化与"从最小化还原"是同一个
        // 动画的正反两放，分开装两条钩子只会多一份句柄要管。
        // alpha.25 起这条钩子**不跳过自家进程**：访达/设置窗口的最小化同样要播 genie
        // （macOS 本来就这样）；自家窗口不会去斗篷/SW_HIDE（alpha.25 全链路已移除），
        // 也不会与自家表面互相干扰（Dock/启动台/替身窗口从不最小化）。
        _minimizeHook = NativeMethods.SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext);
        _locationHook = NativeMethods.SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        // OBJECT_SHOW：新窗口出现的那一刻——关掉它的原生过渡（存量窗口由启动扫描补）+ 决定要不要
        // 斗篷代管后播打开动画。这是"打开动画与原生开窗动画重叠"的根治点。
        _showHook = NativeMethods.SetWinEventHook(
            EventObjectShow, EventObjectShow, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        // OBJECT_DESTROY（v2.2 §4 关闭项，默认关）：窗口销毁的最后一刻。同样跳过自家进程——
        // 自家窗口一律走 WindowCloseFx 真动画，不进这条事件路径。
        _destroyHook = NativeMethods.SetWinEventHook(
            EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);

        ApplyExclusions();
        SeedKnownRects();
        BuildMountStatus();
        StartFrameSampler();
    }

    /// <summary>
    /// 启动播种：把存量普通窗口的当前矩形喂进 <see cref="_lastGoodRect"/>。
    /// 只在启动后第一次最小化的窗口（从未触发过 LOCATIONCHANGE）没有这张表，
    /// 会在事件路径被“无最后已知矩形”挡住——用一次 EnumWindows 把这个窗口期补掉。
    /// </summary>
    private void SeedKnownRects()
    {
        var ownPid = (uint)Environment.ProcessId;
        var seeded = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
                if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == ownPid) return true;
                if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
                if (r.Right - r.Left < MinShellSize || r.Bottom - r.Top < MinShellSize) return true;
                _lastGoodRect[hwnd] = r;
                var cls = NativeMethods.GetWindowClassName(hwnd);   // alpha.27：播种时就把身份证一起建好
                _identityByHwnd[hwnd] = (cls, pid);
                seeded++;
            }
            catch
            {
                // 枚举回调里的任何异常只跳过该窗口
            }
            return true;
        }, IntPtr.Zero);
        Logger.Info($"genie 几何表：启动播种 {seeded} 个存量窗口矩形");
    }

    /// <summary>动画子系统未启用的原因（设置页/诊断块用）；null = 正常运行。</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>挂载自检行：钩子实值 + 设置实值 + 生效引擎 + 参数摘要（v3.0 §Q2-1）。</summary>
    public string MountStatus { get; private set; } = "动画子系统：未初始化";

    /// <summary>
    /// 自检并落日志：<b>不是</b>打印"已挂载"就算完——要把两个钩子句柄、设置项实值、
    /// 引擎（native 会回退）、参数摘要一起写下来。用户报"动画没播"时，这一行就能排除掉
    /// "设置关了"/"引擎退化了"/"钩子没装上"三类原因，剩下的才值得去查捕获。
    /// </summary>
    private void BuildMountStatus()
    {
        var p = AnimParamsStore.Current;
        var s = SettingsService.Current;
        var requestedEngine = string.IsNullOrWhiteSpace(s.GenieEngine) ? p.Engine : s.GenieEngine;
        var effectiveEngine = EffectiveEngine(requestedEngine);
        var hooksOk = _minimizeHook != IntPtr.Zero;
        var locOk = _locationHook != IntPtr.Zero;
        var showOk = _showHook != IntPtr.Zero;
        var destroyOk = _destroyHook != IntPtr.Zero;

        if (!hooksOk)
        {
            DisabledReason = "MINIMIZESTART 钩子安装失败（SetWinEventHook 返回 0）";
            MountStatus = $"动画子系统未启用：{DisabledReason}";
            Logger.Warn(MountStatus);
            return;
        }

        DisabledReason = null;
        var degraded = new List<string>();
        if (!locOk) degraded.Add("LOCATIONCHANGE 钩子失败（最大化动画不可用）");
        if (!showOk) degraded.Add("OBJECT_SHOW 钩子失败（打开动画不可用）");
        if (!destroyOk) degraded.Add("OBJECT_DESTROY 钩子失败（关闭动画不可用）");
        if (_guard is null) degraded.Add("过渡接管器未装配（原生动画可能与替身叠加）");
        if (!s.MinimizeAnimation) degraded.Add("设置项 MinimizeAnimation 为关");
        if (effectiveEngine != requestedEngine) degraded.Add($"引擎 {requestedEngine} 不可用已回退 {effectiveEngine}");

        MountStatus = $"动画子系统已挂载：MINIMIZE=0x{_minimizeHook.ToInt64():X} "
                      + $"LOCATION={(locOk ? $"0x{_locationHook.ToInt64():X}" : "失败")} "
                      + $"SHOW={(showOk ? $"0x{_showHook.ToInt64():X}" : "失败")} "
                      + $"DESTROY={(destroyOk ? $"0x{_destroyHook.ToInt64():X}" : "失败")} "
                      + $"接管器={( _guard is null ? "未装配" : $"托管 {_guard.ManagedCount} 斗篷 {_guard.CloakedCount}" )} "
                      + $"设置=开 请求引擎={requestedEngine} 生效引擎={effectiveEngine} "
                      + $"前台缓存=开 参数[{p.ToLogLine()}]"
                      + (degraded.Count > 0 ? $" 降级：{string.Join("；", degraded)}" : "");
        Logger.Info(MountStatus);
    }

    /// <summary>
    /// 生效引擎：<c>native</c> 在 native 场景渲染器可用时才算数，否则回退 <c>mesh</c>；
    /// <c>mesh</c> 在 3D 面建不起来时运行期再退 <c>strips</c>（见 StartMesh）。
    /// </summary>
    private static string EffectiveEngine(string requested)
    {
        var r = requested?.Trim().ToLowerInvariant() ?? "mesh";
        if (r == "native")
            return SettingsService.Current.NativeModules && SettingsService.Current.NativeScene && NativeLoader.Available
                ? "native"
                : "mesh";
        return r is "mesh" or "strips" ? r : "mesh";
    }

    /// <summary>当前是否启用（设置页/诊断块用）。</summary>
    public bool IsRunning => _minimizeHook != IntPtr.Zero && !_disabledByEnvironment;

    /// <summary>前台缓存条数（诊断行用）。</summary>
    public int CachedFrames => _frames.Count;

    /// <summary>
    /// OOM 自愈钩子（App 侧调用）：立刻丢弃全部前台帧。缓存里 1 全尺寸(720px) + 4 半尺寸，
    /// 都是 frozen 位图——断头时它们就是进程里最大的几块非托管内存，先丢先活。
    /// </summary>
    public void ClearFrameCache() => _frames.Clear();

    /// <summary>
    /// OOM 自愈钩子：暂停前台采样一段时间。采样是唯一的高频位图分配源（每 500ms 一张
    /// PrintWindow + 缩放），内存紧张时先把它停掉，给 DUCE 通道喘口气的窗口。
    /// 恢复由一次性 DispatcherTimer 到点自停（不需要任何人记得来重启）。
    /// </summary>
    public void PauseSampling(TimeSpan duration)
    {
        _sampleTimer?.Stop();
        if (_sampleTimer is null) return;
        var resume = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = duration,
        };
        resume.Tick += (_, _) =>
        {
            resume.Stop();
            if (_sampleTimer is not null && IsRunning)
            {
                _sampleTimer.Start();
                Logger.Info($"前台帧采样：熔断结束（暂停 {duration.TotalSeconds:0}s）后已恢复");
            }
        };
        resume.Start();
        Logger.Warn($"前台帧采样：熔断暂停 {duration.TotalSeconds:0}s（内存压力自愈）");
    }

    /// <summary>
    /// 原生回调：只许做"取出参数 + 投递"两件事。任何异常逃逸出去都会打断消息泵，
    /// 任何阻塞都会让系统认为我们不该收事件（键盘钩子还会被静默摘掉，同一类事故）。
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (idObject != (int)ObjidWindow || idChild != 0) return;   // 只关心顶层窗口本身，不管子元素

            if (evt == EventSystemMinimizeStart)
            {
                // 【alpha.25 遮挡重播，不再斗篷】事件到手时原生"缩向任务栏"动画多半已经开跑
                //（真机实测回调里 IsIconic=True）。不再跟 DWM 斗法（斗篷/SPI/FORCEDISABLED
                // 在 24H2 上全被拒），改用替身遮挡：替身第一帧 = 窗口原矩形上的不透明全尺寸画面，
                // 真窗口的缩放动画被它盖在底下，用户只看得见 genie 一条动画。
                _dispatcher.BeginInvoke(() => PlayMinimize(hwnd, "MINIMIZESTART"), DispatcherPriority.Render);
                return;
            }
            if (evt == EventSystemMinimizeEnd)
            {
                // 还原方向：此刻窗口已经可见，替身从图标那一行"流出来"长回原尺寸。
                // 不装这一条的话最小化看着像 mac、点开却瞬间出现，反而更暴露。
                _dispatcher.BeginInvoke(() => PlayRestore(hwnd), DispatcherPriority.Render);
                return;
            }
            if (evt == EventObjectLocationChange)
            {
                // LOCATIONCHANGE 在拖动窗口时每帧都来，所以这里只做 O(1) 的状态比对，
                // 真正可能耗时的动画只在"非最大化 ↔ 最大化"的跳变上触发。
                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
                var zoomed = NativeMethods.IsZoomed(hwnd);
                if (_lastRect.TryGetValue(hwnd, out var previous))
                {
                    if (!previous.Zoomed && zoomed && IsBigJump(previous.Rect, rect))
                    {
                        _guard?.Cloak(hwnd, "maximize");
                        _dispatcher.BeginInvoke(() => PlayZoom(hwnd, previous.Rect, rect, zoomIn: true),
                            DispatcherPriority.Render);
                    }
                    else if (previous.Zoomed && !zoomed && IsBigJump(rect, previous.Rect))
                    {
                        _guard?.Cloak(hwnd, "unmaximize");
                        _dispatcher.BeginInvoke(() => PlayZoom(hwnd, previous.Rect, rect, zoomIn: false),
                            DispatcherPriority.Render);
                    }
                }
                else if (_lastRect.Count > 512)
                {
                    _lastRect.Clear();   // 只缓存"见过的窗口"，超上限整体重来，不做 LRU 的复杂度
                    _lastGoodRect.Clear();
                }
                _lastRect[hwnd] = (rect, zoomed);
                // alpha.26：非图标矩形才是"最后已知好矩形"。最小化本身也会发 LOCATIONCHANGE
                // （rect=-32000 哨兵），那种样本绝不能进表——尺寸/图标位双重判定。
                if (!NativeMethods.IsIconic(hwnd) && rect.Right > rect.Left && rect.Bottom > rect.Top
                    && rect.Right - rect.Left >= MinShellSize && rect.Bottom - rect.Top >= MinShellSize)
                {
                    _lastGoodRect[hwnd] = rect;
                    RememberIdentity(hwnd);   // alpha.27：顺带记下类名/进程号（DESTROY 后只剩这张身份证）
                }
                return;
            }
            if (evt == EventObjectShow)
            {
                HandleWindowShown(hwnd);
                return;
            }
            if (evt == EventObjectDestroy)
            {
                // alpha.27 关闭动画（默认关）：此刻窗口已经销毁，能做的只有"拿生前的
                // 缓存帧在原位演完退场"。设置未开时 PlayClose 静默返回——功能没开就
                // 没有"尝试"，也没有"未播"日志（DESTROY 对菜单/提示窗非常频繁，不能刷屏）。
                _dispatcher.BeginInvoke(() => PlayClose(hwnd), DispatcherPriority.Render);
                return;
            }
        }
        catch
        {
            // 原生回调里连日志都可能抛（Logger 要写文件）：静默吞掉是这里唯一正确的选择
        }
    }

    /// <summary>
    /// 记住窗口的类名与进程号（一次性；已在表内的窗口零开销直过）。
    /// DESTROY 到手时 hwnd 失效，这是 PlayClose 唯一的类名/进程来源。
    /// </summary>
    private void RememberIdentity(IntPtr hwnd)
    {
        if (_identityByHwnd.ContainsKey(hwnd)) return;
        if (_identityByHwnd.Count > 512) _identityByHwnd.Clear();   // 与 _lastRect 同一纪律
        var cls = NativeMethods.GetWindowClassName(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        _identityByHwnd[hwnd] = (cls, pid);
    }

    private static bool IsBigJump(RECT from, RECT to)
    {
        var fw = from.Right - from.Left;
        var fh = from.Bottom - from.Top;
        var tw = to.Right - to.Left;
        var th = to.Bottom - to.Top;
        return fw > 0 && fh > 0 && tw > fw * 1.2 && th > fh * 1.2;
    }

    // ---------- 打开动画（OBJECT_SHOW → 斗篷代管 → 替身落位 → 解斗篷） ----------

    /// <summary>
    /// 新窗口出现的处置：先无条件接管过渡动画（存量窗口由启动扫描补），
    /// 再决定要不要斗篷+打开动画。所有早退路径都必须先解斗篷——这条链路里
    /// 窗口刚要现身，被我们斗住了却不放出来，就是"窗口凭空消失"级事故。
    /// </summary>
    private void HandleWindowShown(IntPtr hwnd)
    {
        try
        {
            if (_guard is null || hwnd == IntPtr.Zero) return;
            _guard.EnsureNoTransitions(hwnd);   // 存量/新窗口都在这里统一接管
            if (!SettingsService.Current.WindowOpenAnimation) return;
            if (_openAttempted.Contains(hwnd)) return;

            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlpExStyle);
            if ((ex & NativeMethods.WsExToolwindow) != 0) return;
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return;
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) return;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out var cloaked, 4) == 0
                && cloaked != 0) return;   // UWP 挂起/浏览器隐藏窗：别人的斗篷不碰
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0) return;
            var own = pid == (uint)Environment.ProcessId;
            // 自家窗口一律不进事件路径（alpha.25）：这条钩子带 SkipOwnProcess，自家窗口的事件
            // 本来就到不了这里（alpha.23 的 SelfContentWindows 白名单从未真正生效；斗篷门禁在
            // 24H2 上又被 DWM 拒绝，双重死路）。自家内容窗口（访达）的开窗动画改由窗口自身在
            // 首帧渲染后调 PlayOpenForSelf 自导：Opacity=0 预藏 → 替身落位 → 现身，不碰 DWM。
            if (own) return;
            var className = NativeMethods.GetWindowClassName(hwnd);
            if (className is "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "Progman" or "WorkerW"
                or "XamlExplorerHostIslandWindow") return;
            // 排除名单面向第三方应用；自家内容窗口不受名单约束
            if (!own && _policy.IsExcludedName(ProcessName(pid))) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
            if (r.Right - r.Left < MinShellSize || r.Bottom - r.Top < MinShellSize) return;
            // alpha.26：新窗口的首个可信矩形顺手进表（启动后从未移动过的窗口只能从这里获得首次样本）
            if (!NativeMethods.IsIconic(hwnd))
            {
                _lastGoodRect[hwnd] = r;
                RememberIdentity(hwnd);   // alpha.27：类名/进程号同源记录
            }

            _openAttempted.Add(hwnd);
            if (_openAttempted.Count > 256) _openAttempted.Clear();

            // 斗得住才播：斗篷失败就让它直接出现（fail-open），只丢动画不丢窗口
            if (!_guard.Cloak(hwnd, "open")) return;
            _dispatcher.BeginInvoke(() => PlayOpen(hwnd), DispatcherPriority.Render);
        }
        catch
        {
            // WinEvent 回调：静默吞掉（与 OnWinEvent 同一纪律）
        }
    }

    /// <summary>打开动画：替身从 90% 平滑落位淡入，放完解斗篷换真窗口上台。</summary>
    private void PlayOpen(IntPtr hwnd)
    {
        void FailOpen(string why, string layer = "无")
        {
            _guard?.Uncloak(hwnd, $"打开动画早退:{why}");
            _policy.Complete(AnimKind.Open);
            Logger.Info(AnimReport.Skipped("OBJECT_SHOW", layer, why));
        }

        // 排队期间窗口可能又藏起来了/被销毁：斗篷必须先解
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            _guard?.Uncloak(hwnd, "打开时窗口已不可见");
            return;
        }
        if (!Admit(hwnd, AnimKind.Open, "OBJECT_SHOW", out var durationMs, out _))
        {
            FailOpen("门禁未放行");
            return;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            FailOpen("读窗口矩形失败");
            return;
        }
        var target = ToRect(rect);
        if (target.Width < MinShellSize || target.Height < MinShellSize)
        {
            FailOpen("窗口过小", $"{target.Width:0}×{target.Height:0}");
            return;
        }
        var image = Capture(hwnd, "OBJECT_SHOW");
        if (image is null)
        {
            FailOpen("抓帧不可用");
            return;
        }
        var composed = GenieFrameSource.MakeOpaque(image);
        if (composed is null) { FailOpen("帧整理失败"); return; }
        image = composed;
        var layer = _lastLayer;
        _frames.Put(hwnd, image, target);

        var p = AnimParamsStore.Current;
        var dur = durationMs > 0 ? durationMs : p.Scaled(p.OpenDurationMs);
        var startScale = Math.Clamp(p.OpenStartScale, 0.5, 1.0);
        _pendingUncloak = hwnd;
        LogGeometry("OBJECT_SHOW", layer, hwnd, target, Rect.Empty);
        StartGhost(image, target, dur, t =>
        {
            var e = 1.0 - Math.Pow(1.0 - t, 3.0);   // easeOutCubic：开头快、落位稳，macOS 开窗手感
            var scale = startScale + (1.0 - startScale) * e;
            var w = target.Width * scale;
            var h = target.Height * scale;
            var r = new GenieRect(
                (target.Width - w) / 2 / target.Width,
                (target.Height - h) / 2 / target.Height,
                w / target.Width, h / target.Height);
            return new List<(GenieRect, double)> { (r, e) };
        }, "OBJECT_SHOW", "open", layer, 0);
    }

    // ---------- 门禁 ----------

    /// <summary>
    /// 排除清单（设置项 MinimizeExcludes）→ 策略。每次动画前调一次：设置改了不必重启动画器
    /// （与"动画器装配只在启动"是两件事：装配是钩子，排除是数据）。
    /// </summary>
    private void ApplyExclusions()
    {
        var raw = SettingsService.Current.MinimizeExcludes ?? "";
        if (string.Equals(raw, _exclusionSnapshot, StringComparison.Ordinal)) return;
        _exclusionSnapshot = raw;
        var list = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();
        _policy.SetExclusions(list);
        Logger.Info($"动画排除清单：{(list.Count == 0 ? "空" : string.Join("、", list))}");
    }

    /// <summary>
    /// 请求放行。<b>放行后调用方必须 <see cref="AnimPolicy.Complete"/></b>（在 <see cref="RunClock"/>
    /// 的收尾里统一做），否则并发槽会漏，几轮之后所有动画都会被"并发上限"挡住。
    /// </summary>
    private bool Admit(IntPtr hwnd, AnimKind kind, string source, out int durationMs, out uint pid)
    {
        durationMs = 0;
        pid = 0;

        if (!IsRunning)
        {
            Logger.Info(AnimReport.Skipped(source, "无", "动画子系统未启用", DisabledReason));
            return false;
        }
        if (Busy)
        {
            Logger.Info(AnimReport.Skipped(source, "无", "上一条动画未结束（单替身闸门）"));
            return false;
        }

        ApplyExclusions();
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        var name = ProcessName(pid);
        var fullscreen = IsFullscreenForeground();

        var decision = _policy.Evaluate(new AnimRequest(kind, pid, name, NowMs(), fullscreen, Source: source));
        if (!decision.Allowed)
        {
            Logger.Info(AnimReport.Skipped(source, "无", decision.Reason, $"进程={name}"));
            return false;
        }
        durationMs = decision.DurationMs;
        _activeKind = kind;
        return true;
    }

    /// <summary>当前已放行那一条动画的种类。单个替身闸门（<see cref="Busy"/>）保证同时只有一条在跑，
    /// 所以用一个字段即可——不必把 kind 沿着六个渲染函数一路传下去。</summary>
    private AnimKind _activeKind = AnimKind.Minimize;

    /// <summary>单调毫秒（策略只吃时间戳，不读系统时钟）。</summary>
    private static long NowMs() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    private string ProcessName(uint pid)
    {
        if (pid == 0) return "未知";
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            if (_processNames.Count > 256) _processNames.Clear();
            _processNames[pid] = name;
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                     or System.ComponentModel.Win32Exception)
        {
            return "未知";
        }
    }

    /// <summary>前台窗口是否真全屏（盖住整块显示器）。判定复用 Core 的 <see cref="FullscreenAvoidance"/>。</summary>
    private static bool IsFullscreenForeground()
    {
        var fore = FullscreenProbe.Read();
        if (fore is not { IsEligible: true } f) return false;
        var mon = MonitorFromWindow(f.Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        var r = mi.rcMonitor;
        return FullscreenAvoidance.CoversMonitor(f.Bounds, new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom));
    }

    /// <summary>
    /// 抓帧并留进前台缓存。返回 null 时已经写好"动画未播"行——调用方直接返回即可。
    /// </summary>
    private BitmapSource? Capture(IntPtr hwnd, string source)
    {
        var image = WindowCapture.Capture(hwnd, out var layer);
        if (image is null)
        {
            Logger.Info(AnimReport.Skipped(source, layer, "抓帧不可用"));
            return null;
        }
        _lastLayer = layer;
        return image;
    }

    /// <summary>最近一次抓帧命中的层（进触发日志）。</summary>
    private string _lastLayer = "无";

    // ---------- 动画 ----------

    /// <summary>
    /// 最小化：<b>alpha.26 起首选 DWM 实况缩略图 genie</b>——替身像素由 DWM 合成器直接采样
    /// 源窗口表面，零抓帧、零预渲染，事件到手即起播；旧图像链路（抓帧 + mesh/strips）降为
    /// DWM 通道建不起来时的退路。
    /// <para>
    /// 【alpha.26 几何修正】事件走系统队列送达时窗口几乎总是已 iconic（真机实测：
    /// IsIconic=True、rect=-32000 哨兵），GetWindowRect 从此不可信——这正是 alpha.25 及以前
    /// <c>几何对账=无幽灵（未起播）</c>的根因。可信几何只有一处：LOCATIONCHANGE 持续维护的
    /// "最后已知非图标矩形"表（启动播种兜底存量窗口）。
    /// </para>
    /// <para>
    /// 【alpha.25 遗留说明】遮挡重播的"不碰窗口状态"原则保留：斗篷/SW_HIDE 代管不再回来，
    /// 窗口状态全程不被我们碰，任务栏按钮/Dock 跟踪/对账都不受影响。
    /// </para>
    /// </summary>
    private void PlayMinimize(IntPtr hwnd, string source)
    {
        if (!Admit(hwnd, AnimKind.Minimize, source, out var durationMs, out _))
        {
            return;   // 门禁拒绝：不遮不演，原生动画自然播完（降级安全）
        }

        var p = AnimParamsStore.Current;

        // ── alpha.26：几何先于一切 ──
        var iconic = NativeMethods.IsIconic(hwnd);
        Rect sourceRect;
        if (!iconic && NativeMethods.GetWindowRect(hwnd, out var rect)
            && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            sourceRect = ToRect(rect);
            _lastGoodRect.Remove(hwnd);
            _lastGoodRect.Add(hwnd, rect);
        }
        else if (_lastGoodRect.TryGetValue(hwnd, out var good) && good.Right > good.Left && good.Bottom > good.Top)
        {
            sourceRect = ToRect(good);
        }
        else
        {
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "无最后已知矩形（启动播种与 LOCATIONCHANGE 均未覆盖）"));
            return;
        }
        if (sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize)
        {
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "窗口过小", $"{sourceRect.Width:0}×{sourceRect.Height:0}"));
            return;
        }

        var iconRect = IconRectFor(sourceRect, _dockTarget(hwnd));
        LogGeometry(source, "DWM实况", hwnd, sourceRect, iconRect);

        // ── alpha.26 新引擎：事件到手即起播（不再等抓帧/合成/建网格）──
        if (TryPlayLive(hwnd, sourceRect, iconRect, durationMs, reverse: false, source, 0))
            return;

        // ── 退路：alpha.25 图像链路（DWM 通道连续失败停用时才走到；行为不变）──
        if (iconic)
        {
            // 事件走系统队列送达时状态早已翻转（真机实测：回调里 IsIconic=True、rect=-16000,-16000）。
            // 画面只能取前台缓存里替这个窗口留的那一帧；龄超过判废线就如实说"没画面"。
            if (_frames.TryGet(hwnd, p.ForegroundCacheDiscardMs, out var cached, out var cachedRect, out var age)
                && cached is not null && cachedRect.Width >= MinShellSize)
            {
                var verdict = _policy.Usability((long)age);
                Logger.Info($"genie 事件路径：0x{hwnd.ToInt64():X} 到手时已最小化，改用缓存帧补播 "
                            + $"(龄={age:0}ms 判定={verdict})");
                var icon = IconRectFor(cachedRect, _dockTarget(hwnd));
                LogGeometry(source, "缓存", hwnd, cachedRect, icon);
                // 缓存帧同样过实心合成：alpha 洞不会因为帧老就变少
                if (GenieFrameSource.MakeOpaque(cached) is not { } composed)
                {
                    _policy.Complete(AnimKind.Minimize);
                    return;
                }
                StartGenie(composed, cachedRect, icon, durationMs, reverse: false, source, "缓存", age);
                return;
            }

            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "缓存", "到手时已最小化且缓存帧过期/缺失",
                $"缓存条数={_frames.Count}"));
            return;
        }

        var image = GenieFrameSource.MakeOpaque(Capture(hwnd, source));
        if (image is null)
        {
            _policy.Complete(AnimKind.Minimize);
            return;
        }
        var layer = _lastLayer;
        _frames.Put(hwnd, image, sourceRect);   // 留一份：最小化后悬停预览与后续补播都靠它

        LogGeometry(source, layer, hwnd, sourceRect, iconRect);
        StartGenie(image, sourceRect, iconRect, durationMs, reverse: false, source, layer, 0);
    }

    /// <summary>
    /// Dock 触发的最小化专用：<b>趁窗口还画着</b>先抓一帧。
    /// <para>
    /// 事件路径救不了这条链——<c>EVENT_SYSTEM_MINIMIZESTART</c> 走系统队列送达时，
    /// 我们那次同步的 <c>ShowWindow(SW_MINIMIZE)</c> 早已完成、状态早已翻转，
    /// <see cref="PlayMinimize"/> 开头的 IsIconic 早退必然命中：真机上动画一次没播过就是这么来的。
    /// 所以由 Dock 在 ShowWindow <b>之前</b>把画面与矩形抓走，动画拿预抓的那份放。
    /// </para>
    /// </summary>
    public (BitmapSource? Image, Rect? SourceRect) PreCapture(IntPtr hwnd)
    {
        if (!IsRunning || Busy) return (null, null);
        if (hwnd == IntPtr.Zero || NativeMethods.IsIconic(hwnd)) return (null, null);

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return (null, null);
        var sourceRect = ToRect(rect);
        // alpha.26：预抓矩形 = ShowWindow 之前的准确几何，顺手进 lastGoodRect 表（tile 宽高比也靠它）
        _lastGoodRect.Remove(hwnd);
        _lastGoodRect.Add(hwnd, rect);
        var image = GenieFrameSource.MakeOpaque(WindowCapture.Capture(hwnd, out var layer));
        if (image is null)
        {
            Logger.Info(AnimReport.Skipped("Dock预抓", layer, "抓帧不可用"));
            return (null, null);
        }
        _frames.Put(hwnd, image, sourceRect);   // 顺手留一份：这个窗口下次最小化后悬停预览就有画面了
        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        LogGeometry("Dock预抓", layer, hwnd, sourceRect, icon);
        Logger.Info($"Dock 预抓帧：0x{hwnd.ToInt64():X} 层={layer} "
                    + $"{image.PixelWidth}×{image.PixelHeight} 落点={icon.X:0},{icon.Y:0} {icon.Width:0}×{icon.Height:0}");
        return (image, sourceRect);
    }

    /// <summary>用预抓的画面放 genie。窗口此刻多半已经缩下去，矩形用预抓的那份，不再现查。</summary>
    public void PlayMinimizeFromCapture(IntPtr hwnd, BitmapSource image, Rect sourceRect)
    {
        if (!Admit(hwnd, AnimKind.Minimize, "Dock", out var durationMs, out _))
        {
            return;   // 门禁拒绝：Dock 链路已 ShowWindow(SW_MINIMIZE)，原生动画自然播完
        }
        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        // alpha.26：预抓矩形 = 最小化前的准确几何，DWM 实况 genie 首选（预抓位图只作 tile 兼退路）
        if (TryPlayLive(hwnd, sourceRect, icon, durationMs, reverse: false, "Dock", 0))
            return;
        // 预抓帧入替前同样过实心合成；合成器对非空输入不会返回 null（?? image 兜底保型）
        StartGenie(GenieFrameSource.MakeOpaque(image) ?? image, sourceRect, icon, durationMs, reverse: false, "Dock", "预抓帧", 0);
    }

    /// <summary>
    /// 自家内容窗口（访达）的 mac 开窗动画——alpha.25 自导链路。
    /// <para>
    /// 背景：斗篷开窗链路在这台机器上是双重死路（OBJECT_SHOW 钩子带 SkipOwnProcess 收不到
    /// 自家窗口的事件；即便收到，DWM 也拒绝斗篷 E_ACCESSDENIED）。改为窗口自助：
    /// 窗口以 Opacity=0 完成首帧渲染后调这里——抓帧（PrintWindow 对"可见但全透明"的窗口
    /// 照常出图）→ 替身从 94% 落位 → 收尾回调 <paramref name="reveal"/> 把真窗口亮出来。
    /// 全程不碰 DWM 可见性/窗口状态，任何机器行为一致。
    /// </para>
    /// 【fail-open 红线】所有早退/异常/门禁拒绝都必须调 <paramref name="reveal"/>——
    /// 窗口绝不能停在 Opacity=0（调用方另有一道 1.5s 失败保险）。
    /// </summary>
    public void PlayOpenForSelf(IntPtr hwnd, Action reveal)
    {
        if (reveal is null) return;
        if (!IsRunning || hwnd == IntPtr.Zero) { reveal(); return; }
        try
        {
            if (!Admit(hwnd, AnimKind.Open, "SELF_OPEN", out var durationMs, out _))
            {
                reveal();
                return;
            }
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                _policy.Complete(AnimKind.Open);
                Logger.Info(AnimReport.Skipped("SELF_OPEN", "无", "读窗口矩形失败"));
                reveal();
                return;
            }
            var target = ToRect(rect);
            if (target.Width < MinShellSize || target.Height < MinShellSize)
            {
                _policy.Complete(AnimKind.Open);
                Logger.Info(AnimReport.Skipped("SELF_OPEN", "无", "窗口过小"));
                reveal();
                return;
            }
            var image = GenieFrameSource.MakeOpaque(WindowCapture.Capture(hwnd, out var layer));
            if (image is null)
            {
                _policy.Complete(AnimKind.Open);
                Logger.Info(AnimReport.Skipped("SELF_OPEN", layer, "抓帧不可用"));
                reveal();
                return;
            }
            var p = AnimParamsStore.Current;
            var dur = durationMs > 0 ? durationMs : p.Scaled(p.OpenDurationMs);
            var startScale = Math.Clamp(p.OpenStartScale, 0.5, 1.0);
            _pendingReveal = reveal;   // RunClock 收尾统一回调（异常路径也会走到 Finish）
            LogGeometry("SELF_OPEN", layer, hwnd, target, Rect.Empty);
            StartGhost(image, target, dur, t =>
            {
                var e = 1.0 - Math.Pow(1.0 - t, 3.0);   // easeOutCubic：与 PlayOpen 同一开窗手感
                var scale = startScale + (1.0 - startScale) * e;
                var w = target.Width * scale;
                var h = target.Height * scale;
                var r = new GenieRect(
                    (target.Width - w) / 2 / target.Width,
                    (target.Height - h) / 2 / target.Height,
                    w / target.Width, h / target.Height);
                return new List<(GenieRect, double)> { (r, e) };
            }, "SELF_OPEN", "open", layer, 0);
        }
        catch (Exception ex)
        {
            _pendingReveal = null;
            Logger.Warn($"自家开窗动画失败（直接现身）：{ex.GetType().Name} {ex.Message}");
            reveal();
        }
    }

    /// <summary>
    /// 关闭退场（v2.2 §4 关闭项，alpha.27）：默认关（macOS 忠实，README 写明这不是没做完）；
    /// <c>WindowCloseAnimation=true</c> 时，第三方窗口销毁（OBJECT_DESTROY）用<b>前台缓存帧</b>
    /// 在原位播 150ms 的 fade+scale→92%（缓存龄超过 <see cref="AnimParams.CloseCacheMaxAgeMs"/> 不播）。
    /// <para>
    /// 【为什么只能用缓存帧】DESTROY 事件到手时窗口已经销毁：hwnd 失效，实时抓帧物理上不可能，
    /// 矩形也只有"最后已知非图标矩形"表可信。所以画面/几何/类名/进程号全部来自生前留下的表，
    /// 任一缺项都诚实跳过（判废可见化）。
    /// </para>
    /// <para>
    /// 【为什么设置未开时一行日志都不留】DESTROY 对菜单/提示窗/托盘浮层非常频繁；功能没开
    /// 就没有"尝试"，写"未播"行只是刷屏。开了之后才按判废纪律逐条留痕。
    /// </para>
    /// </summary>
    private void PlayClose(IntPtr hwnd)
    {
        // 默认关：静默返回（见方法注释——不判废、不记日志、不碰任何表）
        if (!SettingsService.Current.WindowCloseAnimation) return;

        // 身份表没有这个窗口：菜单/提示窗一类（从未被记为"正经窗口"），静默跳过
        if (!_identityByHwnd.TryGetValue(hwnd, out var identity)) return;

        // 高频销毁窗型（菜单/提示/影子/输入法/系统 shell 面）永远不进关闭动画，连日志都不留
        var cls = identity.Class ?? "";
        if (cls is "#32768" or "Tooltips_Class32" or "SysShadow" or "Default IME" or "MSCTFIME UI"
            or "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "Progman" or "WorkerW") return;

        // 几何：最后已知非图标矩形。销毁后 GetWindowRect 只有 (-32000) 哨兵，不能信
        if (!_lastGoodRect.TryGetValue(hwnd, out var rectRaw))
        {
            Logger.Info(AnimReport.Skipped("OBJECT_DESTROY", "无", "无最后已知矩形", $"类={cls}"));
            return;
        }
        var target = ToRect(rectRaw);
        if (target.Width < MinShellSize || target.Height < MinShellSize)
        {
            Logger.Info(AnimReport.Skipped("OBJECT_DESTROY", "无", "窗口过小", $"类={cls}"));
            return;
        }

        if (!Admit(hwnd, AnimKind.Close, "OBJECT_DESTROY", out var durationMs, out _))
            return;   // 门禁拒绝：自然消失（降级安全）

        // 画面：前台缓存帧（龄口径比最小化宽：销毁事件在系统队列里又晚了一拍）
        var p = AnimParamsStore.Current;
        if (!_frames.TryGet(hwnd, p.CloseCacheMaxAgeMs, out var image, out var frameRect, out var ageMs))
        {
            _policy.Complete(AnimKind.Close);
            Logger.Info(AnimReport.Skipped("OBJECT_DESTROY", "缓存", "缓存无帧或帧龄超限",
                $"类={cls} 上限={p.CloseCacheMaxAgeMs}ms"));
            return;
        }
        var composed = GenieFrameSource.MakeOpaque(image);
        if (composed is null)
        {
            _policy.Complete(AnimKind.Close);
            Logger.Info(AnimReport.Skipped("OBJECT_DESTROY", "缓存", "帧整理失败", $"类={cls}"));
            return;
        }

        var layer = $"缓存 {ageMs:0}ms";
        // 几何以帧矩形为准（画面与矩形是同一时刻抓的）；帧矩形不可信时退最后已知矩形
        var source = frameRect.Width >= MinShellSize && frameRect.Height >= MinShellSize ? frameRect : target;
        var dur = durationMs > 0 ? durationMs : p.Scaled(p.CloseDurationMs);
        var endScale = Math.Clamp(p.CloseEndScale, 0.5, 1.0);
        LogGeometry("OBJECT_DESTROY", layer, hwnd, source, Rect.Empty);
        StartGhost(composed, source, dur, t =>
        {
            var f = CloseAnimMath.Sample(t, endScale);
            var r = CloseAnimMath.RectFor(f.Scale);
            return new List<(GenieRect, double)> { (r, f.Opacity) };
        }, "OBJECT_DESTROY", "close", layer, ageMs);
    }

    /// <summary>
    /// 该窗口最近一帧画面（Dock 最小化窗口缩略图用）。不设龄上限：窗口最小化后帧不再更新，
    /// 拿到的就是"离开时的画面"——macOS 缩略图语义正是如此。无帧返回 null（调用方退应用图标）。
    /// </summary>
    public BitmapSource? LastFrameFor(IntPtr hwnd)
    {
        if (!IsRunning || hwnd == IntPtr.Zero) return null;
        // maxAgeMs=int.MaxValue：不设龄上限，窗口最小化后拿到的就是"离开时的画面"
        return _frames.TryGet(hwnd, int.MaxValue, out var frame, out _, out _)
            ? frame
            : null;
    }

    /// <summary>
    /// 还原方向：真窗口此刻已回到原位但仍处于斗篷下（最小化时我们斗的，一直没解），
    /// 替身从图标那一行"流出来"长回原尺寸，放完解斗篷换真窗口——用户全程看不见
    /// "窗口先弹出来再演动画"的那一帧（alpha.20 反复被抱怨的点，本轮根治）。
    /// 若斗篷当年没打上（DWM 拒绝/降级），这里退化为旧行为：窗口直接可见，替身叠加播放。
    /// </summary>
    private void PlayRestore(IntPtr hwnd)
    {
        if (!Admit(hwnd, AnimKind.Restore, "MINIMIZEEND", out var durationMs, out _))
        {
            _guard?.Uncloak(hwnd, "还原被门禁拒绝");   // 没动画可播就必须放窗口出来
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            _guard?.Uncloak(hwnd, "还原读矩形失败");
            _policy.Complete(AnimKind.Restore);
            Logger.Info(AnimReport.Skipped("MINIMIZEEND", "无", "读窗口矩形失败"));
            return;
        }
        var sourceRect = ToRect(rect);
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            _guard?.Uncloak(hwnd, "还原窗口矩形为零");
            _policy.Complete(AnimKind.Restore);
            Logger.Info(AnimReport.Skipped("MINIMIZEEND", "无", "窗口矩形为零"));
            return;
        }
        // 还原后的矩形是新的可信几何：顺手进表（tile 宽高比来源）
        _lastGoodRect.Remove(hwnd);
        _lastGoodRect.Add(hwnd, rect);

        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        // ── alpha.26 新引擎：从 Dock tile 长回原尺寸（用户点名的"还原其实根本就没有"就修这条）──
        LogGeometry("MINIMIZEEND", "DWM实况", hwnd, sourceRect, icon);
        if (TryPlayLive(hwnd, sourceRect, icon, durationMs, reverse: true, "MINIMIZEEND", 0))
            return;

        // ── 退路：alpha.25 图像链路 ──
        // 斗篷窗口照常被 PrintWindow 抓到（内容仍在合成），且此刻矩形已有效：拿到的就是"即将现身"的画面
        var image = GenieFrameSource.MakeOpaque(Capture(hwnd, "MINIMIZEEND"));
        if (image is null)
        {
            _guard?.Uncloak(hwnd, "还原无帧");
            _policy.Complete(AnimKind.Restore);
            return;
        }
        var layer = _lastLayer;
        _frames.Put(hwnd, image, sourceRect);

        LogGeometry("MINIMIZEEND", layer, hwnd, sourceRect, icon);
        // 【alpha.25 遮挡重播】不再藏窗：替身第一帧 = 原矩形全尺寸不透明画面，原生
        // "从任务栏长回来"的动画被替身压在底下；放完替身关闭，真窗口就在原地。
        StartGenie(image, sourceRect, icon, durationMs, reverse: true, "MINIMIZEEND", layer, 0);
    }

    /// <summary>按设置选效果：genie（灯罩网格/条带）、zoom（缩放淡出）、suck（更紧的吸吮）。</summary>
    private void StartGenie(BitmapSource image, Rect windowRect, Rect iconRect, int durationMs, bool reverse,
        string source, string layer, double ageMs)
    {
        var effect = SettingsService.Current.MinimizeEffect;
        var engine = EffectiveEngine(AnimParamsStore.Current.Engine);

        // native 场景渲染器尚未提供动画宿主（BN_Anim_* 未实现）：如实回退 mesh，日志写生效值
        if (effect == "zoom")
        {
            StartZoomEffect(image, windowRect, iconRect, durationMs, reverse, source, layer, ageMs);
            return;
        }
        if (effect == "suck")
        {
            StartStripGenie(image, windowRect, iconRect, durationMs, reverse, 0.15, source, layer, engine, ageMs);
            return;
        }
        if (engine == "strips")
        {
            StartStripGenie(image, windowRect, iconRect, durationMs, reverse, GenieMath.DefaultLag,
                source, layer, engine, ageMs);
            return;
        }
        StartMesh(image, ToLamp(windowRect), ToLamp(iconRect), durationMs, reverse, source, layer, ageMs);
    }

    // __PART2__

    /// <summary>
    /// zoom：整窗向图标方向缩成一个点并淡出。当前用源窗口内的中心缩放（避免被 2D 幽灵窗口的
    /// 画布裁切）；Dock 图标方向的精确位移等 native 场景渲染器接管后再补。
    /// </summary>
    private void StartZoomEffect(BitmapSource image, Rect windowRect, Rect iconRect, int durationMs, bool reverse,
        string source, string layer, double ageMs)
    {
        if (windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            _policy.Complete(AnimKind.Minimize);
            return;
        }
        var cx = Math.Clamp((iconRect.X + iconRect.Width / 2 - windowRect.Left) / windowRect.Width, 0.15, 0.85);
        var cy = Math.Clamp((iconRect.Y + iconRect.Height / 2 - windowRect.Top) / windowRect.Height, 0.15, 0.85);
        var target = new GenieRect(cx - 0.05, cy - 0.05, 0.1, 0.1);

        StartGhost(image, windowRect, durationMs, t =>
        {
            // v3.0：morph 曲线按 v2.2 §5 用 easeInOutQuad（原来是线性）
            var p = reverse ? 1.0 - t : t;
            var e = AnimCurve.EaseInOutQuad(p);
            var r = GenieMath.ZoomLerp(new GenieRect(0, 0, 1, 1), target, e);
            // 替身全程不透明：缩放本身就是完整的视觉收缩，再叠 35% 淡出只会让画面"发虚透明"
            // （用户实测反馈）。替身消失由 Finish 的窗口关闭一次性完成，与 mesh 引擎口径一致。
            return new List<(GenieRect, double)> { (r, 1.0) };
        }, source, "zoom", layer, ageMs);
    }

    /// <summary>
    /// 兜底引擎：把窗口切成水平条带，整体向图标收拢。
    /// 形状比灯罩网格差一档（没有"脖子"，越靠下的行不会额外多收一些），
    /// 但它只用 2D 绘制，在任何渲染环境下都画得出来。
    /// </summary>
    private void StartStripGenie(BitmapSource image, Rect source, Rect icon, int durationMs, bool reverse,
        double lag, string sourceName, string layer, string engine, double ageMs)
    {
        var scaleX = (icon.X + icon.Width / 2 - (source.Left + source.Width / 2)) / source.Width;
        var scaleY = (icon.Y - source.Top) / source.Height;
        var endWidth = Math.Clamp(source.Width > 0 ? icon.Width / source.Width : EndWidthRatio, 0.04, 0.6);

        StartGhost(image, source, durationMs, t =>
        {
            var p = reverse ? 1.0 - t : t;
            return GenieMath.Sample(p, GenieMath.DefaultSlices, lag,
                        0.5 + scaleX, 1.0 + scaleY, endWidth)
                    .Select(b => (b.Rect, b.Opacity)).ToList();
        }, sourceName, engine, layer, ageMs);
    }

    /// <summary>
    /// 找不到 Dock 条目时的退路：收到条带上沿的中央，宽度取窗口宽的 1/8。
    /// 不是"不做"——"窗口凭空消失"正是用户抱怨的点，给一条连续的运动线索比什么都不画值钱。
    /// </summary>
    private static Rect IconRectFor(Rect source, Rect? dockRect)
    {
        if (dockRect is { } d && d.Width > 0) return d;
        var fallbackWidth = Math.Max(24.0, source.Width / 8.0);
        return new Rect(
            source.Left + source.Width / 2 - fallbackWidth / 2,
            source.Bottom + 40,
            fallbackWidth, 0);
    }

    private static LampGeometry ToLamp(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// 几何三方对账（v3.0 §Q2-4）：源窗口 <c>GetWindowRect</c> / DWM 可见矩形 / Dock 图标物理矩形。
    /// <b>每一帧动画都要有这一行</b>：DPI 倍率错位与图标落点错位在这里是"能被读出来的数字"，
    /// 而不是"看着有点偏"。
    /// </summary>
    private void LogGeometry(string source, string layer, IntPtr hwnd, Rect sourceRect, Rect iconRect)
    {
        var srcAnim = new AnimRect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height);
        var dwm = srcAnim;
        if (NativeMethods.DwmGetRectAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds, out var ext,
                Marshal.SizeOf<RECT>()) == 0 && ext.Right > ext.Left && ext.Bottom > ext.Top)
            dwm = AnimRect.FromLtrb(ext.Left, ext.Top, ext.Right, ext.Bottom);
        var icon = new AnimRect(iconRect.X, iconRect.Y, iconRect.Width, Math.Max(1, iconRect.Height));
        Logger.Info(AnimReport.Geometry($"{source}/{layer}", srcAnim, dwm, icon, null));
    }

    /// <summary>
    /// 用纹理网格跑一版 genie。<paramref name="reverse"/> = 还原方向（进度从 1 倒放到 0）。
    /// </summary>
    private void StartMesh(BitmapSource image, LampGeometry windowRect, LampGeometry iconRect,
        int durationMs, bool reverse, string source, string layer, double ageMs)
    {
        GenieMeshGhost? ghost = null;
        try
        {
            ghost = new GenieMeshGhost(image);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            // 3D 渲染面建不起来（无硬件加速的极端环境等）：按红线回退 strips，而不是"这次不动画"。
            // 回退是可观测的：日志里引擎值变成 strips，用户在 AnimLab 里也能看到。
            Logger.Warn($"genie 网格渲染不可用，回退条带引擎：{ex.GetType().Name}");
            var stripSource = new Rect(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height);
            var stripIcon = new Rect(iconRect.X, iconRect.Y, iconRect.Width, Math.Max(1, iconRect.Height));
            StartStripGenie(image, stripSource, stripIcon, durationMs, reverse, GenieMath.DefaultLag,
                source, layer, "strips", ageMs);
            return;
        }

        _meshGhost = ghost;
        ghost.Apply(reverse ? 1.0 : 0.0, windowRect, iconRect);
        ghost.Show();
        ghost.Place();

        RunClock(durationMs, source, "mesh", layer, ageMs,
            t => ghost.Apply(reverse ? 1.0 - t : t, windowRect, iconRect),
            () =>
            {
                _meshGhost = null;
                ghost.Dispose();
            });
    }

    /// <summary>
    /// vsync 驱动的计时器：进度按真实经过时间算，与帧率无关（裁定：动画必须帧率无关）。
    /// <para>
    /// v3.0 起顺带统计帧间隔：收尾时把"帧数 / P50 / P95 / max"写进 <c>动画触发</c> 行。
    /// 没有这组数字，"动画卡不卡"只能靠肉眼，而肉眼对 20ms 与 45ms 的差别并不可靠。
    /// </para>
    /// </summary>
    private void RunClock(int durationMs, string source, string engine, string layer, double ageMs,
        Action<double> onFrame, Action onDone)
    {
        var clock = Stopwatch.StartNew();
        var intervals = new List<double>(64);
        var lastFrameMs = 0.0;
        var frames = 0;

        EventHandler? tick = null;
        void Finish()
        {
            if (tick is not null) CompositionTarget.Rendering -= tick;
            var stats = FrameStats.FromIntervals(intervals);
            Logger.Info(AnimReport.Trigger(source, layer, engine, durationMs, Math.Max(frames, stats.Frames),
                stats.P95 > 0 ? stats.P95 : 0));
            if (ageMs > 0) Logger.Info($"动画帧来源：缓存帧龄={ageMs:0}ms");
            try { onDone(); }
            finally
            {
                // 槽必须释放：无论正常收尾还是异常中止，否则几轮之后全部被"并发上限"挡住
                _policy.Complete(_activeKind);
                // 斗篷代管的还原/最大化/打开动画：真窗口在这里才现身（异常/提前终止也会走到 Finish，不会漏）
                var uncloak = _pendingUncloak;
                _pendingUncloak = IntPtr.Zero;
                if (uncloak != IntPtr.Zero) _guard?.Uncloak(uncloak, "动画收尾");
                // 自家开窗动画（PlayOpenForSelf）的现身回调：替身收尾真窗口才亮出来
                var reveal = _pendingReveal;
                _pendingReveal = null;
                reveal?.Invoke();
            }
        }

        tick = (_, _) =>
        {
            try
            {
                var elapsed = clock.Elapsed.TotalMilliseconds;
                var t = elapsed / durationMs;
                frames++;
                if (lastFrameMs > 0) intervals.Add(elapsed - lastFrameMs);
                lastFrameMs = elapsed;

                if (t >= 1.0)
                {
                    onFrame(1.0);
                    Finish();
                    return;
                }
                onFrame(t);
            }
            catch (Exception ex)
            {
                // 渲染回调抛出去会让整个 UI 线程的渲染停摆：立刻收摊，动画可以没有
                Logger.Warn($"genie 动画异常终止（渲染侧已降级）：{ex.GetType().Name} {ex.Message}");
                try { Finish(); } catch { /* 收尾也不能抛 */ }
            }
        };
        CompositionTarget.Rendering += tick;
    }

    /// <summary>
    /// 最大化/还原：替身在两态矩形的并集画布上连续变形（内容随窗口一起长/缩，macOS 同款）。
    /// <para>
    /// 斗篷链路：LOCATIONCHANGE 检测到跳变时已同步斗篷（真窗口瞬间到位但隐形），这里抓
    /// <b>新态</b>的画面做替身、从旧矩形长到新矩形，收尾解斗篷换真窗口——原生动画已由
    /// FORCEDISABLED 关掉，替身是唯一动画，不会再打架。
    /// </para>
    /// </summary>
    private void PlayZoom(IntPtr hwnd, RECT fromRect, RECT toRect, bool zoomIn)
    {
        if (!Admit(hwnd, AnimKind.Maximize, "LOCATIONCHANGE", out var durationMs, out _))
        {
            _guard?.Uncloak(hwnd, "最大化被门禁拒绝");
            return;
        }

        var from = ToRect(fromRect);
        var to = ToRect(toRect);
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0)
        {
            _guard?.Uncloak(hwnd, "最大化矩形无效");
            _policy.Complete(AnimKind.Maximize);
            Logger.Info(AnimReport.Skipped("LOCATIONCHANGE", "无", "新旧矩形无效"));
            return;
        }

        var image = GenieFrameSource.MakeOpaque(Capture(hwnd, "LOCATIONCHANGE"));
        if (image is null)
        {
            _guard?.Uncloak(hwnd, "最大化无帧");
            _policy.Complete(AnimKind.Maximize);
            return;
        }
        var layer = _lastLayer;

        // 画布 = 两态的并集：无论放大还是缩小，替身条带都落在画布内（WPF 窗口外内容不可见）
        var canvas = Rect.Union(from, to);
        var fromN = new GenieRect(
            (from.Left - canvas.Left) / canvas.Width, (from.Top - canvas.Top) / canvas.Height,
            from.Width / canvas.Width, from.Height / canvas.Height);
        var toN = new GenieRect(
            (to.Left - canvas.Left) / canvas.Width, (to.Top - canvas.Top) / canvas.Height,
            to.Width / canvas.Width, to.Height / canvas.Height);

        _pendingUncloak = hwnd;
        StartGhost(image, canvas, durationMs, t =>
        {
            // v3.0：morph 曲线按 v2.2 §5 用 easeInOutQuad（原来是线性）；zoomIn 决定插值方向
            var e = AnimCurve.EaseInOutQuad(zoomIn ? t : 1.0 - t);
            var r = GenieMath.ZoomLerp(fromN, toN, e);
            return new List<(GenieRect, double)> { (r, 1.0) };
        }, "LOCATIONCHANGE", "morph", layer, 0);
    }

    private void StartGhost(BitmapSource image, Rect screenPhysical, int durationMs,
        Func<double, List<(GenieRect Rect, double Opacity)>> sampler,
        string source, string engine, string layer, double ageMs)
    {
        var ghost = new GhostLayer(image, GenieMath.DefaultSlices, screenPhysical);
        _ghost = ghost;
        ghost.Apply(sampler(0));   // alpha.25：先画好第一帧再 Show——替身落地即全覆盖（遮挡重播的前提）
        ghost.Show();
        RunClock(durationMs, source, engine, layer, ageMs, t => ghost.Apply(sampler(t)), CloseGhost);
    }

    private void CloseGhost()
    {
        if (_ghost is null) return;
        var ghost = _ghost;
        _ghost = null;
        try { ghost.Close(); }
        catch (InvalidOperationException) { /* 已被关掉：动画是尽力而为的东西 */ }
    }

    /// <summary>两条动画共用"同一时刻只有一个替身"的闸门：网格、条带与 DWM 实况谁在跑都算占用。</summary>
    private bool Busy => _ghost is not null || _meshGhost is not null || _live is { IsActive: true };

    /// <summary>
    /// 该窗口最后已知的非图标物理矩形（Dock 最小化 tile 的宽高比来源；无样本返回 null）。
    /// alpha.26 起 GetWindowRect 在窗口 iconic 后只有 (-32000) 哨兵，tile 等比缩略图只认这张表。
    /// </summary>
    public Rect? LastKnownRectPhysical(IntPtr hwnd)
        => _lastGoodRect.TryGetValue(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top
            ? ToRect(r)
            : null;

    /// <summary>
    /// alpha.26 新引擎入口：<b>DWM 实况缩略图 genie</b>。true = 已接管本次动画（RunClock 驱动）；
    /// false = 通道不可用（连续失败 3 次自动停用），调用方走旧图像链路。
    /// <para>
    /// 【为什么它能修 26H2 的三件事】替身像素由 DWM 合成器直接采样源窗口表面：
    /// ① 不再抓帧 → <c>抓帧不可用/纯色判废</c> 整条故障面消失；② 不再预渲染 →
    /// mesh 那 ~500ms 起播延迟消失，事件到手即起播；③ tile 与动画同一通道 →
    /// 缩略图与 genie 内容天然一致，最小化/还原/条目三处观感对齐。
    /// </para>
    /// </summary>
    private bool TryPlayLive(IntPtr hwnd, Rect sourceRect, Rect iconRect, int durationMs, bool reverse,
        string source, double ageMs)
    {
        if (_liveFailures >= 3) return false;
        try
        {
            _live ??= new DwmLiveGhost();
            if (!_live.Begin(hwnd, sourceRect, iconRect))
            {
                _liveFailures++;
                if (_liveFailures >= 3)
                    Logger.Warn("DWM 实况 genie 连续 3 次建不起来，本会话停用（退回图像链路）");
                return false;
            }
        }
        catch (Exception ex)
        {
            _liveFailures++;
            Logger.Warn($"DWM 实况 genie 异常（退回图像链路）：{ex.GetType().Name} {ex.Message}");
            return false;
        }

        RunClock(durationMs, source, "dwmlive", "DWM实况", ageMs,
            t => _live?.Apply(t, reverse),
            () => _live?.Finish());
        return true;
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    // ---------- 前台帧采样 ----------

    /// <summary>
    /// 前台帧采样器：每 <see cref="AnimParams.ForegroundRefreshMs"/> 至多一张（2Hz），全屏期间零轮询。
    /// <para>
    /// 定时器只做节流判断，真正的 <c>PrintWindow</c> 丢给线程池：抓一张 4K 前台窗口要几十毫秒，
    /// 留在 UI 线程上每秒两次，等于每秒抽掉三四帧——Dock 悬停与动画都会发涩。
    /// </para>
    /// </summary>
    private void StartFrameSampler()
    {
        if (!IsRunning) return;
        var ownPid = (uint)Environment.ProcessId;
        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _sampleTimer.Tick += (_, _) =>
        {
            try
            {
                var fullscreen = IsFullscreenForeground();
                _ = Task.Run(() =>
                {
                    try { _frames.SampleForeground(ownPid, fullscreen: fullscreen); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                                  or ExternalException or OutOfMemoryException)
                    {
                        Logger.Warn($"前台取样异常（已隔离，不影响动画）：{ex.Message}");
                    }
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException
                                          or OutOfMemoryException)
            {
                Logger.Warn($"前台取样调度异常（已隔离）：{ex.Message}");
            }
        };
        _sampleTimer.Start();
        Logger.Info("前台帧采样：已启动（2Hz，全屏期间零轮询）");
    }

    public void Dispose()
    {
        _sampleTimer?.Stop();
        _sampleTimer = null;
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        if (_showHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_showHook);
        if (_destroyHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_destroyHook);
        _minimizeHook = _locationHook = _showHook = _destroyHook = IntPtr.Zero;
        _proc = null;
        _lastRect.Clear();
        _identityByHwnd.Clear();
        _processNames.Clear();
        _openAttempted.Clear();
        _lastGoodRect.Clear();
        _frames.Clear();
        _live?.Dispose();
        _live = null;
        _meshGhost?.Dispose();
        _meshGhost = null;
        CloseGhost();
    }

    /// <summary>
    /// 替身窗口：与源窗口完全重合的分层窗口，里面是 N 条带 Rectangle。
    /// <para>
    /// 落位一律走 <see cref="NativeMethods.SetWindowPos"/> 的<b>物理像素</b>，不设 Left/Top/Width/Height
    /// （WPF 那套是 DIP，跨 DPI 会把源矩形的位置算歪）。条带坐标由归一化值 × 客户区 DIP 尺寸得到。
    /// </para>
    /// </summary>
    private sealed class GhostLayer : Window
    {
        private readonly Canvas _canvas = new();
        private readonly Rectangle[] _bands;
        private readonly int _physicalWidth;
        private readonly int _physicalHeight;

        /// <summary>客户区的 DIP 尺寸：物理像素 ÷ 本窗口 DPI。落位前为 0，Apply 会跳过。</summary>
        private double _widthDip;
        private double _heightDip;

        public GhostLayer(BitmapSource image, int bandCount, Rect physicalBounds)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = null;                 // 未绘制区域不参与命中测试：替身期间用户照样能点穿到下面的桌面
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            Content = _canvas;

            _physicalWidth = Math.Max(1, (int)physicalBounds.Width);
            _physicalHeight = Math.Max(1, (int)physicalBounds.Height);

            var bandHeight = 1.0 / bandCount;
            _bands = new Rectangle[bandCount];
            for (var i = 0; i < bandCount; i++)
            {
                var band = new Rectangle
                {
                    Width = _physicalWidth,
                    Height = Math.Max(1, bandHeight * _physicalHeight),
                    Fill = new ImageBrush(image)
                    {
                        // 相对视口取自己那一条：Viewport=(0, i/n, 1, 1/n) 把整张图裁成第 i 段
                        Viewport = new Rect(0, i * bandHeight, 1, bandHeight),
                        ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                        Stretch = Stretch.Fill,
                        TileMode = TileMode.None,
                    },
                };
                _bands[i] = band;
                _canvas.Children.Add(band);
            }

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var scale = GetDpiForWindow(hwnd) / 96.0;
                if (scale <= 0) scale = 1.0;
                // 窗口尺寸用 SetWindowPos 按物理像素定，条带坐标在 DIP 里画，两者差一个 scale。
                // 高 DPI 屏上把物理像素当 DIP 用，替身会比真窗口大两倍——正是 v3 反复踩的那类坑。
                _widthDip = _physicalWidth / scale;
                _heightDip = _physicalHeight / scale;
                SetWindowPos(hwnd, IntPtr.Zero,
                    (int)physicalBounds.Left, (int)physicalBounds.Top, _physicalWidth, _physicalHeight,
                    SwpNozorder | SwpNoactivate);
            };
        }

        /// <summary>每帧：把归一化矩形换算成 DIP 摆上去。</summary>
        public void Apply(List<(GenieRect Rect, double Opacity)> bands)
        {
            if (_widthDip <= 0 || _heightDip <= 0) return;   // 还没落位：这一帧不画，避免按物理像素画歪
            var count = Math.Min(bands.Count, _bands.Length);
            for (var i = 0; i < count; i++)
            {
                var (rect, opacity) = bands[i];
                var band = _bands[i];
                Canvas.SetLeft(band, rect.X * _widthDip);
                Canvas.SetTop(band, rect.Y * _heightDip);
                band.Width = Math.Max(0, rect.Width * _widthDip);
                band.Height = Math.Max(0, rect.Height * _heightDip);
                band.Opacity = opacity;
                band.Visibility = rect.Height <= 0 || rect.Width <= 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            for (var i = count; i < _bands.Length; i++) _bands[i].Visibility = Visibility.Collapsed;
        }
    }
}
