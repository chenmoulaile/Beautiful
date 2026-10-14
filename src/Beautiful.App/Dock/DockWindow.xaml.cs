using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Dock;
using Beautiful.Core.Themes;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Beautiful.App.Dock;

/// <summary>
/// 底部居中悬浮 Dock：AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS，ABE_BOTTOM 全宽条带）
/// + macOS 式放大（Task 6 重写）：条目交给 <see cref="DockSurface"/> 自绘——连续高斯放大场
/// （真实物理光标坐标，非离散悬停索引）+ CompositionTarget.Rendering（vsync）驱动的指数阻尼，
/// 逐帧只写 Canvas.Left/Top 与 RenderTransform，面板内零 Margin/LayoutTransform 改写。
/// 条带比可见面板高 <see cref="StripHeadroomDip"/>（透明、点击穿透），放大后的图标才不被窗口上沿裁掉。
/// 主题由 ThemeManager 驱动（Task 13）：构造期 Apply(HarmonyOs)，订阅 ThemeChanged 重设
/// 亚克力/间距/放大参数（IconSize、Spacing、MaxScale、ShowMagnification 写入 DockSurface）。
/// </summary>
public partial class DockWindow : AcrylicWindow
{
    // 面板可见高度与条带顶部透明余量随 DockIconSize / 放大强度动态计算（见 VisiblePanelDip / StripHeadroomDip），
    // 不再是固定常量——图标尺寸是设置项（32..72），macOS 放大后图标会向上生长，条带必须容得下。
    // 下列常量把原 v1 实测几何（面板 76、余量 22）拆成可推导的组成部分：
    //   行高 = IconSize + RowInsetDip（DockSurface 内部同值 20）；可见面板 = 行高 + 上下边距（Surface.Margin 6+6）。
    // 主题默认 IconSize=44 时 VisiblePanelDip()=76，与原 DockHeightDip 完全一致；无放大时余量取 MinStripHeadroomDip=22，
    // 与原 StripHeadroomDip 一致 → 未动设置时条带几何零变化（保住"贴底无缝"的实测结论）。
    private const double RowInsetDip = 20;
    private const double PanelMarginDip = 12;
    private const double MinStripHeadroomDip = 22;
    private const double SigmaInPitches = 2.0;  // 高斯 σ（单位 = 一个图标间距；控制器裁定：不随主题变化）
        // 尺度说明（评审发现：旧注释撒谎）：Core 的场是标准差形式 exp(-d²/(2σ²))，v1 的 Magnifier 是
        // exp(-(d/σ)²)，同写 2.0 时本实现的实际影响半径比 v1 宽 √2 倍（底板鼓包约宽 26%）。
        // 刻意保留 2.0：半径更宽 → 相邻项倍率差更小、斜率更缓，正是"放大别看起来粗糙"要的方向；
        // 若要 1:1 复刻 v1 视觉尺度，把本常量改成 2.0/√2 ≈ 1.4142 即可（DockSurface.Sigma 同一口径）。


    private readonly WindowTracker _tracker = new();
    private readonly DockViewModel _vm;
    // 组合根（本任务最简路径）：DockWindow 持有 SmtcService + MediaViewModel，
    // MediaViewModel 构造期自启动 SMTC；MediaPanel 惰性创建（胶囊点击时）。
    // App.OnStartup / DockViewModel 保持不动（媒体条目由 DockViewModel 的 suffix 占位提供）。
    private readonly SmtcService _smtc;
    private readonly MediaViewModel _mediaVm;
    private readonly TaskbarManager? _taskbar; // App 注入：TaskbarCreated（explorer 重建）后重放策略
    private MediaPanel? _mediaPanel;
    private ContextMenu? _itemContextMenu;     // 条目右键菜单（Resources 取用；代码后置手动开关）
    private DockItem? _contextItem;            // 右键菜单打开时的目标条目（固定/取消固定用）
    private DateTime? _panelAutoHiddenAtUtc; // 面板最近一次"失焦自动收起"时刻（胶囊点击的 toggle 语义用）
    // 图标尺寸/放大强度滑杆拖动时会连续触发排布刷新；条带重排（AppBar 协商 + SetWindowPos）成本较高，
    // 用一次性防抖把拖动期间的多次请求合并成落定后的一次重排（排布参数本身每帧即时写入，图标视觉跟手）。
    private DispatcherTimer? _repositionDebounce;

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;
    private NativeMethods.APPBARDATA _abd;
    private NativeMethods.RECT _screenRect; // AppBar 定位后的窗口物理矩形（物理光标校验用）
    private bool _appbarRegistered;
    private bool _closing; // OnClosing 起置位：阻止关闭前挂起的 BeginInvoke 重注册在 ABM_REMOVE 后复活 AppBar
    private HwndSource? _source;

