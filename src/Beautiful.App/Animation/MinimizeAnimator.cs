using System.Diagnostics;
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
/// <c>EVENT_SYSTEM_MINIMIZESTART</c> 在最小化开始的那一刻回调，此时窗口画面还在，
/// 抓一帧当替身盖在原位，真窗口在替身底下正常最小化，替身再收进 Dock 图标：视觉上就是"被吸进去"。
/// </para>
/// <para>
/// 【为什么替身用分层窗口 + 条带 Rectangle】逐像素透明只有分层窗口这一条路（v2 已被系统背板证伪），
/// 而 genie 的弯曲感来自"每条带各自向目标收拢"，WPF 2D 没有网格变形，用 N 条带 + 各自的
/// <c>ImageBrush.Viewport</c> 是零第三方依赖下最接近原味的实现。几何全在
/// <see cref="GenieMath"/>（纯函数 + 单测），这里只负责每帧把它乘回像素。
/// </para>
/// <para>
/// 【帧率无关】动画时长固定，进度按 <see cref="Stopwatch"/> 真实经过时间算，
/// 由 <c>CompositionTarget.Rendering</c>（vsync）驱动 —— 不用固定步长定时器（v2 的"一卡一卡"就是这么来的）。
/// </para>
/// </summary>
public sealed class MinimizeAnimator : IDisposable
{
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectLocationChange = 0x8004;
    private const uint ObjidWindow = 0;
    private const uint WinEventOutOfContext = 0x00000000;
    private const uint WinEventSkipOwnProcess = 0x00000002;

    /// <summary>最小化收拢时长。macOS 实测约 0.35s；再长就开始觉得等。</summary>
    private const int MinimizeDurationMs = 320;

    /// <summary>最大化/还原的缩放时长（比最小化短，因为它没有"吸进洞里"的位移量）。</summary>
    private const int ZoomDurationMs = 200;

    /// <summary>收拢后的宽度相对源窗口的比例（≈ Dock 图标宽 / 普通窗口宽）。</summary>
    private const double EndWidthRatio = 0.10;

    /// <summary>Dock 上该窗口图标那一格的物理矩形；null = 这条窗口没进 Dock。</summary>
    private readonly Func<IntPtr, Rect?> _dockTarget;
    private readonly Dispatcher _dispatcher;

    // 委托与钩子句柄都必须留在字段里：回调桩被 GC 的话，原生侧回调就是随机崩溃
    // （与 ForegroundWatcher / LowLevelKeyboardHook 同一套纪律）
    private NativeMethods.WinEventDelegate? _proc;
    private IntPtr _minimizeHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;

    private GhostLayer? _ghost;
    private GenieMeshGhost? _meshGhost;
    private bool _disabledByEnvironment;

    /// <summary>hwnd → 上一次的 (矩形, 是否最大化)。只为识别"从非最大化跳到最大化"那一下。</summary>
    private readonly Dictionary<IntPtr, (RECT Rect, bool Zoomed)> _lastRect = new();

