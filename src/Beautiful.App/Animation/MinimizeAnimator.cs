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

    private readonly Func<IntPtr, Point?> _dockTarget;
    private readonly Dispatcher _dispatcher;

    // 委托与钩子句柄都必须留在字段里：回调桩被 GC 的话，原生侧回调就是随机崩溃
    // （与 ForegroundWatcher / LowLevelKeyboardHook 同一套纪律）
    private NativeMethods.WinEventDelegate? _proc;
    private IntPtr _minimizeHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;

    private GhostLayer? _ghost;
    private bool _disabledByEnvironment;

    /// <summary>hwnd → 上一次的 (矩形, 是否最大化)。只为识别"从非最大化跳到最大化"那一下。</summary>
    private readonly Dictionary<IntPtr, (RECT Rect, bool Zoomed)> _lastRect = new();

    public MinimizeAnimator(Func<IntPtr, Point?> dockTargetProvider)
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
        _minimizeHook = NativeMethods.SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeStart, IntPtr.Zero, _proc,
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
        if (!IsRunning || _ghost is not null) return;
        if (NativeMethods.IsIconic(hwnd)) return;   // 已经缩下去了，抓不到画面

        var image = WindowCapture.Capture(hwnd);
        if (image is null) return;                  // 纯色/失败：宁可不动画，也不甩一块黑卡
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        var source = ToRect(rect);
        var target = _dockTarget(hwnd);
        // 目标点：该应用在 Dock 上的图标中心；找不到就收到条带上沿中央（不是"不做"，
        // 因为"窗口凭空消失"正是用户抱怨的点，收到条带中央至少给出连续的运动线索）
        var targetPoint = target ?? new Point((source.Left + source.Right) / 2, source.Bottom + 40);

        var scaleX = (double)(targetPoint.X - (source.Left + source.Width / 2)) / source.Width;
        var scaleY = (double)(targetPoint.Y - source.Top) / source.Height;
        var endWidth = Math.Clamp(image.PixelWidth > 0 ? 96.0 / image.PixelWidth : EndWidthRatio, 0.04, 0.6);

        StartGhost(image, source, MinimizeDurationMs,
            t => GenieMath.Sample(t, GenieMath.DefaultSlices, GenieMath.DefaultLag,
                0.5 + scaleX, 1.0 + scaleY, endWidth)
                .Select(b => (b.Rect, b.Opacity)).ToList());
    }

    /// <summary>最大化/还原：替身从旧矩形长到新矩形（单条带，无滞后）。</summary>
    private void PlayZoom(IntPtr hwnd, RECT fromRect, RECT toRect)
    {
        if (!IsRunning || _ghost is not null) return;
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

        var clock = Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            try
            {
                var t = clock.Elapsed.TotalMilliseconds / durationMs;
                if (t >= 1.0)
                {
                    ghost.Apply(sampler(1.0));
                    CloseGhost();
                    CompositionTarget.Rendering -= tick;
                    return;
                }
                ghost.Apply(sampler(t));
            }
            catch (Exception ex)
            {
                // 渲染回调抛出去会让整个 UI 线程的渲染停摆：立刻收摊，动画可以没有
                Logger.Warn($"最小化动画异常终止: {ex.GetType().Name} {ex.Message}");
                CompositionTarget.Rendering -= tick;
                CloseGhost();
            }
        };
        CompositionTarget.Rendering += tick;
    }

    private void CloseGhost()
    {
        if (_ghost is null) return;
        var ghost = _ghost;
        _ghost = null;
        try { ghost.Close(); }
        catch (InvalidOperationException) { /* 已被关掉：动画是尽力而为的东西 */ }
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public void Dispose()
    {
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        _minimizeHook = _locationHook = IntPtr.Zero;
        _proc = null;
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
