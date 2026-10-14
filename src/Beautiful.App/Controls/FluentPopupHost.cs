// 自绘菜单，视觉对标 macOS；无第三方代码。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突
using Point = System.Windows.Point; // UseWindowsForms 隐式全局 using 与 System.Drawing.Point 冲突

namespace Beautiful.App.Controls;

/// <summary>
/// 无边框高级弹层宿主：Topmost、不进任务栏、<c>ShowActivated=false</c> + <c>WS_EX_NOACTIVATE</c>
/// （点击/显示均不抢前台焦点）。
/// <para>
/// 关闭策略【刻意不用 Deactivated→Close】：NOACTIVATE 弹层从不被激活，也就没有失活事件可用；
/// 而普通"失活即关"在托盘场景会因显示窗口引发的激活竞态秒关。改为"点击外部才关"：
/// 40ms 定时器轮询 GetCursorPos（物理像素）——光标离开本窗口矩形（外扩容差，覆盖父子弹层间的
/// 空隙桥接）持续 ≥2 拍（~80ms，防穿越抖动误关）即 Close。全链存活判定由 <see cref="KeepAliveProbe"/>
/// 提供（<see cref="FluentMenu"/> 注入"光标在整条父子弹层链任一矩形内"），根宿主独揽定时器，
/// 子宿主（<see cref="ParentHost"/> 非空）不自关、随链级联关闭。
/// </para>
/// <para>
/// 定位全程物理像素：输入锚点即 <c>GetCursorPos</c>/<c>Cursor.Position</c> 的物理坐标；
/// 尺寸取显示后 <c>GetWindowRect</c> 的真实物理矩形（免 DPI 往返换算），边缘翻转用锚点所在显示器的
/// <c>rcWork</c>（物理），再 <c>SetWindowPos</c> 落位。入场 120ms 淡入 + 位移（CubicEase，
/// 方向随翻转：托盘弹层从下往上滑入，子菜单自父项向右滑出）。
/// </para>
/// </summary>
public class FluentPopupHost : Window
{
    private const int PollIntervalMs = 40;   // 光标轮询周期
    private const int GraceTicks = 2;        // 连续离开拍数（~80ms）才关闭
    private const double TaskbarLift = 12;   // 上翻时弹层底边与锚点的间隙（物理 px，避让任务栏）
    private const double DropdownGap = 8;    // 未翻转（向下展开）时顶边下移量（物理 px）
    private const double EdgeInset = 4;      // 工作区边缘最小留白（物理 px）

    private DispatcherTimer? _watchdog;
    private int _outsideTicks;

    /// <summary>暂停自动关闭（拖拽选择、模态确认等场景可临时置 true 抑制"光标离开即关"）。</summary>
    public bool KeepOpen { get; set; }

    /// <summary>父弹层（子菜单宿主）；null = 根弹层（自带看门狗，全链存活由 <see cref="KeepAliveProbe"/> 判定）。
    /// 刻意不用 <c>Window.Owner</c>：拥有关系会引发激活联动，与 NOACTIVATE 模型冲突。</summary>
    public FluentPopupHost? ParentHost { get; init; }

    /// <summary>存活探测（根宿主看门狗每拍调用；返回 true=保持打开）。null = 退化为"仅本窗口矩形"判定。</summary>
    public Func<bool>? KeepAliveProbe { get; set; }

    /// <summary>最终落位的物理像素矩形（ShowAt 之后有效），供链存活判定。</summary>
    public Rect PlacedPhysicalRect { get; private set; } = Rect.Empty;

