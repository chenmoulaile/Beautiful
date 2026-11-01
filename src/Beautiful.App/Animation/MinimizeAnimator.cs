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
/// 【alpha.21 换思路：过渡接管】原生动画由 <see cref="WindowTransitionGuard"/> 在 DWM 层按窗口关闭
/// （FORCEDISABLED），替身是唯一动画——"两条动画打架"在架构上不可能再发生；打开动画走
/// OBJECT_SHOW → 斗篷代管 → 替身落位 → 解斗篷，用户全程看不见"窗口先弹出来再演动画"的那一帧。
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

    /// <summary>窗口过渡接管器（alpha.21）：原生动画的开关与可见性代管都归它。可为 null（极端降级）。</summary>
    private readonly WindowTransitionGuard? _guard;

    /// <summary>genie 收尾时要解斗篷的窗口：还原/最大化/打开动画结束后真窗口才现身。</summary>
    private IntPtr _pendingUncloak = IntPtr.Zero;

    /// <summary>已尝试过打开动画的窗口（同一 hwnd 只播一次；上限整体重置）。</summary>
    private readonly HashSet<IntPtr> _openAttempted = new();

    private GhostLayer? _ghost;
    private GenieMeshGhost? _meshGhost;
    private bool _disabledByEnvironment;

    /// <summary>前台帧缓存（兜底捕获层）。采样在后台线程，查询在 UI 线程。</summary>
    private readonly ForegroundFrameCache _frames = new();

    /// <summary>放行门禁（风暴/并发/节流/排除/全屏）。</summary>
    private readonly AnimPolicy _policy = new(AnimParamsStore.Current);

    /// <summary>hwnd → 上一次的 (矩形, 是否最大化)。只为识别"从非最大化跳到最大化"那一下。</summary>
    private readonly Dictionary<IntPtr, (RECT Rect, bool Zoomed)> _lastRect = new();

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
        // 动画的正反两放，分开装两条钩子只会多一份句柄要管
        _minimizeHook = NativeMethods.SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _locationHook = NativeMethods.SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        // OBJECT_SHOW：新窗口出现的那一刻——关掉它的原生过渡（存量窗口由启动扫描补）+ 决定要不要
        // 斗篷代管后播打开动画。这是"打开动画与原生开窗动画重叠"的根治点。
        _showHook = NativeMethods.SetWinEventHook(
            EventObjectShow, EventObjectShow, IntPtr.Zero, _proc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);

        ApplyExclusions();
        BuildMountStatus();
        StartFrameSampler();
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
        if (_guard is null) degraded.Add("过渡接管器未装配（原生动画可能与替身叠加）");
        if (!s.MinimizeAnimation) degraded.Add("设置项 MinimizeAnimation 为关");
        if (effectiveEngine != requestedEngine) degraded.Add($"引擎 {requestedEngine} 不可用已回退 {effectiveEngine}");

        MountStatus = $"动画子系统已挂载：MINIMIZE=0x{_minimizeHook.ToInt64():X} "
                      + $"LOCATION={(locOk ? $"0x{_locationHook.ToInt64():X}" : "失败")} "
                      + $"SHOW={(showOk ? $"0x{_showHook.ToInt64():X}" : "失败")} "
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
                // 【同步斗篷，别等 BeginInvoke】斗篷是几百纳秒的 DWM 属性写入，可以在回调线程直接做；
                // 若等 UI 线程下一轮消息，窗口可能已经画了一帧"凭空消失前的样子"。此后斗篷一直保持到
                // 还原动画收尾——真窗口在隐形状态下完成还原，用户只看见 genie。
                _guard?.Cloak(hwnd, "minimize");
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
                }
                _lastRect[hwnd] = (rect, zoomed);
                return;
            }
            if (evt == EventObjectShow)
            {
                HandleWindowShown(hwnd);
                return;
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
            // 自家窗口默认不接管（Dock/顶栏/启动台/控制中心全是自家表面，接管=自我打架）；
            // 显式登记的内容窗口（访达，见 SelfContentWindows）例外——否则自家窗口永远播
            // 系统原生开窗动画（用户反馈"访达的滑动动画退回 windows 的了"）。
            if (own && !SelfContentWindows.Contains(hwnd)) return;
            var className = NativeMethods.GetWindowClassName(hwnd);
            if (className is "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "Progman" or "WorkerW"
                or "XamlExplorerHostIslandWindow") return;
            // 排除名单面向第三方应用；自家内容窗口不受名单约束
            if (!own && _policy.IsExcludedName(ProcessName(pid))) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
            if (r.Right - r.Left < MinShellSize || r.Bottom - r.Top < MinShellSize) return;

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

    /// <summary>最小化：抓一帧（或取缓存帧）盖在原位，再把替身收进该应用在 Dock 上的图标。</summary>
    private void PlayMinimize(IntPtr hwnd, string source)
    {
        // SW_HIDE 代管（斗篷被拒）且本轮动画没播成：立刻恢复可见——隐藏窗口会从任务栏/
        // 切换器消失，不能留着（斗篷窗口没有这个问题：斗篷不改变可见性，可安全挂到还原收尾）。
        void ReleaseHidden(string why)
        {
            if (_guard?.IsHiddenByUs(hwnd) == true) _guard.Uncloak(hwnd, why);
        }

        if (!Admit(hwnd, AnimKind.Minimize, source, out var durationMs, out _))
        {
            ReleaseHidden("门禁未放行");
            return;
        }

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
                // 缓存帧同样过实心合成：alpha 洞不会因为帧老就变少
                if (GenieFrameSource.MakeOpaque(cached) is not { } composed)
                {
                    ReleaseHidden("缓存帧整理失败");
                    _policy.Complete(AnimKind.Minimize);
                    return;
                }
                // SW_HIDE 代管的窗口不能像斗篷那样挂到还原收尾——隐藏窗口没有任务栏按钮，
                // 替身一放完就得让它以"最小化但可见"现身（Finish 统一走 Uncloak）。
                if (_guard?.IsHiddenByUs(hwnd) == true) _pendingUncloak = hwnd;
                StartGenie(composed, cachedRect, icon, durationMs, reverse: false, source, "缓存", age);
                return;
            }

            ReleaseHidden("缓存帧过期/缺失");
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "缓存", "到手时已最小化且缓存帧过期/缺失",
                $"缓存条数={_frames.Count}"));
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            ReleaseHidden("读窗口矩形失败");
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "读窗口矩形失败"));
            return;
        }
        var sourceRect = ToRect(rect);
        if (sourceRect.Width < MinShellSize || sourceRect.Height < MinShellSize)
        {
            ReleaseHidden("窗口过小");
            _policy.Complete(AnimKind.Minimize);
            Logger.Info(AnimReport.Skipped(source, "无", "窗口过小", $"{sourceRect.Width:0}×{sourceRect.Height:0}"));
            return;
        }

        var image = GenieFrameSource.MakeOpaque(Capture(hwnd, source));
        if (image is null)
        {
            ReleaseHidden("抓帧不可用");
            _policy.Complete(AnimKind.Minimize);
            return;
        }
        var layer = _lastLayer;
        _frames.Put(hwnd, image, sourceRect);   // 留一份：最小化后悬停预览与后续补播都靠它

        var iconRect = IconRectFor(sourceRect, _dockTarget(hwnd));
        LogGeometry(source, layer, hwnd, sourceRect, iconRect);
        // SW_HIDE 代管：替身一放完就恢复"最小化但可见"（隐藏窗口没有任务栏按钮）。
        // 斗篷窗口不设这个——斗篷保持到还原收尾，用户看不见"先弹出"帧。
        if (_guard?.IsHiddenByUs(hwnd) == true) _pendingUncloak = hwnd;
        // 最小化方向不解斗篷：斗篷保持到还原收尾（还原时真窗口在隐形状态下就位，用户看不见"先弹出"帧）。
        // 窗口一直最小化 → 看门狗会跳过 iconic 窗口（见 WindowTransitionGuard），斗篷 harmless。
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
            // SW_HIDE 代管（事件路径打的）但 Dock 链路没能播：立即恢复，别留隐藏窗口
            if (_guard?.IsHiddenByUs(hwnd) == true) _guard.Uncloak(hwnd, "Dock预抓门禁未放行");
            return;
        }
        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        // SW_HIDE 代管：替身一放完就恢复"最小化但可见"，任务栏按钮不能少
        if (_guard?.IsHiddenByUs(hwnd) == true) _pendingUncloak = hwnd;
        // 预抓帧入替前同样过实心合成；合成器对非空输入不会返回 null（?? image 兜底保型）
        StartGenie(GenieFrameSource.MakeOpaque(image) ?? image, sourceRect, icon, durationMs, reverse: false, "Dock", "预抓帧", 0);
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

        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        LogGeometry("MINIMIZEEND", layer, hwnd, sourceRect, icon);
        // SW_HIDE 代管窗口的还原：触发还原的 SW_RESTORE 已让真窗口可见，原生还原动画会和
        // 替身叠播（minimize 方向刚治好的病）——帧已抓（上面），先藏回去，替身放完由 Finish
        // 统一现身（Uncloak 对代管窗口走 ShowWindow 恢复，不抢激活）。
        if (_guard?.IsHiddenByUs(hwnd) == true && NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
        }
        _pendingUncloak = hwnd;   // RunClock 收尾统一解斗篷（异常路径也会走到 Finish，不会漏）
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
        ghost.Show();
        ghost.Apply(sampler(0));
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
        _sampleTimer?.Stop();
        _sampleTimer = null;
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        if (_showHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_showHook);
        _minimizeHook = _locationHook = _showHook = IntPtr.Zero;
        _proc = null;
        _lastRect.Clear();
        _processNames.Clear();
        _openAttempted.Clear();
        _frames.Clear();
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
