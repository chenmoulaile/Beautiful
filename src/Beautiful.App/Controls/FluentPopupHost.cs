// 自绘菜单，视觉对标 macOS；无第三方代码。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突
using Point = System.Windows.Point; // UseWindowsForms 隐式全局 using 与 System.Drawing.Point 冲突
using Size = System.Windows.Size; // 同上，System.Drawing.Size

namespace Beautiful.App.Controls;

/// <summary>
/// 无边框高级弹层宿主：Topmost、不进任务栏、<c>ShowActivated=true</c> + <c>Focusable=true</c>
/// （评审 Important#3：菜单须键盘可达 → 采用"激活式弹层"模型：窗口显示即夺取前台激活与键盘焦点）。
/// <para>
/// 【形态（v3 T2/T3）】恒为<b>分层窗口</b>（<c>AllowsTransparency=true</c>）——WPF 唯一保证客户端逐像素
/// alpha 的模式，圆角与自绘投影留白因此真的透明；v2 那句"启用模糊的主题走非分层 + DWM 系统背板"
/// 已在用户真机上被证伪（半透明卡底下面是不被合成的客户端像素 → 黑卡 + 硬边），连同
/// <c>DwmExtendFrameIntoClientArea</c> / <c>DWMWA_SYSTEMBACKDROP_TYPE</c> /
/// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> 一起退役。
/// v3.1 补充：这条禁令只针对 **WPF 表面**；唯一的例外是 <c>Preview/ThumbnailPreview.cs</c> 的纯 Win32
/// 缩略图宿主——它客户区里装的就是 DWM 自己合成的画面，没有客户端 alpha 可言，玻璃边距是它显示的前提。
/// 入场淡入动画 <c>Content.Opacity</c> 的习惯保留：
/// 分层窗口动画 <c>Window.Opacity</c> 会额外叠一层整窗混合，内容层淡入既够用又更省。
/// <para>
/// 【玻璃感来自抓屏自绘模糊】落位算出窗口物理矩形后、<b>Show 之前</b>调
/// <see cref="Services.ScreenBlurBackdrop.ForPanel"/>：此刻本窗口还不可见，抓到的必定是干净的下方内容
/// （弹层是"显示一次"的瞬态表面，不需要重抓）。抓屏失败或 <c>BEAUTIFUL_NOBLUR=1</c> 时交回 null，
/// 内容由 <see cref="IBackdropAware.OnBackdropReady"/> 改铺实心卡底——降级链上没有任何一环会画成黑。
/// </para>
/// <para>
/// 关闭策略【Deactivated→Close 为主，光标探测看门狗为辅】：激活式窗口显示后即为前台，失去激活
/// （点别处/Esc/选叶子）即关链——这与 v1"秒自杀"无关（v1 的病根是把 Deactivated→Close 与
/// <c>WS_EX_NOACTIVATE</c> 并用：窗口从不激活→任何激活抖动都触发失活秒关）。本模型窗口确实激活，
/// Deactivated 只在真正让位给链外窗口时触发。<see cref="FluentMenu"/> 负责链感知：子弹层激活导致
/// 父层 Deactivated 时不误关（链内仍有活动窗口即保活）。看门狗（40ms GetCursorPos 轮询）保留为兜底
/// 安全网，但当整条父子弹层链仍持有激活（键盘导航中）时它绝不因光标离开而关，二者不打架：
/// 全链存活判定由 <see cref="KeepAliveProbe"/> 提供（<see cref="FluentMenu"/> 注入"链内有活动窗口
/// 或光标在链任一矩形内"），根宿主独揽定时器，子宿主（<see cref="ParentHost"/> 非空）不自关、随链级联关闭。
/// </para>
/// <para>
/// 定位全程物理像素：输入锚点即 <c>GetCursorPos</c>/<c>Cursor.Position</c> 的物理坐标；
/// 尺寸取内容同步 <c>Measure</c> 的 DesiredSize×<c>锚点所在显示器的有效 DPI</c>（评审 Important#1：
/// 混合 DPI 多显示器必须按锚点显示器取缩放，不能用刚创建未落位 hwnd 的 GetDpiForWindow），
/// 边缘翻转用锚点所在显示器的 <c>rcWork</c>（物理），再 <c>SetWindowPos</c> 落位。
/// 入场 120ms 淡入 + 位移（CubicEase，方向随翻转：托盘弹层从下往上滑入，子菜单自父项向右滑出）。
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
    private bool _flipX, _flipY; // 最近一次收敛落位是否左/上翻转（入场动画方向用）

    /// <summary>暂停自动关闭（拖拽选择、模态确认等场景可临时置 true 抑制"光标离开即关"）。</summary>
    public bool KeepOpen { get; set; }

    /// <summary>父弹层（子菜单宿主）；null = 根弹层（自带看门狗，全链存活由 <see cref="KeepAliveProbe"/> 判定）。
    /// 不用 <c>Window.Owner</c>：链存活/级联关闭由 <see cref="FluentMenu"/> 统一编排，父子各自独立激活。</summary>
    public FluentPopupHost? ParentHost { get; init; }

    /// <summary>存活探测（根宿主看门狗每拍调用；返回 true=保持打开）。null = 退化为"仅本窗口矩形"判定。</summary>
    public Func<bool>? KeepAliveProbe { get; set; }

    /// <summary>最终落位的物理像素矩形（ShowAt 之后有效），供链存活判定。</summary>
    public Rect PlacedPhysicalRect { get; private set; } = Rect.Empty;

    public FluentPopupHost()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // 【v3 形态】一律分层窗口：WPF 的逐像素 alpha 只有这一条路，圆角卡 + 四周自绘投影留白
        // 都靠它才能真正透明（v2 按主题模糊开关在 true/false 之间切换，是"黑卡 + 硬边"的成因）。
        // 玻璃感不再来自 DWM 材质，而来自显示前的抓屏模糊（ShowAtPhysical 里 ForPanel），
        // 拿不到背板时由内容自铺实心卡底 —— 窗口底维持 Transparent（不参与"要不要透出模糊"的决策）。
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = true;                    // 激活式弹层：可获焦 → 支持键盘导航（评审 Important#3）
        ShowActivated = true;                // 显示即夺取前台激活（失活即关，由 FluentMenu 链感知兜底）
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 激活式弹层（评审 Important#3）：显示即夺取前台激活/键盘焦点，故【不加】WS_EX_NOACTIVATE
        // （它会压制激活，与 ShowActivated=true 冲突）。仅保留 WS_EX_TOOLWINDOW：alt-tab 不可见兜底
        // （ShowInTaskbar=false 已覆盖，防御性叠加）。
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GwlpExStyle);
        SetWindowLongPtr(hwnd, GwlpExStyle, ex | WsExToolwindow);
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
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // 尺寸确定性来源 = 同步 Measure 的 DesiredSize（DIP），不依赖任何 dispatcher 冲刷时机：
        // Show 后 SizeToContent 对 HWND 的真实尺寸调整发生在低于 Render 的派发优先级里，
        // GetWindowRect 即时读数会拿到未定形旧尺寸 → 翻转计算错误（曾致托盘弹层落位偏 150px）。
        // 缩放基准取【锚点所在显示器的有效 DPI】（评审 Important#1）：hwnd 刚 EnsureHandle 尚未落位，
        // PerMonitorV2 下 GetDpiForWindow 返回初置/主屏 DPI，副屏 150% 时会让 230DIP 测成 230px
        // 而 WPF 随后按目标屏重排成 345px → 翻转判定/底边计算差 ~1.5 倍。
        double wPx = 0, hPx = 0;
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = ScaleOfAnchor(primary, hwnd);
            wPx = fe.DesiredSize.Width * scale;
            hPx = fe.DesiredSize.Height * scale;
        }

        var work = WorkAreaOf(primary);
        _flipX = false;
        double x = primary.X;
        if (x + wPx > work.Right - EdgeInset) { x = flip.X - wPx; _flipX = true; } // 右边收拢到 flip 锚点
        if (x < work.Left + EdgeInset) x = work.Left + EdgeInset;

        _flipY = false;
        double y = primary.Y;
        if (y + hPx > work.Bottom - EdgeInset) { y = flip.Y - hPx; _flipY = true; } // 底边收拢到 flip 锚点
        if (y < work.Top + EdgeInset) y = work.Top + EdgeInset;

        var ix = (int)Math.Round(x);
        var iy = (int)Math.Round(y);

        // 【抓屏必须在 Show 之前】此刻本窗口不可见，取回的是"面板下方"的干净内容；
        // 显示之后再抓就会把弹层自己的像素抓进背板（鬼影）。捕获区已由 ScreenBlurBackdrop 外扩余量，
        // 因此菜单内容变宽/换行只露出旁边已抓好的内容，不需要重抓。
        // 只在内容确实要铺背板时才抓（不实现 IBackdropAware 的内容 = 自绘卡片，没必要为它跑一遍 GDI）。
        var backdrop = Content is IBackdropAware && wPx > 0 && hPx > 0
            ? ScreenBlurBackdrop.ForPanel(new PhysicalRect(ix, iy, ix + (int)Math.Round(wPx), iy + (int)Math.Round(hPx)))
            : null;

        // 首帧即透明，杜绝任何潜在跳动。淡入只动画内容不动画窗口（Window.Opacity 会给整窗再叠一层
        // 混合，内容层淡入够用且更省）。
        if (Content is UIElement rootForFade) rootForFade.Opacity = 0;
        Show();
        // Show 后 WPF 可能按默认位置落一次（Left/Top 未设），以测量位置强制覆盖
        SetWindowPos(hwnd, IntPtr.Zero, ix, iy, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
        PlacedPhysicalRect = new Rect(ix, iy, wPx, hPx);

        // 把背板交给内容：卡片在窗口里的位置只有内容知道（自绘投影留白），所以由内容自己瞄准并决定
        // 实心降级底。null 也是合法结果（抓屏失败 / BEAUTIFUL_NOBLUR=1）→ 内容必须铺实心底，不得留透明。
        (Content as IBackdropAware)?.OnBackdropReady(backdrop);

        // 激活式弹层：落位后强制夺取前台激活/键盘焦点。托盘右键经 NotifyIcon 消息进来时本进程未必是
        // 前台，系统前台锁会压制 ShowActivated/Activate → 菜单收不到键盘（评审 Important#3 键盘可达的
        // 前提）。用 AttachThreadInput+SetForegroundWindow 可靠夺焦（原生菜单显示时同样短暂抢焦点）。
        ForceForeground();

        PlayEntrance();
        if (ParentHost is null) StartWatchdog(); // 子宿主不自律：随根链级联关闭
    }

    /// <summary>
    /// 强制把本窗口置为前台活动窗口（临时挂接当前前台线程解除系统前台锁），确保键盘可达。
    /// 已是前台时退化为普通 <see cref="Window.Activate"/>（父↔子弹层间切换无需挂接）。
    /// </summary>
    internal void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) { Activate(); return; }
        var fore = GetForegroundWindow();
        uint foreTid = fore != IntPtr.Zero ? GetWindowThreadProcessId(fore, out _) : 0u;
        uint curTid = GetCurrentThreadId();
        var attached = foreTid != 0 && foreTid != curTid && AttachThreadInput(curTid, foreTid, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            // 【终审 I-4】附着必须在 finally 配对解除：中途抛异常若遗留附着，本 UI 线程会与
            // **前台应用**的输入队列持久耦合（焦点/激活互相牵制，且每次开菜单累积一次附着）。
            // 与 Switcher/SwitcherService.cs 激活链路同型。
            if (attached) AttachThreadInput(curTid, foreTid, false);
        }
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

    /// <summary>锚点所在显示器的工作区（物理像素，扣任务栏）；异常回退主屏全屏。
    /// internal：启动器覆盖层要按"光标所在屏"把卡片水平居中并落在上三分之一处，与此处落位规则同源，
    /// 不再另抄一份 MonitorFromPoint/GetMonitorInfo。</summary>
    internal static Rect WorkAreaOf(Point anchorPhysicalPx)
    {
        var pt = new POINT { X = (int)Math.Round(anchorPhysicalPx.X), Y = (int)Math.Round(anchorPhysicalPx.Y) };
        var mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return new Rect(mi.rcWork.Left, mi.rcWork.Top,
                            mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        return new Rect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)); // SM_CXSCREEN/SM_CYSCREEN 兜底
    }

    /// <summary>
    /// 锚点所在显示器的有效 DPI 缩放（DIP→物理像素倍率，评审 Important#1）。
    /// MonitorFromPoint(TONEAREST)+GetDpiForMonitor(MDT_EFFECTIVE_DPI)；shcore 不可用或异常时
    /// 回退 <c>GetDpiForWindow</c>（单屏主 DPI 场景等价），再兜底 1.0。
    /// </summary>
    internal static double ScaleOfAnchor(Point anchorPhysicalPx, IntPtr fallbackHwnd)
    {
        var pt = new POINT { X = (int)Math.Round(anchorPhysicalPx.X), Y = (int)Math.Round(anchorPhysicalPx.Y) };
        var mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (mon != IntPtr.Zero &&
            GetDpiForMonitor(mon, MdtEffectiveDpi, out uint dpiX, out _) == 0 && // 0 = S_OK
            dpiX > 0)
            return dpiX / 96.0;
        // 兜底路径极少命中（多屏 shcore 恒可用）：退回窗口 DPI，无则 1.0。
        // 注意此 hwnd 尚未落位，可能取到初置屏 DPI——仅为降级，不追求精确。
        return fallbackHwnd != IntPtr.Zero ? GetDpiForWindow(fallbackHwnd) / 96.0 : 1.0;
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

    private void PlayEntrance()
    {
        var dpiScale = GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;
        // 位移方向：翻转即"从锚点那侧滑回"——托盘弹层自下而上，子菜单自左向右滑出
        var tt = new TranslateTransform(_flipX ? 6 * dpiScale : -6 * dpiScale,
                                        _flipY ? 10 * dpiScale : -10 * dpiScale);
        if (Content is UIElement content)
        {
            content.RenderTransform = tt;
            content.Opacity = 0; // 淡入动画内容（不可动画窗口，见 ShowAtPhysical 注释：会破坏背板合成）
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var xAnim = new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        var yAnim = new DoubleAnimation(tt.Y, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        tt.BeginAnimation(TranslateTransform.XProperty, xAnim);
        tt.BeginAnimation(TranslateTransform.YProperty, yAnim);
        if (Content is UIElement fadeTarget)
            fadeTarget.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
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