    public FluentPopupHost()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;           // 逐像素 alpha：圆角轮廓 + 投影（真毛玻璃背景由 Task 4 DWM 化）
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;                   // 永不获焦 → 无激活/失活竞态
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE：Show 与点击都不夺取前台激活（托盘菜单不闪掉前台应用）；
        // WS_EX_TOOLWINDOW 兜底（ShowInTaskbar=false 已覆盖，防御性叠加，alt-tab 不可见）。
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GwlpExStyle);
        SetWindowLongPtr(hwnd, GwlpExStyle, ex | WsExNoactivate | WsExToolwindow);
    }

    /// <summary>
    /// 在物理像素锚点处显示：优先右下展开；越出锚点所在显示器工作区则向左/向上翻转
    /// （上翻时底边停在锚点上方 <see cref="TaskbarLift"/>px——托盘场景即贴任务栏上沿）。
    /// </summary>
    public void ShowAt(Point anchorPhysicalPx) =>
        ShowAtPhysical(
            new Point(anchorPhysicalPx.X, anchorPhysicalPx.Y + DropdownGap),
            new Point(anchorPhysicalPx.X, anchorPhysicalPx.Y - TaskbarLift));

    /// <summary>双锚点摆位：放得下则左上角贴 <paramref name="primary"/>，否则右边贴 <paramref name="flip"/>.X / 底边贴 <paramref name="flip"/>.Y。</summary>
    internal void ShowAtPhysical(Point primary, Point flip)
    {
        Opacity = 0; // 先透明：显示→测真实物理尺寸→重定位，落位跳动不可见
        Show();
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded); // 冲刷布局/测量（SizeToContent 定形）

        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) { Opacity = 1; return; }
        double w = r.Right - r.Left, h = r.Bottom - r.Top;
        var work = WorkAreaOf(primary);

        var flippedX = false;
        double x = primary.X;
        if (x + w > work.Right - EdgeInset) { x = flip.X - w; flippedX = true; } // 右边收拢到 flip 锚点
        if (x < work.Left + EdgeInset) x = work.Left + EdgeInset;

        var flippedY = false;
        double y = primary.Y;
        if (y + h > work.Bottom - EdgeInset) { y = flip.Y - h; flippedY = true; } // 底边收拢到 flip 锚点
        if (y < work.Top + EdgeInset) y = work.Top + EdgeInset;

        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            SwpNosize | SwpNozorder | SwpNoactivate);
        PlacedPhysicalRect = new Rect(x, y, w, h);

        PlayEntrance(flippedX, flippedY);
        if (ParentHost is null) StartWatchdog(); // 子宿主不自律：随根链级联关闭
    }

    /// <summary>光标是否在本弹层物理矩形外扩 <paramref name="inflate"/>px 范围内（桥接父子弹层间隙）。</summary>
    internal bool CursorInInflated(double inflate)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PlacedPhysicalRect == Rect.Empty) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        if (!GetCursorPos(out var c)) return true; // 探测失败宁不关（保守）
        return c.X >= r.Left - inflate && c.X <= r.Right + inflate &&
               c.Y >= r.Top - inflate && c.Y <= r.Bottom + inflate;
    }

    /// <summary>锚点所在显示器的工作区（物理像素，扣任务栏）；异常回退主屏全屏。</summary>
    private static Rect WorkAreaOf(Point anchorPhysicalPx)
    {
        var pt = new POINT { X = (int)Math.Round(anchorPhysicalPx.X), Y = (int)Math.Round(anchorPhysicalPx.Y) };
        var mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return new Rect(mi.rcWork.Left, mi.rcWork.Top,
                            mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        return new Rect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)); // SM_CXSCREEN/SM_CYSCREEN 兜底
    }

    private void StartWatchdog()
    {
        _watchdog = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (KeepOpen) { _outsideTicks = 0; return; }
        var alive = KeepAliveProbe?.Invoke() ?? CursorInInflated(RectInflate);
        if (alive) _outsideTicks = 0;
        else if (++_outsideTicks >= GraceTicks) Close();
    }

    /// <summary>本窗口自身的判定容差（物理 px）：桥接父子弹层间 ~8px 空隙 + 光标微抖。</summary>
    internal const double RectInflate = 16;

    private void PlayEntrance(bool flippedX, bool flippedY)
    {
        var dpiScale = GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;
        // 位移方向：翻转即"从锚点那侧滑回"——托盘弹层自下而上，子菜单自左向右滑出
        var tt = new TranslateTransform(flippedX ? 6 * dpiScale : -6 * dpiScale,
                                        flippedY ? 10 * dpiScale : -10 * dpiScale);
        if (Content is UIElement content) content.RenderTransform = tt;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new Storyboard();
        var xAnim = new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        var yAnim = new DoubleAnimation(tt.Y, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        tt.BeginAnimation(TranslateTransform.XProperty, xAnim);
        tt.BeginAnimation(TranslateTransform.YProperty, yAnim);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = ease
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_watchdog is not null)
        {
            _watchdog.Tick -= OnWatchdogTick;
            _watchdog.Stop();
            _watchdog = null;
        }
        KeepAliveProbe = null; // 断链防泄漏（probe 闭包捕获 FluentMenu）
        base.OnClosed(e);
    }
}