    public DockWindow(TaskbarManager? taskbar = null)
    {
        _taskbar = taskbar;
        _smtc = new SmtcService();
        _mediaVm = new MediaViewModel(_smtc, Dispatcher); // 先于 InitializeComponent：XAML 模板可能经 RelativeSource 绑定 MediaVm
        InitializeComponent();
        _vm = new DockViewModel(_tracker);
        DataContext = _vm;
        _itemContextMenu = (ContextMenu)Resources["ItemContextMenu"];
        WireSurface();
        ThemeManager.ThemeChanged += OnThemeChanged;
        // 任务栏策略切换/explorer 重启重放（TaskbarManager.Apply/Restore → PolicyApplied）后，
        // AppBar 条带需重新协商：Hide 释放了底部条带预留 → Dock 贴真底；Keep → 让位在任务栏上方。
        if (_taskbar is not null) _taskbar.PolicyApplied += OnTaskbarPolicyApplied;
        // 初始主题：设置项驱动（settings.json 的 Theme，缺省鸿蒙；设置窗口切换即持久化）
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
        Loaded += OnWindowLoaded;
    }

    /// <summary>Dock 数据源（设置窗口编辑固定应用后经此重载固定块）。</summary>
    public DockViewModel Vm => _vm;

    /// <summary>媒体 VM（XAML 媒体条目模板经 RelativeSource 绑定到本属性）。</summary>
    public MediaViewModel MediaVm => _mediaVm;

    /// <summary>窗口跟踪器（顶栏"窗口"菜单数据源/激活动作，Task 9）。</summary>
    public WindowTracker Tracker => _tracker;

    /// <summary>顶栏媒体迷你项点击：展开 Dock 媒体面板（无会话时面板自身按 HasSession 折叠，不展开）。
    /// 复用既有 PositionMediaPanel 落位（面板锚在 Dock 胶囊上方），顶栏仅提供触发入口。</summary>
    public void OpenMediaPanelFromTopBar()
    {
        if (!_mediaVm.HasSession) return;
        ShowMediaPanel();
    }

    /// <summary>DockSurface 接线：光标取样 + 左键激活 + 右键条目菜单。</summary>
    private void WireSurface()
    {
        Surface.Sigma = SigmaInPitches;
        Surface.CursorProvider = SurfaceCursorXDip;   // 真实物理光标（含 AppBar 矩形守卫），见放大场注释
        Surface.Activated = ActivateItem;
        Surface.ContextRequested = OpenItemContextMenu;
    }

