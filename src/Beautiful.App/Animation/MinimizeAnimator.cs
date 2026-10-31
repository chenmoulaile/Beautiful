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
/// 【为什么替身用分层窗口 + 条带 Rectangle】逐像素透明只有分层窗口这一条路（v2 已被系统背板证伪），
/// 而 genie 的弯曲感来自"每条带各自向目标收拢"，WPF 2D 没有网格变形，用 N 条带 + 各自的
/// <c>ImageBrush.Viewport</c> 是零第三方依赖下最接近原味的实现。几何全在
/// <see cref="GenieMath"/>（纯函数 + 单测），这里只负责每帧把它乘回像素。
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
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x8004;
    private const uint EventSystemForeground = 0x0003;
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
    private IntPtr _foregroundHook = IntPtr.Zero;

    private GhostLayer? _ghost;
    private GenieMeshGhost? _meshGhost;
    private bool _disabledByEnvironment;

    /// <summary>当前被 Cloak 的真窗口（最多一个，单替身闸门保证）：退出/收摊时必须 Uncloak，
    /// 否则真窗口会被永久藏起来（比动画本身严重得多的故障）。</summary>
    private IntPtr _cloakedWindow;

    /// <summary>前台帧缓存（兜底捕获层）。采样在后台线程，查询在 UI 线程。</summary>
    private readonly ForegroundFrameCache _frames = new();

    /// <summary>放行门禁（风暴/并发/节流/排除/全屏）。</summary>
    private readonly AnimPolicy _policy = new(AnimParams.Default);

    /// <summary>hwnd → 上一次的 (矩形, 是否最大化)。只为识别"从非最大化跳到最大化"那一下。</summary>
    private readonly Dictionary<IntPtr, (RECT Rect, bool Zoomed)> _lastRect = new();

    /// <summary>pid → 进程名。排除清单与日志都要它；每 3-4 分钟才有新 pid，缓存足够。</summary>
    private readonly Dictionary<uint, string> _processNames = new();

    /// <summary>已播过打开动画的窗口：只有"第一次显示"是打开，hide/show 回归不是（macOS 语义）。</summary>
    private readonly HashSet<IntPtr> _openAttempted = new();

    /// <summary>
    /// hwnd → pid 记账（关闭动画的排除/节流依据）：EVENT_OBJECT_DESTROY 到手时句柄已销毁，
    /// 查不到 pid；SHOW 是唯一能可靠记下它的时机。无论打开动画开没开都记（O(1)，不播也不亏）。
    /// </summary>
    private readonly Dictionary<IntPtr, uint> _knownPids = new();

    /// <summary>已报过"首窗出现"的 pid：Dock 启动弹跳联动每个进程只收口一次。</summary>
    private readonly HashSet<uint> _firstWindowSeenPids = new();

    /// <summary>
    /// CBT 钩子/静默路径已处理过的窗口 → 时间戳：这些窗口的 EVENT_SYSTEM_MINIMIZESTART
    /// 随后照样会到（我们自己调 ShowWindow 落位也会触发），不挡就是第二次 genie 叠在同一窗口上。
    /// 2 秒窗口期足够覆盖"genie 收尾 + 事件到达"的间隔。
    /// </summary>
    private readonly Dictionary<IntPtr, long> _hookMinimizedAt = new();

    /// <summary>首窗出现回调（DockWindow.NotifyFirstWindowShown；null = 无联动）。</summary>
    private readonly Action<string>? _firstWindowShown;

    private DispatcherTimer? _sampleTimer;
    private string _exclusionSnapshot = "";

    public MinimizeAnimator(Func<IntPtr, Rect?> dockTargetProvider, Action<string>? onFirstWindowShown = null)
    {
        _dockTarget = dockTargetProvider;
        _firstWindowShown = onFirstWindowShown;
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
        // 动画的正反两放，分开装两条钩子只会多一份句柄要管
        _minimizeHook = NativeMethods.SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _locationHook = NativeMethods.SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _showHook = NativeMethods.SetWinEventHook(
            EventObjectShow, EventObjectShow, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _destroyHook = NativeMethods.SetWinEventHook(
            EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        // alpha.15：前台变化即补采一帧（绕过 2Hz 轮询节流）。
        // 【为什么】事件路径到手时窗口已 iconic，画面只能靠缓存；而缓存只伺候 2Hz 前台采样，
        // "刚切到窗口就最小化"必然无帧不播（真机矩阵：caption 按钮两连未播即此因）。
        // 前台切换是"这个窗口接下来可能被最小化"的最强前兆，此刻抓一张性价比最高。
        _foregroundHook = NativeMethods.SetWinEventHook(
            EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);

        ApplyExclusions();
        // alpha.15 接线（两处"参数没接上"缺陷）：
        // ① 策略时长从构造时快照改为跟随热加载（改 anim-params.json 即时生效）；
        // ② 设置页"动画速度"滑条（AnimationSpeed）与参数表倍率相乘后进策略——
        //    alpha.14 里这个滑条没有任何消费方，拖到 0.5× 也不变慢。
        AnimParamsStore.ParamsChanged += OnParamsChanged;
        OnParamsChanged(AnimParamsStore.Current);
        BuildMountStatus();
        StartFrameSampler();
    }

    /// <summary>热加载/保存 → 重新合成参数推进策略。AnimSpeed 语义与 SpeedMultiplier 同向（越大越快）。</summary>
    private void OnParamsChanged(AnimParams parameters)
    {
        var speed = Math.Clamp(SettingsService.Current.AnimationSpeed, 0.5, 2.0);
        _policy.UpdateParams(parameters with { SpeedMultiplier = parameters.SpeedMultiplier * speed });
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
        var foreOk = _foregroundHook != IntPtr.Zero;

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
        if (!showOk) degraded.Add("OBJECTSHOW 钩子失败（打开动画不可用）");
        if (!destroyOk) degraded.Add("OBJECTDESTROY 钩子失败（关闭动画不可用）");
        if (!foreOk) degraded.Add("FOREGROUND 钩子失败（事件路径最小化命中率降低）");
        if (!s.MinimizeAnimation) degraded.Add("设置项 MinimizeAnimation 为关");
        if (!s.OpenAnimation) degraded.Add("设置项 OpenAnimation 为关");
        if (effectiveEngine != requestedEngine) degraded.Add($"引擎 {requestedEngine} 不可用已回退 {effectiveEngine}");

        MountStatus = $"动画子系统已挂载：MINIMIZE=0x{_minimizeHook.ToInt64():X} "
                      + $"LOCATION={(locOk ? $"0x{_locationHook.ToInt64():X}" : "失败")} "
                      + $"SHOW={(showOk ? $"0x{_showHook.ToInt64():X}" : "失败")} "
                      + $"DESTROY={(destroyOk ? $"0x{_destroyHook.ToInt64():X}" : "失败")} "
                      + $"FOREGROUND={(foreOk ? $"0x{_foregroundHook.ToInt64():X}" : "失败")} "
                      + $"设置=开 打开={(s.OpenAnimation ? "开" : "关")} 关闭={(s.CloseAnimation ? "开" : "关")} "
                      + $"请求引擎={requestedEngine} 生效引擎={effectiveEngine} "
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
    /// 原生回调：只许做"取出参数 + 投递"两件事。任何异常逃逸出去都会打断消息泵，
    /// 任何阻塞都会让系统认为我们不该收事件（键盘钩子还会被静默摘掉，同一类事故）。
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (idObject != (int)ObjidWindow || idChild != 0) return;   // 只关心顶层窗口本身，不管子元素

            if (evt == EventSystemForeground)
            {
                // 只做投递：真正的抓帧在线程池（PrintWindow 阻塞几十 ms，原生回调里绝不许）
                _dispatcher.BeginInvoke(() => CaptureForegroundNow(hwnd), DispatcherPriority.Background);
                return;
            }
            if (evt == EventSystemMinimizeStart)
            {
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
                // 真正可能耗时的动画只在"非最大化 → 最大化"那一次跳变上触发。
                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
                var zoomed = NativeMethods.IsZoomed(hwnd);
                if (_lastRect.TryGetValue(hwnd, out var previous))
                {
                    if (!previous.Zoomed && zoomed && IsBigJump(previous.Rect, rect))
                        _dispatcher.BeginInvoke(() => PlayZoom(hwnd, previous.Rect, rect), DispatcherPriority.Render);
                }
                else if (_lastRect.Count > 512)
                {
                    _lastRect.Clear();   // 只缓存"见过的窗口"，超上限整体重来，不做 LRU 的复杂度
                }
                _lastRect[hwnd] = (rect, zoomed);
            }
            else if (evt == EventObjectShow)
            {
                _dispatcher.BeginInvoke(() => PlayOpen(hwnd), DispatcherPriority.Render);
            }
            else if (evt == EventObjectDestroy)
            {
                _dispatcher.BeginInvoke(() => PlayClose(hwnd), DispatcherPriority.Render);
            }
        }
        catch
        {
            // 原生回调里连日志都可能抛（Logger 要写文件）：静默吞掉是这里唯一正确的选择
        }
    }

    private static bool IsBigJump(RECT from, RECT to)
    {
        var fw = from.Right - from.Left;
        var fh = from.Bottom - from.Top;
        var tw = to.Right - to.Left;
        var th = to.Bottom - to.Top;
        return fw > 0 && fh > 0 && tw > fw * 1.2 && th > fh * 1.2;
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
    private bool Admit(IntPtr hwnd, AnimKind kind, string source, out int durationMs, out uint pid, uint knownPid = 0)
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
        // 关闭路径的句柄已销毁：GetWindowThreadProcessId 返回 0，改用 SHOW 时记账的 pid
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out pid) == 0 && knownPid != 0) pid = knownPid;
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

    /// <summary>最小化：抓一帧（或取缓存帧）盖在原位，再把替身收进该应用在 Dock 上的图标。</summary>
    private void PlayMinimize(IntPtr hwnd, string source)
    {
        // CBT 钩子/静默路径已真替换过的事件不再补播（防双播）
        if (_hookMinimizedAt.TryGetValue(hwnd, out var handledAt) && NowMs() - handledAt < 2000)
        {
            Logger.Info($"动画未播：源={source} 层=无 因=钩子路径已真替换（防双播）");
            return;
        }

        if (!Admit(hwnd, AnimKind.Minimize, source, out var durationMs, out _)) return;

        var p = AnimParamsStore.Current;

        if (NativeMethods.IsIconic(hwnd))
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
                StartGenie(cached, cachedRect, icon, durationMs, reverse: false, source, "缓存", age);
                return;
            }

            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "缓存", "到手时已最小化且缓存帧过期/缺失",
                $"缓存条数={_frames.Count}"));
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "读窗口矩形失败"));
            return;
        }
        var sourceRect = ToRect(rect);
        if (sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize)
        {
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "窗口过小", $"{sourceRect.Width:0}×{sourceRect.Height:0}"));
            return;
        }

        var image = Capture(hwnd, source);
        if (image is null)
        {
            _policy.Complete(AnimKind.Minimize);
            return;
        }
        var layer = _lastLayer;
        _frames.Put(hwnd, image, sourceRect);   // 留一份：最小化后悬停预览与后续补播都靠它

        var iconRect = IconRectFor(sourceRect, _dockTarget(hwnd));
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
        var image = WindowCapture.Capture(hwnd, out var layer);
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
        if (!Admit(hwnd, AnimKind.Minimize, "Dock", out var durationMs, out _)) return;
        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        StartGenie(image, sourceRect, icon, durationMs, reverse: false, "Dock", "预抓帧", 0);
    }

    /// <summary>
    /// CBT 钩子路径的真替换入口：注入侧已拦下原生最小化并交帧，本方法<b>全权接管</b>——
    /// 门禁放行就播 genie，播完静默落位；门禁拒绝/无帧也必须静默落位（原生最小化已被拦，
    /// 我们不落位窗口就缩不下去，比没有动画严重得多）。
    /// </summary>
    public void PlayMinimizeFromHook(IntPtr hwnd, BitmapSource? image, Rect sourceRect)
    {
        var durationMs = 0;
        // alpha.18：钩子抓帧失败会送来"空帧槽"（image=null，Width=0）——原生最小化已被拦，
        // 我们必须有一个像样的收场。先试前台帧缓存（几分钟前刚在屏上的窗口大概率有帧），
        // 有新鲜缓存帧就照样播 genie；连缓存都没有才静默落位（无声收进任务栏，绝不播原生动画）。
        if (image is null && !NativeMethods.IsIconic(hwnd))
        {
            var p0 = AnimParamsStore.Current;
            if (_frames.TryGet(hwnd, p0.ForegroundCacheMaxAgeMs, out var cached, out var cachedRect, out var age))
            {
                Logger.Info($"CBT 钩子：空帧槽（钩子抓帧失败），改用前台缓存帧（龄 {age:0}ms）播 genie");
                image = cached;
                sourceRect = cachedRect;
            }
            else
            {
                Logger.Info($"CBT 钩子：空帧槽且无可用缓存帧（龄上限 {p0.ForegroundCacheMaxAgeMs}ms），静默落位");
            }
        }

        var canPlay = IsRunning
                      && image is not null
                      && sourceRect.Width >= MinShellSize && sourceRect.Height >= MinShellSize
                      && Admit(hwnd, AnimKind.Minimize, "CBT钩子", out durationMs, out _);
        if (canPlay)
        {
            var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
            LogGeometry("CBT钩子", "钩子帧", hwnd, sourceRect, icon);
            Logger.Info($"CBT 钩子接管：0x{hwnd.ToInt64():X} 已拦原生最小化，genie 真替换开播 "
                        + $"帧={image!.PixelWidth}×{image.PixelHeight}");
            _hookMinimizedAt[hwnd] = NowMs();
            StartGenie(image!, sourceRect, icon, durationMs, reverse: false, "CBT钩子", "钩子帧", 0,
                afterDone: () => SilentMinimize(hwnd, "genie 收尾落位"));
            return;
        }

        SilentMinimize(hwnd, "门禁拒绝/无帧（钩子已拦原生，必须补落位）");
    }

    /// <summary>
    /// 静默落位：关 DWM 过渡 → ShowWindow(SW_MINIMIZE) → 还过渡。
    /// 调用方必须已经接管这次最小化（钩子拦下原生 / Dock 主动路径），否则会出现"双重动画"。
    /// </summary>
    public void SilentMinimize(IntPtr hwnd, string reason)
    {
        try
        {
            _hookMinimizedAt[hwnd] = NowMs();   // 我们自己的落位也会触发 MINIMIZESTART，同样要防双播
            MinimizeHookClient.Suppress(hwnd);  // 钩子层放行：这是程序化最小化，不是用户动作
            var transitions = NativeMethods.DwmwaTransitionsForceDisabled;
            int on = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, transitions, ref on, sizeof(int));
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
            int off = 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, transitions, ref off, sizeof(int));
            Logger.Info($"最小化落位：0x{hwnd.ToInt64():X} 静默完成（{reason}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"静默落位异常（0x{hwnd.ToInt64():X}）：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            MinimizeHookClient.Suppress(IntPtr.Zero);
        }
    }

    /// <summary>
    /// 主动路径（Dock 图标点击/右键菜单/顶栏菜单/热角）共用的真替换最小化：
    /// 预抓帧（窗口还画着）→ 抑制钩子 + 关 DWM 过渡 → ShowWindow 静默落位 → genie（预抓帧）。
    /// 动画器不可用时回退系统最小化（原生动画照旧，功能不丢）。
    /// </summary>
    public void MinimizeNow(IntPtr hwnd)
    {
        if (!IsRunning || hwnd == IntPtr.Zero || NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
            return;
        }

        var (image, rect) = PreCapture(hwnd);
        MinimizeHookClient.Suppress(hwnd);
        try
        {
            var transitions = NativeMethods.DwmwaTransitionsForceDisabled;
            int on = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, transitions, ref on, sizeof(int));
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
            int off = 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, transitions, ref off, sizeof(int));
        }
        finally
        {
            MinimizeHookClient.Suppress(IntPtr.Zero);
        }

        if (image is not null && rect is { } r)
            PlayMinimizeFromCapture(hwnd, image, r);
        else
            Logger.Info($"动画未播：源=Dock 层=无 因=预抓失败（窗口已静默最小化，无双重动画）");
    }

    /// <summary>前台补采的在飞标志（0 = 空闲）：同一时刻至多一张，防 alt-tab 风暴叠抓。</summary>
    private IntPtr _foregroundCaptureInFlight;

    /// <summary>前台补采的绝对节流（单调毫秒）：连按 alt-tab 时至多 4 张/秒。</summary>
    private long _lastForegroundCaptureMs = long.MinValue;

    /// <summary>
    /// alpha.15：EVENT_SYSTEM_FOREGROUND 到手后的补采——绕过 2Hz 轮询的节流，
    /// 但保留三道闸：已有新鲜帧就跳过、绝对节流 250ms、全屏零捕获（与采样器同一纪律）。
    /// 【它救的是哪条命】标题栏按钮/Win+↓/任务栏三条最小化路径到手时窗口已 iconic，
    /// 画面全靠缓存；"刚切到窗口就最小化"在纯 2Hz 轮询下必然无帧不播。
    /// </summary>
    private void CaptureForegroundNow(IntPtr hwnd)
    {
        if (!IsRunning || hwnd == IntPtr.Zero) return;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;   // 自己的表面不进缓存
        if (NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return;
        if (IsFullscreenForeground()) return;

        var p = AnimParamsStore.Current;
        if (_frames.TryGet(hwnd, p.ForegroundCacheMaxAgeMs, out _, out _, out _)) return;   // 已有新鲜帧

        var now = NowMs();
        if (now - _lastForegroundCaptureMs < 250) return;
        _lastForegroundCaptureMs = now;
        if (Interlocked.CompareExchange(ref _foregroundCaptureInFlight, hwnd, IntPtr.Zero) != IntPtr.Zero) return;

        _ = Task.Run(() =>
        {
            try
            {
                for (var i = 1; i <= 4; i++)
                {
                    if (NativeMethods.IsIconic(hwnd)) return;   // 抓的过程中被最小化了：交给事件路径
                    var image = WindowCapture.Capture(hwnd, out var layer);
                    if (image is not null)
                    {
                        if (NativeMethods.GetWindowRect(hwnd, out var rect))
                        {
                            _frames.Put(hwnd, image, ToRect(rect));
                            Logger.Info($"前台补采：0x{hwnd.ToInt64():X} 层={layer} "
                                        + $"{image.PixelWidth}×{image.PixelHeight}（FOREGROUND 事件触发）");
                        }
                        return;
                    }
                    Thread.Sleep(8);
                }
                Logger.Info(AnimReport.Skipped("前台补采", "重试", "FOREGROUND 捕获重试耗尽",
                    $"hwnd=0x{hwnd.ToInt64():X}"));
            }
            catch (Exception ex)
            {
                Logger.Warn($"前台补采异常（已隔离）：{ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _foregroundCaptureInFlight, IntPtr.Zero);
            }
        });
    }

    /// <summary>
    /// 还原方向：窗口此刻已经可见，替身从图标那一行"流出来"长回原尺寸。
    /// 只装最小化不装还原的话，点开窗口是瞬间出现的，反而把动画衬得更假。
    /// </summary>
    private void PlayRestore(IntPtr hwnd)
    {
        if (!Admit(hwnd, AnimKind.Restore, "MINIMIZEEND", out var durationMs, out _)) return;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            _policy.Complete(AnimKind.Restore);
            Logger.Info(AnimReport.Skipped("MINIMIZEEND", "无", "读窗口矩形失败"));
            return;
        }
        var sourceRect = ToRect(rect);
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            _policy.Complete(AnimKind.Restore);
            Logger.Info(AnimReport.Skipped("MINIMIZEEND", "无", "窗口矩形为零"));
            return;
        }

        // 刚还原的窗口需要一点时间把客户区画完，抓太早会得到上一帧或纯色
        var image = Capture(hwnd, "MINIMIZEEND");
        if (image is null)
        {
            _policy.Complete(AnimKind.Restore);
            return;
        }
        var layer = _lastLayer;
        _frames.Put(hwnd, image, sourceRect);

        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        LogGeometry("MINIMIZEEND", layer, hwnd, sourceRect, icon);
        StartGenie(image, sourceRect, icon, durationMs, reverse: true, "MINIMIZEEND", layer, 0);
    }

    /// <summary>按设置选效果：genie（灯罩网格/条带）、zoom（缩放淡出）、suck（更紧的吸吮）。</summary>
    private void StartGenie(BitmapSource image, Rect windowRect, Rect iconRect, int durationMs, bool reverse,
        string source, string layer, double ageMs, Action? afterDone = null)
    {
        var effect = SettingsService.Current.MinimizeEffect;
        var engine = EffectiveEngine(AnimParamsStore.Current.Engine);

        // native 场景渲染器尚未提供动画宿主（BN_Anim_* 未实现）：如实回退 mesh，日志写生效值
        if (effect == "zoom")
        {
            StartZoomEffect(image, windowRect, iconRect, durationMs, reverse, source, layer, ageMs, afterDone);
            return;
        }
        if (effect == "suck")
        {
            StartStripGenie(image, windowRect, iconRect, durationMs, reverse, 0.15, source, layer, engine, ageMs, afterDone);
            return;
        }
        if (engine == "strips")
        {
            StartStripGenie(image, windowRect, iconRect, durationMs, reverse, GenieMath.DefaultLag,
                source, layer, engine, ageMs, afterDone);
            return;
        }
        StartMesh(image, ToLamp(windowRect), ToLamp(iconRect),
            AnimParamsStore.Current.LampSplit, durationMs, reverse, source, layer, ageMs, afterDone);
    }

    /// <summary>
    /// zoom：整窗向图标方向缩成一个点并淡出。当前用源窗口内的中心缩放（避免被 2D 幽灵窗口的
    /// 画布裁切）；Dock 图标方向的精确位移等 native 场景渲染器接管后再补。
    /// </summary>
    private void StartZoomEffect(BitmapSource image, Rect windowRect, Rect iconRect, int durationMs, bool reverse,
        string source, string layer, double ageMs, Action? afterDone = null)
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
            var opacity = 1.0 - 0.35 * GenieMath.Clamp01(e);
            return new List<(GenieRect, double)> { (r, opacity) };
        }, source, "zoom", layer, ageMs, afterDone);
    }

    /// <summary>
    /// 兜底引擎：把窗口切成水平条带，整体向图标收拢。
    /// 形状比灯罩网格差一档（没有"脖子"，越靠下的行不会额外多收一些），
    /// 但它只用 2D 绘制，在任何渲染环境下都画得出来。
    /// </summary>
    private void StartStripGenie(BitmapSource image, Rect source, Rect icon, int durationMs, bool reverse,
        double lag, string sourceName, string layer, string engine, double ageMs, Action? afterDone = null)
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
        }, sourceName, engine, layer, ageMs, afterDone);
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
    /// <paramref name="split"/> = 两阶段分界（参数表 LampSplit，alpha.15 接线：之前这个参数无消费方）。
    /// </summary>
    private void StartMesh(BitmapSource image, LampGeometry windowRect, LampGeometry iconRect,
        double split, int durationMs, bool reverse, string source, string layer, double ageMs,
        Action? afterDone = null)
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
                source, layer, "strips", ageMs, afterDone);
            return;
        }

        _meshGhost = ghost;
        ghost.Apply(reverse ? 1.0 : 0.0, windowRect, iconRect, split);
        ghost.Show();
        ghost.Place();

        RunClock(durationMs, source, "mesh", layer, ageMs,
            t => ghost.Apply(reverse ? 1.0 - t : t, windowRect, iconRect, split),
            () =>
            {
                _meshGhost = null;
                ghost.Dispose();
                afterDone?.Invoke();
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

    /// <summary>最大化/还原：替身从旧矩形长到新矩形（morph 曲线，无滞后）。</summary>
    private void PlayZoom(IntPtr hwnd, RECT fromRect, RECT toRect)
    {
        if (!Admit(hwnd, AnimKind.Maximize, "LOCATIONCHANGE", out var durationMs, out _)) return;

        var image = Capture(hwnd, "LOCATIONCHANGE");
        if (image is null)
        {
            _policy.Complete(AnimKind.Maximize);
            return;
        }
        var layer = _lastLayer;

        var from = ToRect(fromRect);
        var to = ToRect(toRect);
        if (from.Width <= 0 || from.Height <= 0)
        {
            _policy.Complete(AnimKind.Maximize);
            Logger.Info(AnimReport.Skipped("LOCATIONCHANGE", layer, "旧矩形无效"));
            return;
        }

        var p = AnimParamsStore.Current;
        var maxDuration = p.Scaled(p.MorphDurationMs);
        durationMs = durationMs <= 0 ? maxDuration : durationMs;

        StartGhost(image, to, durationMs, t =>
        {
            var e = AnimCurve.EaseInOutQuad(t);
            var r = GenieMath.ZoomLerp(
                new GenieRect(
                    (from.Left - to.Left) / to.Width, (from.Top - to.Top) / to.Height,
                    from.Width / to.Width, from.Height / to.Height),
                new GenieRect(0, 0, 1, 1), e);
            return new List<(GenieRect, double)> { (r, 1.0) };
        }, "LOCATIONCHANGE", "morph", layer, 0);
    }

    // ---------- 打开 / 关闭（v4.0.0-alpha.13，v2.2 §4.3/§4.4） ----------

    /// <summary>
    /// 打开（EVENT_OBJECT_SHOW，系统触发路径）：窗口此刻已可见但<b>首帧通常还没画完</b>，
    /// 这段空窗就是"白板裸窗"暴露的时间。占位帧设计把它吃掉：ghost 先以 t=0（全透明，
    /// 与 <see cref="OpenAnimMath.Start"/> 同帧）占住窗口矩形，后台捕获重试（8ms×12）就绪后
    /// 当帧换入真画面、开始播时钟；捕获彻底失败 → 撤占位、如实写日志，窗口照常（系统行为）。
    /// <para>
    /// 【为什么只演"第一次显示"】macOS 的打开动画发生在窗口生命周期的创建时刻；
    /// hide/show 回归（托盘应用回显）在 mac 上没有入场动画，演了反而假。
    /// </para>
    /// </summary>
    private void PlayOpen(IntPtr hwnd)
    {
        // pid 记账先于一切设置判断：关闭动画的排除/节流全靠它，SHOW 是唯一可靠时机
        if (_knownPids.Count > 1024) _knownPids.Clear();
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid != 0) _knownPids[hwnd] = pid;

        if (!SettingsService.Current.OpenAnimation) return;   // 用户显式关掉：SHOW 高频，静默早退不刷日志
        if (!IsRunning || Busy) return;
        if (_openAttempted.Contains(hwnd)) return;
        if (_openAttempted.Count > 512) _openAttempted.Clear();
        _openAttempted.Add(hwnd);

        var ownPid = (uint)Environment.ProcessId;
        if (!WindowEligibility.IsEligibleWindow(hwnd, ownPid)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var sourceRect = ToRect(rect);
        if (sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize) return;   // 下拉框/提示条不演

        // 首窗收口（Dock 启动弹跳联动）：每个进程只报一次；自家窗口不报（Dock 自己的弹层）
        if (pid != 0 && pid != ownPid && _firstWindowSeenPids.Add(pid) && _firstWindowShown is not null)
        {
            var name = ProcessName(pid);
            if (name != "未知") _firstWindowShown.Invoke(name);
        }

        // alpha.17：钩子层可用 = 每一次 SHOW 都会被 CBT 层在首帧上屏前 Cloak 接管（真替换）。
        // 事后路径整体短路——若这里再播，就是在已可见窗口上叠加第二层动画（用户点名不要的形态）。
        // 首窗报告与 pid 记账必须保留（Dock 启动弹跳与关闭动画的 pid 台账都靠它们）。
        if (HookBridge.Available) return;

        if (!Admit(hwnd, AnimKind.Open, "OBJECTSHOW", out var durationMs, out _)) return;

        var p = AnimParamsStore.Current;
        if (string.Equals(SettingsService.Current.OpenAnimStyle, "fromicon", StringComparison.OrdinalIgnoreCase))
        {
            PlayOpenFromIcon(hwnd, sourceRect, p, durationMs);
            return;
        }
        PlayOpenInPlace(hwnd, sourceRect, p, durationMs);
    }

    /// <summary>in-place（默认）：占位帧 → 捕获重试 → 换入真帧 → 200ms easeOutQuint 落位。</summary>
    private void PlayOpenInPlace(IntPtr hwnd, Rect sourceRect, AnimParams p, int durationMs)
    {
        var ghost = new GhostLayer(PlaceholderTexture(), 1, sourceRect);
        _ghost = ghost;
        ghost.Show();
        ghost.SetContentVisible(false);   // 占位阶段不画任何东西：不遮挡真窗口，只占住合成层的位置

        var startedMs = NowMs();
        var attempts = Math.Max(1, p.CaptureRetryCount);
        var retryMs = Math.Max(1, p.CaptureRetryMs);
        _ = Task.Run(() =>
        {
            // PrintWindow 在线程池跑（与前台帧采样同款纪律：抓一张大窗要几十毫秒，不能占 UI 线程）
            for (var i = 1; i <= attempts; i++)
            {
                var image = WindowCapture.Capture(hwnd, out var layer);
                if (image is not null)
                {
                    var waited = NowMs() - startedMs;
                    _dispatcher.BeginInvoke(
                        () => StartOpenClock(hwnd, ghost, image, sourceRect, durationMs, layer, waited, i, "OBJECTSHOW", "openInPlace"),
                        DispatcherPriority.Render);
                    return;
                }
                Thread.Sleep(retryMs);
            }
            _dispatcher.BeginInvoke(() =>
            {
                if (_ghost == ghost) { _ghost = null; }
                try { ghost.Close(); } catch (InvalidOperationException) { /* 已关：占位也是尽力而为 */ }
                _policy.Complete(AnimKind.Open);
                Logger.Info(AnimReport.Skipped("OBJECTSHOW", "重试", "占位帧捕获重试耗尽",
                    $"重试={attempts}×{retryMs}ms 窗口照常显示"));
            }, DispatcherPriority.Render);
        });
    }

    /// <summary>
    /// fromIcon（实验，<b>默认关</b>）：替身画布 = 窗口 ∪ Dock 图标的联合矩形，
    /// 画面从图标矩形长到窗口矩形（单条带 + 整块缩放；无捕获重试——实验项不做完整编排，
    /// 捕获失败即如实不播）。缺图标矩形（应用不在 Dock）退化 in-place。
    /// </summary>
    private void PlayOpenFromIcon(IntPtr hwnd, Rect sourceRect, AnimParams p, int durationMs)
    {
        var icon = _dockTarget(hwnd);
        if (icon is not { } d || d.Width <= 0)
        {
            Logger.Info(AnimReport.Skipped("OBJECTSHOW", "无", "fromIcon 缺 Dock 图标矩形，退化 in-place"));
            PlayOpenInPlace(hwnd, sourceRect, p, durationMs);
            return;
        }

        var image = Capture(hwnd, "OBJECTSHOW");
        if (image is null)
        {
            _policy.Complete(AnimKind.Open);
            return;
        }

        var canvas = Rect.Union(sourceRect, d);
        var from = new GenieRect(
            (d.Left - canvas.Left) / canvas.Width, (d.Top - canvas.Top) / canvas.Height,
            d.Width / canvas.Width, Math.Max(0.02, d.Height / canvas.Height));
        var to = new GenieRect(
            (sourceRect.Left - canvas.Left) / canvas.Width, (sourceRect.Top - canvas.Top) / canvas.Height,
            sourceRect.Width / canvas.Width, sourceRect.Height / canvas.Height);
        var flyDuration = p.Scaled(p.FromIconDurationMs);
        var fadeShare = OpenAnimMath.FadeShare;

        var ghost = new GhostLayer(image, 1, canvas);
        _ghost = ghost;
        ghost.Show();
        RunClock(flyDuration, "OBJECTSHOW", "openFromIcon", _lastLayer, 0, t =>
        {
            var e = AnimCurve.EaseOutQuint(t);
            var r = GenieMath.ZoomLerp(from, to, e);
            var opacity = AnimCurve.EaseOutQuad(AnimCurve.Clamp01(t / fadeShare));
            ghost.Apply(new List<(GenieRect, double)> { (r, opacity) });
        }, () =>
        {
            _ghost = null;
            ghost.Close();
        });
    }

    /// <summary>DWM Cloak：把真窗口从合成树里摘掉（不销毁、不隐藏，Win8+ 对普通顶层窗口可靠）。
    /// 失败（老系统/属性被拒）返回 false，调用方退回 alpha.14 的"重影"模式。</summary>
    /// <remarks>internal：CBT-SHOW 路径的 Cloak 由钩子层就地完成，宿主端（MinimizeHookClient）
    /// 只负责 Uncloak——验活/看门狗/退出收摊三处都要调它。</remarks>
    internal static bool SetCloak(IntPtr hwnd, bool cloaked)
    {
        try
        {
            var v = cloaked ? 1 : 0;
            return NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref v, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CBT-SHOW 真替换入口（alpha.17）：钩子已在目标进程内、首帧上屏之前把真窗口 Cloak 并交权，
    /// 本方法以<b>不透明替身</b>从 <see cref="OpenAnimMath.Start"/> 的 90% 缩放起播到 100% 落位，
    /// 演完先 Uncloak 真窗口（终帧与真窗口完全重合，无跳变）再收替身。
    /// 返回是否接管：false = 门禁拒绝/参数不合法（调用方必须立即 Uncloak 放行原生显示）。
    /// 与事后路径（<see cref="PlayOpen"/>）互斥——钩子可用时后者整体短路，不存在双动画。
    /// </summary>
    public bool PlayOpenFromHook(IntPtr hwnd, BitmapSource image, Rect sourceRect)
    {
        if (!IsRunning || Busy)
        {
            Logger.Info(AnimReport.Skipped("CBT-SHOW", "无", IsRunning ? "上一条动画未结束（单替身闸门）" : "动画子系统未启用"));
            return false;
        }
        if (image is null || sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize)
        {
            Logger.Info(AnimReport.Skipped("CBT-SHOW", "钩子帧", "帧/矩形不合法"));
            return false;
        }
        if (!Admit(hwnd, AnimKind.Open, "CBT-SHOW", out var durationMs, out _)) return false;

        var p = AnimParamsStore.Current;
        LogGeometry("CBT-SHOW", "钩子帧", hwnd, sourceRect, IconRectFor(sourceRect, _dockTarget(hwnd)));
        Logger.Info($"打开动画（CBT-SHOW 真替换）：0x{hwnd.ToInt64():X} 钩子已 Cloak 真窗口，替身起播 "
                    + $"{image.PixelWidth}×{image.PixelHeight} 落位={durationMs}ms");

        var ghost = new GhostLayer(image, 1, sourceRect);
        _ghost = ghost;
        _lastLayer = "钩子帧";
        ghost.Show();
        var startFrame = OpenAnimMath.Start(p.OpenStartScale);
        ghost.Apply(new List<(GenieRect, double)> { (startFrame.Rect, 1.0) });
        RunClock(durationMs, "CBT-SHOW", "openInPlace", "钩子帧", 0, t =>
        {
            var f = OpenAnimMath.Frame(t, p.OpenStartScale);
            ghost.Apply(new List<(GenieRect, double)> { (f.Rect, 1.0) });
        }, () =>
        {
            SetCloak(hwnd, false);   // 先还真窗口再收替身：终帧与真窗口重合，Uncloak 藏在替身下面
            if (_cloakedWindow == hwnd) _cloakedWindow = IntPtr.Zero;
            _ghost = null;
            ghost.Close();
        });
        return true;
    }

    /// <summary>
    /// CBT-DESTROY 关闭路径（alpha.17）：钩子在窗口销毁前抓下最后一帧（不阻断销毁），
    /// 本方法用这帧播"收拢消失"替身——比 alpha.15 的前台缓存帧新（零龄），也比它诚实：
    /// 画面就是用户按关闭键那一瞬间。设置项 CloseAnimation 关闭时直接返回（钩子层那帧白抓，
    /// 但开关状态由 Flags bit1 同步给钩子，正常情况下钩子根本不会拦）。
    /// </summary>
    public void PlayCloseFromHook(IntPtr hwnd, uint pid, BitmapSource image, Rect sourceRect)
    {
        if (!SettingsService.Current.CloseAnimation) return;   // 钩子侧已同步开关；双保险静默
        if (!IsRunning || Busy) return;
        if (sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize)
        {
            Logger.Info(AnimReport.Skipped("CBT-DESTROY", "钩子帧", "矩形不合法"));
            return;
        }
        if (!Admit(hwnd, AnimKind.Close, "CBT-DESTROY", out var durationMs, out _, pid)) return;

        Logger.Info($"关闭动画（CBT-DESTROY 钩子帧）：pid={pid} 收拢={durationMs}ms "
                    + $"{image.PixelWidth}×{image.PixelHeight}");
        StartGhost(image, sourceRect, durationMs, t =>
        {
            var f = CloseAnimMath.Frame(t, AnimParamsStore.Current.CloseEndScale);
            return new List<(GenieRect, double)> { (f.Rect, f.Opacity) };
        }, "CBT-DESTROY", "closeFade", "钩子帧", 0);
    }

    /// <summary>捕获就绪后的换帧与起播：占位 → 真帧 → Cloak 真窗口 → 时钟。核对 ghost 身份（期间可能被收摊）。</summary>
    private void StartOpenClock(IntPtr hwnd, GhostLayer ghost, BitmapSource image, Rect sourceRect, int durationMs,
        string layer, long waitedMs, int attempts, string source, string engine)
    {
        if (_ghost != ghost)
        {
            _policy.Complete(AnimKind.Open);
            return;   // 占位期间被收摊（Dispose/竞速）：槽必须还，动画放弃
        }
        var p = AnimParamsStore.Current;
        ghost.SwapImage(image);
        ghost.SetContentVisible(true);

        // alpha.15：Cloak 真窗口——alpha.14 里真窗口从不隐藏，替身只是叠在已可见窗口上的
        // 一层淡入重影（用户反馈"打开动画几乎没有"的最大单一原因）。现在：先让替身以
        // 不透明帧占住窗口（从 90% 缩放起），再 Cloak；动画结束时先 Uncloak 再收替身
        // （终帧与真窗口完全重合，Uncloak 发生在不透明替身下面，无跳变）。
        // Cloak 失败 → 回退重影模式（透明度曲线照旧）。
        var cloaked = SetCloak(hwnd, true);
        if (cloaked) _cloakedWindow = hwnd;
        var startFrame = OpenAnimMath.Start(p.OpenStartScale);
        ghost.Apply(new List<(GenieRect, double)> { (startFrame.Rect, cloaked ? 1.0 : startFrame.Opacity) });
        if (cloaked)
            Logger.Info("打开动画：真窗口已 Cloak（替身入场模式）");
        Logger.Info($"打开动画：占位帧就绪 层={layer} 重试={attempts} 耗时={waitedMs:0}ms "
                    + $"{image.PixelWidth}×{image.PixelHeight} 落位={durationMs}ms");
        RunClock(durationMs, source, engine, layer, 0, t =>
        {
            var f = OpenAnimMath.Frame(t, p.OpenStartScale);
            ghost.Apply(new List<(GenieRect, double)> { (f.Rect, cloaked ? 1.0 : f.Opacity) });
        }, () =>
        {
            if (cloaked)
            {
                SetCloak(hwnd, false);   // 先还真窗口再收替身：顺序反了会闪一帧空白
                if (_cloakedWindow == hwnd) _cloakedWindow = IntPtr.Zero;
            }
            _ghost = null;
            ghost.Close();
        });
    }

    /// <summary>
    /// 关闭（EVENT_OBJECT_DESTROY，默认<b>不播</b>——macOS 的关闭就是干脆地消失，README 已写明这是忠实）。
    /// <see cref="AnimParams.CloseAnimation"/> 所在设置显式打开才播；DESTROY 到手时窗口已销毁，
    /// 画面只能取前台帧缓存替它留的那张，龄超过 <see cref="AnimParams.CloseCacheMaxAgeMs"/> 判废不播。
    /// 自家窗口（设置/关于本机）不走这里——它们拥有自己的窗口循环，由 <see cref="OwnWindowAnimator"/> 真动画。
    /// </summary>
    private void PlayClose(IntPtr hwnd)
    {
        if (!SettingsService.Current.CloseAnimation) return;   // 默认关：用户显式开启才播（静默，不刷日志）
        // alpha.17：钩子可用时关闭走 CBT-DESTROY 钩子帧（销毁前新帧，零龄）；缓存帧路径整体短路，
        // 避免同一销毁播两遍（事后 OBJECTDESTROY 与钩子帧必然都到）。
        if (HookBridge.Available) return;
        if (!IsRunning || Busy) return;
        // 只演"我们见过 SHOW"的窗口：DESTROY 对所有顶层窗口都发（含 tooltip/临时窗），
        // 没有 pid 记账的一律不管——它们没进过缓存，也没有排除清单可查。
        if (!_knownPids.TryGetValue(hwnd, out var pid)) return;

        var p = AnimParamsStore.Current;
        if (!_frames.TryGet(hwnd, p.CloseCacheMaxAgeMs, out var cached, out var cachedRect, out var age)
            || cached is null || cachedRect.Width < MinShellSize || cachedRect.Height < MinShellSize)
        {
            Logger.Info(AnimReport.Skipped("OBJECTDESTROY", "缓存", "关闭动画无可用缓存帧",
                $"pid={pid} 龄线={p.CloseCacheMaxAgeMs}ms 缓存条数={_frames.Count}"));
            return;
        }

        if (!Admit(hwnd, AnimKind.Close, "OBJECTDESTROY", out var durationMs, out _, pid)) return;

        var verdict = _policy.Usability((long)age);
        Logger.Info($"关闭动画：缓存帧 龄={age:0}ms 判定={verdict} pid={pid} 收拢={durationMs}ms");
        StartGhost(cached, cachedRect, durationMs, t =>
        {
            var f = CloseAnimMath.Frame(t, p.CloseEndScale);
            return new List<(GenieRect, double)> { (f.Rect, f.Opacity) };
        }, "OBJECTDESTROY", "closeFade", "缓存", age);
    }

    /// <summary>占位纹理：1×1 近白（Pbgra32）。占位阶段内容不可见，这张只是让 ImageBrush 有合法纹理。</summary>
    private static BitmapSource PlaceholderTexture()
    {
        // B,G,R,A（预乘）：0xF3 = 近白的"主题卡底"
        byte[] pixel = [0xF3, 0xF3, 0xF3, 0xFF];
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, pixel, 4);
        bmp.Freeze();
        return bmp;
    }

    private void StartGhost(BitmapSource image, Rect screenPhysical, int durationMs,
        Func<double, List<(GenieRect Rect, double Opacity)>> sampler,
        string source, string engine, string layer, double ageMs, Action? afterDone = null)
    {
        var ghost = new GhostLayer(image, GenieMath.DefaultSlices, screenPhysical);
        _ghost = ghost;
        ghost.Show();
        ghost.Apply(sampler(0));
        RunClock(durationMs, source, engine, layer, ageMs, t => ghost.Apply(sampler(t)),
            () => { CloseGhost(); afterDone?.Invoke(); });
    }

    private void CloseGhost()
    {
        if (_ghost is null) return;
        var ghost = _ghost;
        _ghost = null;
        try { ghost.Close(); }
        catch (InvalidOperationException) { /* 已被关掉：动画是尽力而为的东西 */ }
    }

    /// <summary>两条动画共用"同一时刻只有一个替身"的闸门：网格与条带谁在跑都算占用。</summary>
    private bool Busy => _ghost is not null || _meshGhost is not null;

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
        AnimParamsStore.ParamsChanged -= OnParamsChanged;   // 静态事件：不退订会把整个动画器钉死在内存里
        _sampleTimer?.Stop();
        _sampleTimer = null;
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        if (_showHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_showHook);
        if (_destroyHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_destroyHook);
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        _minimizeHook = _locationHook = IntPtr.Zero;
        _showHook = _destroyHook = _foregroundHook = IntPtr.Zero;
        _proc = null;
        _lastRect.Clear();
        _processNames.Clear();
        _knownPids.Clear();
        _openAttempted.Clear();
        _firstWindowSeenPids.Clear();
        _hookMinimizedAt.Clear();
        _frames.Clear();
        if (_cloakedWindow != IntPtr.Zero)
        {
            SetCloak(_cloakedWindow, false);   // 中途收摊：被 Cloak 的真窗口必须还真（红线）
            _cloakedWindow = IntPtr.Zero;
        }
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

        /// <summary>
        /// 换入真帧纹理（打开动画：占位帧 → 捕获就绪的真画面）。条带结构与视口切分不变，
        /// 只换 ImageBrush 的图——重建整棵树会让首帧多一趟布局，竞态窗口里能省则省。
        /// </summary>
        public void SwapImage(BitmapSource image)
        {
            foreach (var band in _bands)
            {
                if (band.Fill is ImageBrush brush) brush.ImageSource = image;
            }
        }

        /// <summary>
        /// 占位阶段隐藏全部条带（真窗口不被遮挡、命中测试不拦截）；换入真帧后恢复。
        /// Apply 每帧会按矩形重新写 Visibility，恢复后无需再调本方法。
        /// </summary>
        public void SetContentVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var band in _bands) band.Visibility = v;
        }
    }
}