    public MinimizeAnimator(Func<IntPtr, Rect?> dockTargetProvider)
    {
        _dockTarget = dockTargetProvider;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _disabledByEnvironment = Environment.GetEnvironmentVariable("BEAUTIFUL_NO_ANIMATION") == "1";
        if (_disabledByEnvironment)
        {
            Logger.Info("最小化动画：环境开关 BEAUTIFUL_NO_ANIMATION=1 已停用");
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

        if (_minimizeHook == IntPtr.Zero)
            Logger.Warn("最小化动画：MINIMIZESTART 钩子安装失败，动画不可用");
        else
            Logger.Info("最小化动画：已挂载（最小化 genie + 最大化缩放）");
    }

    /// <summary>当前是否启用（设置页/诊断块用）。</summary>
    public bool IsRunning => _minimizeHook != IntPtr.Zero && !_disabledByEnvironment;

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
                _dispatcher.BeginInvoke(() => PlayMinimize(hwnd), DispatcherPriority.Render);
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

    // ---------- 动画 ----------

    /// <summary>最小化：抓一帧盖在原位，再把替身收进该应用在 Dock 上的图标。</summary>
    private void PlayMinimize(IntPtr hwnd)
    {
        if (!IsRunning || Busy) return;
        if (NativeMethods.IsIconic(hwnd))
        {
            // 事件走队列后到，状态早已翻转——Dock 路径这时应已由预抓接管并占用 Busy。
            Logger.Info($"genie 事件路径错过：0x{hwnd.ToInt64():X} 到手时已最小化");
            return;
        }

        var image = WindowCapture.Capture(hwnd);
        if (image is null) { Logger.Info("genie 事件路径：抓帧不可用，跳过"); return; }
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        var source = ToRect(rect);
        var icon = IconRectFor(source, _dockTarget(hwnd));
        StartGenie(image, source, icon, MinimizeDurationMs, reverse: false);
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
        var image = WindowCapture.Capture(hwnd);
        if (image is null) return (null, null);
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return (null, null);
        return (image, ToRect(rect));
    }

    /// <summary>用预抓的画面放 genie。窗口此刻多半已经缩下去，矩形用预抓的那份，不再现查。</summary>
    public void PlayMinimizeFromCapture(IntPtr hwnd, BitmapSource image, Rect sourceRect)
    {
        if (!IsRunning || Busy) return;
        var icon = IconRectFor(sourceRect, _dockTarget(hwnd));
        StartGenie(image, sourceRect, icon, MinimizeDurationMs, reverse: false);
    }

    /// <summary>
    /// 还原方向：窗口此刻已经可见，替身从图标那一行"流出来"长回原尺寸。
    /// 只装最小化不装还原的话，点开窗口是瞬间出现的，反而把动画衬得更假。
    /// </summary>
    private void PlayRestore(IntPtr hwnd)
    {
        if (!IsRunning || Busy) return;
        // 刚还原的窗口需要一点时间把客户区画完，抓太早会得到上一帧或纯色
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var source = ToRect(rect);
        if (source.Width <= 0 || source.Height <= 0) return;

        var image = WindowCapture.Capture(hwnd);
        if (image is null) return;
        var icon = IconRectFor(source, _dockTarget(hwnd));
        StartGenie(image, source, icon, MinimizeDurationMs, reverse: true);
    }

    /// <summary>按设置选引擎：mesh（灯罩三角网，默认）或 strips（条带缩放，兜底）。</summary>
    private void StartGenie(BitmapSource image, Rect windowRect, Rect iconRect, int durationMs, bool reverse)
    {
        if (string.Equals(Services.SettingsService.Current.GenieEngine, "strips", StringComparison.OrdinalIgnoreCase))
        {
            StartStripGenie(image, windowRect, iconRect, durationMs, reverse);
            return;
        }
        StartMesh(image, ToLamp(windowRect), ToLamp(iconRect), durationMs, reverse);
    }

    /// <summary>
    /// 兜底引擎：把窗口切成水平条带，整体向图标收拢。
    /// 形状比灯罩网格差一档（没有"脖子"，越靠下的行不会额外多收一些），
    /// 但它只用 2D 绘制，在任何渲染环境下都画得出来。
    /// </summary>
    private void StartStripGenie(BitmapSource image, Rect source, Rect icon, int durationMs, bool reverse)
    {
        var scaleX = (icon.X + icon.Width / 2 - (source.Left + source.Width / 2)) / source.Width;
        var scaleY = (icon.Y - source.Top) / source.Height;
        var endWidth = Math.Clamp(source.Width > 0 ? icon.Width / source.Width : EndWidthRatio, 0.04, 0.6);

        StartGhost(image, source, durationMs, t =>
        {
            var p = reverse ? 1.0 - t : t;
            return GenieMath.Sample(p, GenieMath.DefaultSlices, GenieMath.DefaultLag,
                        0.5 + scaleX, 1.0 + scaleY, endWidth)
                    .Select(b => (b.Rect, b.Opacity)).ToList();
        });
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
    /// 用纹理网格跑一版 genie。<paramref name="reverse"/> = 还原方向（进度从 1 倒放到 0）。
    /// </summary>
    private void StartMesh(BitmapSource image, LampGeometry windowRect, LampGeometry iconRect,
        int durationMs, bool reverse)
    {
        GenieMeshGhost? ghost = null;
        try
        {
            ghost = new GenieMeshGhost(image);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            // 3D 渲染面建不起来（无硬件加速的极端环境等）：这次不动画，别把主流程带崩
            Logger.Warn($"genie 网格渲染不可用，本次跳过动画：{ex.GetType().Name}");
            return;
        }

        _meshGhost = ghost;
        ghost.Apply(reverse ? 1.0 : 0.0, windowRect, iconRect);
        ghost.Show();
        ghost.Place();

        RunClock(durationMs,
            t => ghost.Apply(reverse ? 1.0 - t : t, windowRect, iconRect),
            () =>
            {
                _meshGhost = null;
                ghost.Dispose();
            });
    }

    /// <summary>vsync 驱动的计时器：进度按真实经过时间算，与帧率无关（裁定：动画必须帧率无关）。</summary>
    private void RunClock(int durationMs, Action<double> onFrame, Action onDone)
    {
        var clock = Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            try
            {
                var t = clock.Elapsed.TotalMilliseconds / durationMs;
                if (t >= 1.0)
                {
                    onFrame(1.0);
                    CompositionTarget.Rendering -= tick;
                    onDone();
                    return;
                }
                onFrame(t);
            }
            catch (Exception ex)
            {
                // 渲染回调抛出去会让整个 UI 线程的渲染停摆：立刻收摊，动画可以没有
                Logger.Warn($"genie 动画异常终止: {ex.GetType().Name} {ex.Message}");
                CompositionTarget.Rendering -= tick;
                onDone();
            }
        };
        CompositionTarget.Rendering += tick;
    }

    /// <summary>最大化/还原：替身从旧矩形长到新矩形（单条带，无滞后）。</summary>
    private void PlayZoom(IntPtr hwnd, RECT fromRect, RECT toRect)
    {
        if (!IsRunning || Busy) return;
        var image = WindowCapture.Capture(hwnd);
        if (image is null) return;

        var from = ToRect(fromRect);
        var to = ToRect(toRect);
        if (from.Width <= 0 || from.Height <= 0) return;

        StartGhost(image, to, ZoomDurationMs, t =>
        {
            var r = GenieMath.ZoomLerp(
                new GenieRect(
                    (from.Left - to.Left) / to.Width, (from.Top - to.Top) / to.Height,
                    from.Width / to.Width, from.Height / to.Height),
                new GenieRect(0, 0, 1, 1), t);
            return new List<(GenieRect, double)> { (r, 1.0) };
        });
    }

    private void StartGhost(BitmapSource image, Rect screenPhysical, int durationMs,
        Func<double, List<(GenieRect Rect, double Opacity)>> sampler)
    {
        var ghost = new GhostLayer(image, GenieMath.DefaultSlices, screenPhysical);
        _ghost = ghost;
        ghost.Show();
        ghost.Apply(sampler(0));
        RunClock(durationMs, t => ghost.Apply(sampler(t)), CloseGhost);
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

    public void Dispose()
    {
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        _minimizeHook = _locationHook = IntPtr.Zero;
        _proc = null;
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