    // ---------- 生命周期 / AppBar ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _appbarMsg = NativeMethods.RegisterWindowMessage("AppBarMessage");
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        RegisterAndPositionAppBar();
        // 亚克力由主题驱动（构造期 ThemeManager.Apply → OnThemeChanged → ApplyTheme），此处不再硬编码
    }

    // ---------- 主题引擎（Task 13） ----------

    private void OnThemeChanged(ThemeDefinition theme) => ApplyTheme(theme);

    /// <summary>
    /// 主题切换落地：窗口级亚克力开/关 + 间距参数；放大开关与 MaxScale 由动画/悬停路径
    /// 直读 ThemeManager.Current；底色/圆角/图标尺寸/字体/指示样式走字典 DynamicResource 即时生效。
    /// <para>
    /// 【Task 4 裁定：Dock 不用 DWM 系统背板】本窗口的点击穿透依赖 XAML 的
    /// <c>Background="{x:Null}"</c>（未绘制区域不参与命中测试 → 面板两侧的条带空白可点到底下的桌面/
    /// 图标），而 WPF 里"未绘制区域真的透明"只有 <c>AllowsTransparency=true</c>（分层窗口）能做到；
    /// 分层窗口与 <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 互斥（DWM 不在其重定向表面后合成背板，写入返回
    /// S_OK 却什么都不画 → 黑条），且 <c>DwmExtendFrameIntoClientArea</c> 在分层窗口上同样无效。
    /// 所以 Dock 保留旧 <c>SetWindowCompositionAttribute</c> 亚克力（现代主题下确有模糊，只是非系统背板），
    /// 真背板只用于"内容铺满窗口"的弹层族（FluentPopupHost）与 MediaPanel。勿在此窗口改用 ApplyBackdrop。
    /// </para>
    /// </summary>
    private void ApplyTheme(ThemeDefinition theme)
    {
        if (theme.BlurEnabled)
            ApplyAcrylic(Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                        theme.AcrylicTint.G, theme.AcrylicTint.B));
        else
            DisableAcrylic();
        // 排布参数：底色/圆角/字体走 DynamicResource，尺寸/间距/放大写入 DockSurface。
        // IconSize/MaxScale 由设置项驱动（DockIconSize / MagnificationStrength），主题只提供"是否放大"与间距，
        // 见 ApplySurfaceLayoutParams。尺寸变化会改条带高度 → 顺带重排 AppBar（若尚未注册则 no-op，
        // OnSourceInitialized 的 RegisterAndPositionAppBar 会用同一套几何完成首帧定位）。
        ApplySurfaceLayoutParams();
        Surface.Refresh();
        ScheduleReposition();
    }

    /// <summary>
    /// 把当前设置（已规范化）落到 DockSurface 与主题字典：图标尺寸、放大强度、倒影开关。
    /// 主题只决定"是否放大"（macOS 才有放大）与间距；强度与尺寸是用户配置。放大关闭时 MaxScale 归 1、
    /// DockSurface 场恒回落 1（仍走阻尼，不冻结）。图标宽高 + 遮罩几何经 <see cref="ThemeManager.ApplyIconSize"/>
    /// 覆盖主题字典的 ThemeIconSize/ThemeIconClip，使条目模板的图片随 DockIconSize 即时缩放。
    /// </summary>
    private void ApplySurfaceLayoutParams()
    {
        var s = SettingsService.Current;
        var theme = ThemeManager.Current;
        var icon = (double)s.DockIconSize;
        Surface.IconSize = icon;
        Surface.Spacing = theme.ItemSpacing;
        Surface.ShowMagnification = theme.ShowMagnification;
        Surface.MaxScale = theme.ShowMagnification ? s.MagnificationStrength : 1.0;
        Surface.ReflectionEnabled = s.ReflectionEnabled;   // 接口位：倒影渲染器由后续任务消费，此处仅落旗标
        ThemeManager.ApplyIconSize(icon, Application.Current);
    }

    // ---------- 条带几何（随图标尺寸/放大强度动态） ----------

    /// <summary>可见面板高 = 行高(IconSize+20) + 上下边距(6+6)。主题默认 IconSize=44 时 = 76。</summary>
    private double VisiblePanelDip() => Surface.IconSize + RowInsetDip + PanelMarginDip;

    /// <summary>
    /// 条带在面板之上的透明余量，供放大后的图标向上生长不被窗口上沿裁切。
    /// 无放大主题恒取 <see cref="MinStripHeadroomDip"/>（=22，与 v1 实测一致）；macOS 下按
    /// 图标尺寸与当前倍率算越出量（图标底边在面板内，放大后顶边越出 ≈ IconSize·(MaxScale−1)−16），
    /// 至少 22（IconSize=44/MaxScale=1.8 时越出 19.2 → 取 22，复现原几何）。
    /// </summary>
    private double StripHeadroomDip()
    {
        if (!ThemeManager.Current.ShowMagnification) return MinStripHeadroomDip;
        var overflow = Surface.IconSize * (Surface.MaxScale - 1.0) - 16.0;
        return Math.Max(MinStripHeadroomDip, Math.Ceiling(overflow));
    }

    private double StripHeightDip() => VisiblePanelDip() + StripHeadroomDip();

    /// <summary>
    /// 设置页改动 DockIconSize/MagnificationStrength 后的即时生效入口（App.ApplyDockLayout 调用）：
    /// 重落排布参数 + 重排条带（图标尺寸变了面板/条带要跟着长，否则大图标被窗口上沿裁切）。
    /// </summary>
    public void RefreshLayoutFromSettings()
    {
        ApplySurfaceLayoutParams();
        Surface.Refresh();
        ScheduleReposition();
    }

    /// <summary>
    /// 请求重排条带：立即返回，真正的 <see cref="PositionAsAppBar"/> 延后到防抖到期执行。
    /// AppBar 尚未注册（构造早期）时到期重排也无害——OnSourceInitialized 的首帧定位已用同一套几何。
    /// 惰性建计时器（DockWindow 未必走到滑杆路径，避免构造期多分配一个计时器）。
    /// </summary>
    private void ScheduleReposition()
    {
        _repositionDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _repositionDebounce.Stop();
        _repositionDebounce.Tick -= OnRepositionDebounceTick;
        _repositionDebounce.Tick += OnRepositionDebounceTick;
        _repositionDebounce.Start();
    }

    private void OnRepositionDebounceTick(object? sender, EventArgs e)
    {
        _repositionDebounce?.Stop();
        if (_closing) return;
        PositionAsAppBar();
    }


    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _tracker.Start();
        // 动画驱动态（CompositionTarget.Rendering 钩子）由 DockSurface 自管：光标进入面板即 Wake，
        // 全部收敛且无悬停目标时自行摘钩——此处不再有 15ms DispatcherTimer。
        // 渲染层级一次性入日志：tier 0=软件渲染、1=部分加速、2=硬件加速。
        // 本机实测 tier=2（硬件加速），所以 frametime.ps1 里 ~30ms 的帧节奏**不是**软件渲染造成的：
        // 它来自 AllowsTransparency 分层窗口的呈现路径（WPF 光栅到内存位图 → DWM 逐帧合成）在这台 VM
        // 上的下限。排查"掉帧是代码还是环境"时先看这一行，别把 tier=2 误读成降级。
        var tier = RenderCapability.Tier >> 16;
        Logger.Info($"Dock 就绪：WPF 渲染 tier={tier}（0=软件渲染，2=硬件加速）" +
                    $" magnification={ThemeManager.Current.ShowMagnification} maxScale={ThemeManager.Current.MaxScale}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;                             // 先于 ABM_REMOVE：作废队列中尚未执行的 BeginInvoke 重注册
        _repositionDebounce?.Stop();                 // 作废挂起的条带重排（OnClosing 后不再 ABM_SETPOS）
        _mediaPanel?.Close();                        // 面板随 Dock 一起关闭（Closed 处理器会清空引用）
        Surface.Shutdown();                           // 摘掉 CompositionTarget.Rendering 钩子
        UnregisterAppBar();                           // ABM_REMOVE 必须在 hwnd 销毁前
        _source?.RemoveHook(WndProc);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged; // 解除主题订阅，防止闭包引用已关闭窗口
        if (_taskbar is not null) _taskbar.PolicyApplied -= OnTaskbarPolicyApplied;
        _vm.Detach();
        _tracker.Dispose();
        _mediaVm.Dispose();                          // 停定时器 + 解除 SmtcService 事件订阅
        _smtc.Dispose();
        base.OnClosed(e);
    }

    private void RegisterAndPositionAppBar()
    {
        if (_closing) return;                         // 关闭竞态：TaskbarCreated/WM_DISPLAYCHANGE 挂起的重注册不再执行
        if (_appbarRegistered) UnregisterAppBar();    // 重注册路径：先 ABM_REMOVE
        _abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _appbarMsg
        };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref _abd);
        _appbarRegistered = true;
        Logger.Info($"AppBar ABM_NEW 完成 hwnd=0x{_hwnd.ToInt64():X}");
        PositionAsAppBar();
        ScheduleHideSettle();
    }

    /// <summary>
    /// Hide 策略下 AppBar 注册/重注册会引发 shell 重布局，Win11 可能把任务栏摆回屏幕内、甚至把
    /// autohide 状态位改回去（= 重新占用底部条带）：T+0.8s / T+3s 各重申一次（重释放条带 + 移出窗口）
    /// 并重新协商条带位置。非 Hide 策略 Reassert 为 no-op，不调度。
    /// </summary>
    private void ScheduleHideSettle()
    {
        if (_taskbar is not { Current: Services.TaskbarPolicy.Hide }) return;
        foreach (var delayMs in new[] { 800, 3000 })
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_closing) return;
                _taskbar.Reassert();
                PositionAsAppBar();
            };
            timer.Start();
        }
    }

    /// <summary>任务栏策略变更落地（Apply/Restore）后重申策略并重新协商 AppBar 条带。</summary>
    private void OnTaskbarPolicyApplied()
    {
        if (_closing || !_appbarRegistered) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_appbarRegistered) return;
            _taskbar?.Reassert();
            PositionAsAppBar();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void UnregisterAppBar()
    {
        if (!_appbarRegistered) return;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref _abd);
        _appbarRegistered = false;
    }

    /// <summary>
    /// ABE_BOTTOM 全宽条带：QUERYPOS → 修正高度 → SETPOS → SetWindowPos，并同步 WPF 侧 DIP 布局属性。
    /// <para>
    /// 底边以 <b>shell 给的可用底边</b> 为准（<c>rc.Top = rc.Bottom - stripH</c>）：任务栏策略为 Keep/AutoHide
    /// 时那是任务栏上沿（Dock 悬在其上方，符合预期）；Hide 策略下 <see cref="TaskbarManager"/> 已用
    /// ABM_SETSTATE(ABS_AUTOHIDE) 释放掉任务栏的条带预留，QUERYPOS 返回的 bottom 就是**真屏幕底边**，
    /// 于是条带 = [0, screenH-stripH]-[screenW, screenH]，Dock 无缝贴底（实测 gap 48px → 0px，Task 7）。
    /// </para>
    /// </summary>
    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        // 条带 = 可见面板高 + 放大余量（均随图标尺寸/放大强度动态，见 StripHeightDip）：面板 Grid
        // VerticalAlignment=Bottom 贴条带底，多出的顶部透明带（Background={x:Null} 分层窗口不参与命中 →
        // 仍点击穿透）供放大后的图标向上生长。
        var stripH = (int)Math.Round(StripHeightDip() * dpiScale);
        var screenW = NativeMethods.GetSystemMetrics(0);  // SM_CXSCREEN
        var screenH = NativeMethods.GetSystemMetrics(1);  // SM_CYSCREEN

        _abd.uEdge = NativeMethods.AbeBottom;
        _abd.rc = new NativeMethods.RECT { Left = 0, Top = screenH - stripH, Right = screenW, Bottom = screenH };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var availableBottom = _abd.rc.Bottom;             // shell 允许我们占到的底边（= 既有 AppBar 的上沿）
        _abd.rc.Top = availableBottom - stripH;           // 修正：底边让位既有 AppBar/任务栏，高度以期望为准
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        // 贴底判据（Task 7 实测口径，日志即证据）：availableBottom == screenH ⇔ 任务栏条带预留已释放 ⇔ Dock 真贴底；
        // 未释放（Keep / 释放失败）时 availableBottom = 任务栏上沿，Dock 让位在其上方，此时"间隙"即用户看到的空档。
        var gap = screenH - availableBottom;
        var expect = $"(0,{availableBottom - stripH})-({screenW},{availableBottom})";
        var actual = $"({_abd.rc.Left},{_abd.rc.Top})-({_abd.rc.Right},{_abd.rc.Bottom})";
        Logger.Info($"AppBar QUERYPOS 可用底边={availableBottom}/screenH={screenH}" +
                    $" {(gap == 0 ? "→ 真屏幕底边（间隙 0px，条带预留已释放）" : $"→ 任务栏仍占 {gap}px")}" +
                    $"（策略={_taskbar?.Current.ToString() ?? "N/A"}）SETPOS rc={actual} 期望={expect}" +
                    $" {(actual == expect ? "一致" : "不一致")}");
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            _abd.rc.Left, _abd.rc.Top,
            _abd.rc.Right - _abd.rc.Left, _abd.rc.Bottom - _abd.rc.Top,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
        _screenRect = _abd.rc;

        // 同步 WPF 侧属性，避免 Show() 按内容重新计算窗口尺寸
        Left = _abd.rc.Left / dpiScale;
        Top = _abd.rc.Top / dpiScale;
        Width = Math.Max(1, (_abd.rc.Right - _abd.rc.Left) / dpiScale);
        Height = Math.Max(1, (_abd.rc.Bottom - _abd.rc.Top) / dpiScale);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appbarMsg != 0 && msg == _appbarMsg)
        {
            handled = true; // ABN_* 通知：本任务无需响应（空间被抢占由重注册流程覆盖）
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            handled = true; // explorer 重建任务栏 → ABM_REMOVE + 重注册；任务栏策略（隐藏/自动隐藏）一并重放
            _taskbar?.HandleTaskbarCreated();
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
            return IntPtr.Zero;
        }
        // WM_DISPLAYCHANGE：分辨率/显示器拓扑变化 → 重新协商条带。
        // 注意 WM_SETTINGCHANGE 不可在此重注册：ABM_SETPOS 改变工作区会再触发 WM_SETTINGCHANGE
        // 广播，形成"注册 → 广播 → 再注册"的无限循环（QA 实测 6 秒 63 次重注册）。
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
        }
        return IntPtr.Zero;
    }

    // ---------- 放大场光标取样 ----------

    /// <summary>
    /// 物理光标是否已不在本窗口矩形内；AppBar 未就绪 / GetCursorPos 失败时视为"在窗内"（交由取样继续判断）。
    /// 保留 v1 守卫：WPF 会在布局变化后按"最后已知位置"合成 MouseMove，若以事件坐标为准，
    /// 光标跳跃离窗后这些陈旧事件会把放大状态钉住。v2 更进一步——动画每帧直接取真实光标，
    /// 事件只作"唤醒"信号，因此陈旧事件连唤醒语义都没有（离场即在下一帧回落）。
    /// <para>
    /// 注意这里**不**收紧到可见面板矩形：光标越出面板上沿（在顶部透明余量里、停在已放大的图标上）时
    /// 必须继续跟踪，否则场一回落图标就缩回去、光标又落回面板 → 反复"放大/回落"自激振荡。
    /// 空闲摘钩改由 <see cref="DockSurface"/> 按条目行的水平影响范围判定（评审发现 #1）。
    /// </para>
    /// </summary>
    private bool IsCursorOutsideWindow(NativeMethods.POINT pt)
    {
        if (!_appbarRegistered) return false;
        return pt.X < _screenRect.Left || pt.X >= _screenRect.Right
            || pt.Y < _screenRect.Top || pt.Y >= _screenRect.Bottom;
    }

    /// <summary>
    /// DockSurface 的光标取样：真实物理光标 → 窗口 DIP（PointFromScreen，PerMonitorV2 下随当前 DPI）
    /// → surface 本地 DIP（TranslatePoint）。不在窗口内 / 源未就绪时返回 null = 无悬停目标（场回落 1）。
    /// 连续 X 交给 <see cref="MagnificationField"/>，scale 因而随光标平滑变化，
    /// 不再出现 v1"悬停索引切换 → 整排图标一起跳"的卡顿。
    /// 每帧只调一次 GetCursorPos（评审发现 #1 的伴随浪费：旧写法守卫与取样各调一次）。
    /// </summary>
    private double? SurfaceCursorXDip()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return null;
        if (IsCursorOutsideWindow(pt)) return null;
        try
        {
            var inWindow = PointFromScreen(new Point(pt.X, pt.Y)); // 物理 px → 窗口 DIP
            return TranslatePoint(inWindow, Surface).X;             // 窗口 DIP → surface 本地 DIP
        }
        catch (InvalidOperationException)
        {
            return null; // PresentationSource 尚未就绪（显示前）
        }
    }

    private void ActivateItem(DockItem item)
    {
        switch (item.Kind)
        {
            case DockItemKind.Window:
                var wi = item.WindowHandle is { } h
                    ? _tracker.Windows.FirstOrDefault(w => w.Handle == h)
                    : null;
                if (wi is not null) _tracker.ActivateOrMinimize(wi);
                break;

            case DockItemKind.RecycleBin:
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
                }
                catch (Exception ex) { Logger.Warn($"打开回收站失败: {ex.Message}"); }
                break;

            case DockItemKind.Pinned:
                if (item.LaunchPath.Length == 0) break;
                try
                {
                    Process.Start(new ProcessStartInfo(item.LaunchPath) { UseShellExecute = true });
                }
                catch (Exception ex) { Logger.Warn($"启动 {item.LaunchPath} 失败: {ex.Message}"); }
                break;

            case DockItemKind.Media:
                ToggleMediaPanel();
                break;
        }
    }

    // ---------- 右键菜单：固定 / 取消固定 ----------

    /// <summary>
    /// 右键条目弹出固定菜单（媒体/回收站无固定语义不弹）。仍走"代码后置手动开关"而非容器 ContextMenu：
    /// 实测 ItemContainerStyle 挂 ContextMenu 时 ContextMenuService 在本窗口把事件路由到
    /// ItemsControl 层（Source=ItemsControl），容器菜单永远不打开。
    /// 条目命中改由 DockSurface 用放大后矩形给出（HitTestItem），v1 的"最近基准中心"权宜法已删除。
    /// 该菜单迁移到 FluentMenu 属另一个任务，这里保持原生 ContextMenu 可用。
    /// </summary>
    private void OpenItemContextMenu(DockItem item, Point anchorInSurface)
    {
        if (_itemContextMenu is null) return;
        if (item.Kind is DockItemKind.Media or DockItemKind.RecycleBin) return;
        _contextItem = item;
        if (_itemContextMenu.Items[0] is MenuItem pin)
            pin.Visibility = item is { Kind: DockItemKind.Window, LaunchPath.Length: > 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (_itemContextMenu.Items[1] is MenuItem unpin)
            unpin.Visibility = item.Kind == DockItemKind.Pinned ? Visibility.Visible : Visibility.Collapsed;
        _itemContextMenu.PlacementTarget = Surface;
        _itemContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _itemContextMenu.IsOpen = true;
    }

    private void PinItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { Kind: DockItemKind.Window, LaunchPath.Length: > 0 } item)
            _vm.PinExecutable(item.LaunchPath); // 固定当前窗口的 exe 路径（去重，写回 dock.json）
    }

    private void UnpinItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { Kind: DockItemKind.Pinned } item)
            _vm.Unpin(item.LaunchPath);
    }

    // ---------- 媒体面板（实况窗展开） ----------

    private void ToggleMediaPanel()
    {
        if (!_mediaVm.HasSession) return; // 无会话：胶囊已折叠（占位命中不展开）
        if (_mediaPanel is { IsVisible: true })
        {
            _mediaPanel.Hide();
            _panelAutoHiddenAtUtc = null;
            return;
        }
        // 点击胶囊时面板会先因失焦自动 Hide（Deactivated 先于本 MouseUp），
        // 刚刚自动收起（<600ms）→ 本次点击视为"收起"而非"再次展开"
        if (_panelAutoHiddenAtUtc is { } at && (DateTime.UtcNow - at).TotalMilliseconds < 600)
        {
            _panelAutoHiddenAtUtc = null;
            return;
        }
        ShowMediaPanel();
    }

    private void ShowMediaPanel()
    {
        try
        {
            if (_mediaPanel is null)
            {
                _mediaPanel = new MediaPanel(_mediaVm) { Owner = this };
                // 仅在"失焦自动收起"开启时记录时间戳：关闭开关后面板不因失焦隐藏，
                // 若仍记录会把 <600ms 的 toggle 抑制窗口错误应用到点击展开
                _mediaPanel.Deactivated += (_, _) =>
                {
                    if (_mediaVm.PanelAutoHideOnDeactivate) _panelAutoHiddenAtUtc = DateTime.UtcNow;
                };
                _mediaPanel.Closed += (_, _) => { _mediaPanel = null; _panelAutoHiddenAtUtc = null; };
            }
            PositionMediaPanel();
            _mediaPanel.Show();      // Show 默认激活 → Deactivated 自动收起链路生效
            _mediaPanel.Activate();
            _mediaPanel.ScrollToCurrentLine();
        }
        catch (Exception ex)
        {
            // 防御：媒体面板是附属功能，任何异常不得拖垮 Dock 本体
            _mediaPanel?.Close();
            _mediaPanel = null;
            Logger.Warn($"打开媒体面板失败: {ex}");
        }
    }

    /// <summary>媒体条目（恒为 suffix 末位，仍按 Kind 扫描以稳健）；无则 null。</summary>
    private DockItem? MediaItemOrNull()
    {
        for (var i = _vm.Items.Count - 1; i >= 0; i--)
            if (_vm.Items[i].Kind == DockItemKind.Media) return _vm.Items[i];
        return null;
    }

    /// <summary>面板置于胶囊上方并水平居中（钳制在屏幕内）；胶囊中心经 PointToScreen（物理 px）→ DIP 换算。</summary>
    private void PositionMediaPanel()
    {
        const double panelW = 420, panelH = 520, edgeMargin = 8, gap = 6;
        var panel = _mediaPanel;
        if (panel is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1;

        double centerDipX = Left + Width / 2; // 兜底：Dock 中心
        var mediaItem = MediaItemOrNull();
        if (mediaItem is not null && Surface.GetItemBounds(mediaItem) is { } bounds && bounds.Width > 0)
        {
            // 放大后的胶囊矩形中心 → 屏幕物理 px → DIP（与 v1 同一路径，容器改由 DockSurface 提供）
            var screenPt = Surface.PointToScreen(new Point(bounds.X + bounds.Width / 2, bounds.Y));
            centerDipX = screenPt.X / dpi;
        }

        var screenW = NativeMethods.GetSystemMetrics(0) / dpi; // SM_CXSCREEN
        var left = Math.Clamp(centerDipX - panelW / 2, edgeMargin, Math.Max(edgeMargin, screenW - panelW - edgeMargin));
        // 条带顶部有动态的放大余量（供放大图标生长），面板真正的上沿在 Top + StripHeadroomDip()：
        // 用它算间距，余量尺寸变化时面板与 Dock 的视觉间隙保持一致。
        var top = Math.Max(edgeMargin, Top + StripHeadroomDip() - panelH - gap);
        panel.Left = left;
        panel.Top = top;
    }
}
