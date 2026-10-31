using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Design;
using Beautiful.Core.Layout;
using Beautiful.Core.Themes;
using Beautiful.Core.TopBar;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
// UseWindowsForms 隐式全局 using 与下列 WPF 类型同名，定向消歧：
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control; // 消歧：WinForms 隐式 using 带入同名类型（控制中心打开态要清它的 Background 属性）
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment; // 消歧：WinForms 隐式 using 带入同名枚举（含 Center/Left/Right）
using Key = System.Windows.Input.Key; // 同上（Esc 收起控制中心的按键枚举）
using KeyEventArgs = System.Windows.Input.KeyEventArgs; // 同上
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Beautiful.App.TopBar;

/// <summary>
/// macOS 风格顶部菜单栏（"finder 栏"，对标 MyDockFinder 顶栏）：主屏顶部全宽 <see cref="BarHeightDip"/> DIP
/// 条带，ABE_TOP AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS），最大化窗口自动让位顶栏+Dock 双条带。
/// <para>
/// 【形态（v3 T2/T3）】<see cref="AcrylicWindow"/> 分层窗口：<c>AllowsTransparency=true</c> +
/// <c>Background={x:Null}</c>，条带矩形由 XAML 三层自绘底（tint + 顶部内高光 + 下缘亮边）铺满，
/// 不留任何未绘制像素；<see cref="ApplyTheme"/> 只写一次 <c>AccentState.Disabled</c>，用于清除 v3.0.0
    /// 留在本窗口上的旧亚克力状态（v3.1：那条"best-effort 系统模糊增强"在 Win11 24H2/25H2 上会把 tint
    /// 平铺满整个窗口矩形，已从 UI 路径删除）。
/// v2 的"非分层 + <c>DWMWA_SYSTEMBACKDROP_TYPE</c>"已在真机上被证伪（条带矩形内露桌面），已退役。
/// </para>
/// <para>
/// 【Task 9：菜单与状态项】左区 = logo（Beautiful 应用菜单）+ 前台应用名（粗体）+ 文件/编辑/视图/窗口/帮助；
/// 右区 = 媒体迷你 + 时钟 + 音量 + 网络 + 电量。全部菜单/浮层经 <see cref="FluentMenu"/>/
/// <see cref="FluentPopupHost"/> 承载（共享 macOS 质感与背板规则）。依赖（跟踪器/媒体 VM/系统动作）经
/// <see cref="TopBarContext"/> 注入，顶栏不持有 Dock 生命周期。
/// 状态项配色/字体走 DynamicResource（换主题即时生效，无需重建）；网络/电量共用一条每分钟
/// <see cref="DispatcherTimer"/>（常驻表面，无逐秒分配）；时钟单独一条，步长与边界对齐由
/// <see cref="Core.TopBar.ClockFormat"/> 按"是否显示秒"决定（v3.1 缺陷 3 的定时器纪律）。
/// 前台应用名沿用 WinEventHook + 2s 对账（Task 8）。
/// </para>
/// </summary>
public partial class TopBarWindow : AcrylicWindow
{
    private const double BarHeightDip = 24; // macOS 顶栏视觉高度（DIP；AppBar 条带同值）
    private const int IconFontDip = 13;

    /// <summary>
    /// "点顶栏图标收起控制中心"的**重开抑制窗口**（毫秒）：面板失活后由看门狗（40ms × 2 拍 ≈ 80ms）
    /// 关掉，而按钮的 Click 可能落在这之后。取值只要覆盖"按下→关闭落地→抬起"这段真实点击时长即可，
    /// 这里给 250ms（比慢速双击的下限还短，不会被当成"用户又点了一次"）。见 <see cref="ToggleControlCenter"/>。
    /// </summary>
    private const int ReopenGuardMs = 250;

    private readonly TopBarContext _context;

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;

    /// <summary>`Beautiful.exe --exit` 的注册消息号（0 = 注册失败，等于没有这条通道）。</summary>
    private readonly uint _exitRequestMsg = NativeMethods.ExitRequestMessage;
    private NativeMethods.APPBARDATA _abd;
    private bool _appbarRegistered;
    private bool _closing;
    private HwndSource? _source;
    private NativeMaterialHost? _nativeMaterial;
    // 最近一次协商出的**目标窗口矩形**（物理 px）。shell 在条带重排时会把顶栏窗口摆到"当前工作区顶边"
    // ——也就是我们自己预留带的下方一个条带高（本机实测漂到 (0,30)-(1024,60)），这就是用户投诉的
    // "顶栏不在最上方、上方露一截壁纸"。收到通知后按这个值重申一次，见 AssertWindowRect。
    private PhysicalRect _windowTarget;

    // 前台应用名跟踪（Task 8；Task 11 起改用进程内共享的 ForegroundWatcher，不再自建第二条同款钩子）
    private IDisposable? _foregroundSubscription;
    private DispatcherTimer? _fallbackTimer;
    private IntPtr _lastFgHwnd;
    private string _lastAppName = "";
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    // 最近一次读到的**非本进程**前台句柄：本进程窗口（顶栏自身/弹层）从不覆盖它，
    // 因此它是"用户真正在用的窗口"的可信来源，供 ForegroundSnapshotForMenu 在抢焦前兜底。
    private IntPtr _lastForeignFgHwnd;
    // 菜单构造时使用的前台快照（PreviewMouseDown 取得），见 ForegroundHandle 注释。
    private IntPtr _menuFgSnapshot;

    // Task 9：状态项
    private DispatcherTimer? _statusTimer;   // 每分钟：网络/电量对账
    private DispatcherTimer? _clockTimer;    // 时钟专用：步长与边界对齐由 ClockFormat 决定（见 ApplyClockSettings）
    private Button? _mediaButton;
    private TextBlock? _mediaText;
    private Button? _clockButton;
    private Button? _inputButton;
    private TextBlock? _inputText;
    private System.Windows.Threading.DispatcherTimer? _inputTimer;
    private TextBlock? _clockText;
    private TextBlock? _networkText;
    private TextBlock? _batteryText;
    private MediaViewModel? _subscribedMedia;

    // v3 T7：控制中心面板（宿主 + 入口按钮 + "刚关过"抑制位；见 ToggleControlCenter 的竞态说明）
    private FluentPopupHost? _controlCenterHost;
    private Button? _controlCenterButton;

    /// <summary>自家 WiFi 面板（v3.2）：宿主与触发按钮；面板自己持有 wlanapi 句柄，关闭时 Shutdown。</summary>
    private FluentPopupHost? _wifiHost;
    private Button? _wifiButton;

    /// <summary>自家音量面板（v3.2）：持有音频端点 COM 引用，关闭时 Shutdown。</summary>
    private FluentPopupHost? _volumeHost;
    private Button? _volumeButton;

    /// <summary>托盘图标面板（v3.2）：服务层持有跨进程读 explorer 的句柄，关闭必须 Dispose。</summary>
    private FluentPopupHost? _trayHost;
    private Button? _trayButton;

    // ---------- 顶栏常驻托盘条（v3.4） ----------
    private TrayIconSource? _trayStripSource;          // 自己一份：与面板互不共享句柄生命周期
    private System.Windows.Controls.StackPanel? _trayStripHost;
    private DispatcherTimer? _trayStripTimer;
    private bool _trayStripReading;
    private System.Collections.Generic.IReadOnlyList<TrayIconItem> _trayStripItems =
        System.Array.Empty<TrayIconItem>();
    private DateTime _controlCenterClosedAt = DateTime.MinValue;

    /// <summary>
    /// "窗口"菜单的前台句柄快照：<b>只读 <see cref="_menuFgSnapshot"/>，绝不读 <see cref="_lastFgHwnd"/></b>。
    /// 评审发现（Task 9）：点顶栏按钮会让顶栏自身成为前台，<see cref="ShowMenuUnder"/> 之后的
    /// <see cref="RefreshForegroundApp"/> 又会把 <see cref="_lastFgHwnd"/> 指向弹层（甚至上一个已销毁的弹层），
    /// 于是极快连开两次"窗口"菜单时勾选项/最小化-关闭小节会指向错误句柄。快照在 <see cref="Button.PreviewMouseDown"/>
    /// （早于 Click 构造菜单）取得，且只接受**非本进程**的窗口，因此弹层/顶栏永远无法污染它。
    /// </summary>
    public IntPtr ForegroundHandle => _menuFgSnapshot;

    public TopBarWindow(TopBarContext context)
    {
        _context = context;
        InitializeComponent();
        BuildLeftRegion();
        BuildRightRegion();
        StartTrayStrip();
        // 静态事件订阅必须晚于 Build*：Build 抛异常时构造函数不会返回，调用方（App.EnsureTopBar）
        // 手里的 bar 恒为 null → catch 里的 bar?.Close() 成 no-op → 静态事件永久持有半构造窗口，
        // 之后每次换主题都对一个没有 HWND 的僵尸实例执行 ApplyTheme（与 App.xaml.cs 刚修过的同类泄漏一致）。
        // 订阅放最后：此刻 Build 已完成，后续 OnClosed 的解绑与之一一对称。
        ThemeManager.ThemeChanged += OnThemeChanged;
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
        EnsureNativeMaterial();
        ApplyTheme(ThemeManager.Current);
        StartForegroundTracking();
        StartStatusTracking();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        StopForegroundTracking();
        StopStatusTracking();
        UnregisterAppBar();
        _source?.RemoveHook(WndProc);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _nativeMaterial?.Dispose();
        _nativeMaterial = null;
        if (_subscribedMedia is not null) _subscribedMedia.PropertyChanged -= OnMediaChanged;
        _trayStripTimer?.Stop();
        _trayStripTimer = null;
        _trayStripSource?.Dispose();   // 读者不驻留句柄的纪律与 TrayPanel 同一条
        _trayStripSource = null;
        _controlCenterHost?.Close(); // 控制中心随顶栏一起收起（重载/退出时不得留一个无主的弹层）
        FluentMenu.CloseCurrent(); // 防弹层残留（重载/退出时）
        NotificationCenterWindow.CloseCurrent(); // 通知中心随顶栏收起（alpha.15：之前重载顶栏会留一个孤儿面板）
        base.OnClosed(e);
    }

    private void RegisterAndPositionAppBar()
    {
        if (_closing) return;
        if (_appbarRegistered) UnregisterAppBar();
        _abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _appbarMsg
        };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref _abd);
        _appbarRegistered = true;
        Logger.Info($"顶栏 AppBar ABM_NEW 完成 hwnd=0x{_hwnd.ToInt64():X}");
        PositionAsAppBar();
    }

    /// <summary>
    /// ABE_TOP 条带定位（v3 T1）：几何唯一来源是<b>本窗口实际所在（或设置指定）的显示器</b>的
    /// <c>rcMonitor</c> 与该屏有效 DPI（经 <see cref="MonitorGeometry"/> + <c>StripMath</c>），
    /// 顶栏窗口矩形恒等于预留带 ⇒ <c>window.top == rcMonitor.top</c> 恒成立。
    /// <para>
    /// 【顺序是硬约束：先 WPF 属性，后物理 SetWindowPos】v2 先 <c>SetWindowPos</c> 再写
    /// <c>Left/Top/Width/Height</c>，于是 WPF 在随后的布局帧里按自己的 DIP→物理换算把我们刚落好的
    /// 矩形覆盖掉——100% DPI 看不出，125%/150% 就是"顶栏上方露出一截壁纸"的那几像素。
    /// 现在四个 DIP 属性先写、<c>UpdateLayout()</c> 把重排同步做完，物理矩形最后落地。
    /// </para>
    /// <para>
    /// 【QUERYPOS 的顶边仍然尊重 shell】用户的原生任务栏若就停在屏幕顶部，那里已有顶部条带，
    /// shell 会把可用顶边下压；此时"贴边"的含义是贴 shell 允许的那条边，日志里的 <c>偏移 Npx</c>
    /// 相对 <c>rcMonitor.top</c> 如实报出来，真机上一眼能判因。
    /// </para>
    /// </summary>
    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var target = MonitorGeometry.ForEdge(_hwnd, StripEdge.Top, BarHeightDip, 0,
                                             SettingsService.Current.TopBarMonitor);
        if (target is null) return;   // 显示器取不到（拔屏瞬间）：保持上次几何，不写非法矩形
        var t = target.Value;
        var stripH = t.Reserve.Height;    // = round(BarHeightDip × 该屏 DPI)，与上报的条高同一个数

        _abd.uEdge = NativeMethods.AbeTop;
        _abd.rc = MonitorGeometry.ToNative(t.Reserve);    // 该屏全宽 + 条高（SM_CXSCREEN 已退役）
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var available = MonitorGeometry.ToCore(_abd.rc);
        // 以 shell 给的可用顶边为准重建条带（宽度取返回矩形的左右缘 = 该屏全宽）：贴边不变量随之落到
        // shell 允许的那条边上，高度恒等于期望条高（不让既有 AppBar 把顶栏压矮）。
        var reserve = StripMath.Reserve(available, stripH, StripEdge.Top);
        var expect = $"({reserve.Left},{reserve.Top})-({reserve.Right},{reserve.Bottom})";
        _abd.rc = MonitorGeometry.ToNative(reserve);
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        var placed = MonitorGeometry.ToCore(_abd.rc);     // shell 最终预留的带——窗口以它为基准
        var actual = $"({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom})";
        var window = StripMath.WindowRect(placed, 0, StripEdge.Top);   // 顶栏无越界生长：窗口 == 预留带

        Logger.Info($"顶栏 AppBar(ABE_TOP) QUERYPOS 可用顶边={available.Top}/rcMonitor顶={t.MonitorRect.Top}" +
                    $" 条高={stripH}px dpi={t.DpiScale:0.##} monitor=0x{t.Monitor.ToInt64():X}");
        // 单独一行的 "AppBar SETPOS rc=(l,t)-(r,b)"：scripts/frametime.ps1 按这条正则取条带矩形
        // （顶栏这行带着 "(ABE_TOP)" 前缀，正则不会误命中它，取到的一定是 Dock 的底边条带）。
        Logger.Info($"顶栏 条带落位 rc={actual} 期望={expect} {(actual == expect ? "一致" : "不一致")}");

        Left = StripMath.ToDip(window.Left, t.DpiScale);
        Top = StripMath.ToDip(window.Top, t.DpiScale);
        Width = Math.Max(1, StripMath.ToDip(window.Width, t.DpiScale));
        Height = Math.Max(1, StripMath.ToDip(window.Height, t.DpiScale));
        UpdateLayout();   // 把上面四个属性的重排就地做完，物理落位才真的是"最后一步"
        _windowTarget = window;
        ApplyPhysicalRect(window);

        // 回读校验（v3 §1.C）：真机若仍偏移，这一行日志就是判因证据。
        if (NativeMethods.GetWindowRect(_hwnd, out var back))
        {
            var offset = back.Top - t.MonitorRect.Top;
            Logger.Info($"顶栏几何 window.top={back.Top} rcMonitor.top={t.MonitorRect.Top}" +
                        $" {(offset == 0 ? "→ 贴边OK" : $"→ 偏移 {offset}px")}" +
                        $" 预留={actual} 窗口=({back.Left},{back.Top})-({back.Right},{back.Bottom})" +
                        $" {(SameRect(back, window) ? "落位一致" : $"落位被改写（目标 ({window.Left},{window.Top})-({window.Right},{window.Bottom})）")}");
        }
        else
        {
            Logger.Warn($"顶栏几何 回读 GetWindowRect 失败，无法校验贴边（目标 ({window.Left},{window.Top})-({window.Right},{window.Bottom})）");
        }
    }

    /// <summary>把目标矩形落到物理层（写窗口位置的唯一出口，重申路径复用它）。</summary>
    private void ApplyPhysicalRect(PhysicalRect window)
        => NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, window.Left, window.Top, window.Width, window.Height,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);

    private static bool SameRect(NativeMethods.RECT a, PhysicalRect b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>
    /// 把窗口矩形重申为 <see cref="_windowTarget"/>：只 <c>SetWindowPos</c>，<b>不</b>重发
    /// <c>ABM_SETPOS</c>——预留带没变就不会再触发 shell 重排，因此不构成"重申 → 广播 → 再重申"的环
    /// （与 Dock 里"WM_SETTINGCHANGE 不可重注册"同一条纪律）。矩形已等于目标时静默返回。
    /// <para>
    /// 【为什么必须有】本机实测：顶栏落位日志明明是 <c>window=(0,0)-(1024,30) 落位一致</c>，
    /// 随后（任务栏 autohide 状态位变化 + Dock 重新 SETPOS 之后）窗口被移到 <c>(0,30)-(1024,60)</c>
    /// ——正好是自己预留带下方一个条带高，即"按当前工作区顶边"摆的。用户看到的"顶栏上方露壁纸"
    /// 就是这个漂移，与 DPI/渲染无关，属几何侧根因，必须在几何侧兜住。
    /// </para>
    /// </summary>
    private void AssertWindowRect()
    {
        if (_closing || !_appbarRegistered) return;
        if (_windowTarget.Width <= 0 || _windowTarget.Height <= 0) return;   // 尚未定位过（首帧前）
        if (!NativeMethods.GetWindowRect(_hwnd, out var now)) return;
        if (SameRect(now, _windowTarget)) return;
        Logger.Info($"顶栏几何 窗口被移到 ({now.Left},{now.Top})-({now.Right},{now.Bottom})" +
                    $" ≠ 目标 ({_windowTarget.Left},{_windowTarget.Top})-({_windowTarget.Right},{_windowTarget.Bottom})" +
                    $"（偏移 {now.Top - _windowTarget.Top}px）→ 重申贴边");
        ApplyPhysicalRect(_windowTarget);
    }

    private void UnregisterAppBar()
    {
        if (!_appbarRegistered) return;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref _abd);
        _appbarRegistered = false;
    }

    /// <summary>
    /// 设置页改动 <c>TopBarMonitor</c> 后的即时生效入口（App.ApplyTopBarGeometry 调用）：
    /// 按新的目标显示器重算条带几何并把窗口搬过去。AppBar 句柄与回调注册不动，只重发
    /// QUERYPOS/SETPOS——与 Dock 的 <c>RefreshLayoutFromSettings</c> 同语义，但不必像 Dock 那样
    /// 防抖（显示器选择器是下拉框的离散事件，一次改动只来一发）。
    /// </summary>
    public void RefreshLayoutFromSettings()
    {
        if (_closing) return;
        PositionAsAppBar();   // 未注册时自身 no-op（OnSourceInitialized 的首帧定位已用同一套几何）
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appbarMsg != 0 && msg == _appbarMsg)
        {
            handled = true;
            // ABN_POSCHANGED：shell 重排了条带（实测会连带把本窗口摆到工作区顶边）→ 重申目标矩形。
            if (wParam.ToInt32() == NativeMethods.AbnPosChanged) Dispatcher.BeginInvoke(AssertWindowRect);
            return IntPtr.Zero;
        }
        // 安装/卸载器发的"干净退出"信号（`Beautiful.exe --exit`）：走完整退出链路，
        // 而不是被 taskkill 掉——强杀会把任务栏 autohide 位与 Alt+Tab 钩子留在系统里。
        if (_exitRequestMsg != 0 && msg == _exitRequestMsg)
        {
            handled = true;
            Logger.Info("收到退出请求（--exit），走完整退出链路");
            Dispatcher.BeginInvoke(_context.Exit);
            return IntPtr.Zero;
        }
        // 任何来源的位置变化都过一遍比对。用 BeginInvoke 排到本次落位之后执行，所以自己写的
        // SetWindowPos 在这里只命中"已等于目标"的静默分支，不会自激。
        if (msg == NativeMethods.WmWindowPosChanged) Dispatcher.BeginInvoke(AssertWindowRect);
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            handled = true;
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
            Dispatcher.BeginInvoke(RefreshTrayStrip);   // explorer 重建 → 托盘图标全体搬家，立即重读
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
        }
        return IntPtr.Zero;
    }

    // ---------- 主题 ----------

    private void OnThemeChanged(ThemeDefinition theme) => ApplyTheme(theme);

    private void ApplyTheme(ThemeDefinition theme)
    {
        if (_closing) return;
        // 顶栏是**常驻**表面：不做抓屏背板（重抓会把自身像素抓进去 = 鬼影；为抓屏先隐藏一帧 = 闪烁），
        // 也绝不依赖系统是否画得出模糊。可见底恒由 XAML 的 BarBackground/BarGlow/BarEdge 三层铺满整条
        // （DynamicResource 随主题即时变色），渲染侧不再有"背板胜出/失败"两种形态。
        // v3.1：这里唯一的系统调用是清掉 v3.0.0 遗留的旧亚克力状态（缺陷 1：Win11 上它把 tint 平铺满
        // 整个窗口矩形）。theme 参数仍是本方法的语义入口（换主题即重落底色），底色值本身走 XAML 的
        // DynamicResource，不需要在这里搬运。
        ClearLegacyAccent("顶栏");
        UpdateNativeMaterial();
    }

    /// <summary>native 背板（Phase 2 T3）：探针可用才建；不可用保留 XAML 静态三层底。</summary>
    private void EnsureNativeMaterial()
    {
        if (_nativeMaterial is not null || _hwnd == IntPtr.Zero) return;
        var tokens = MacTokens.For(ThemeManager.IsDark);
        _nativeMaterial = new NativeMaterialHost(
            this, MacTokenDictionary.ParseArgb(tokens.MenuBarBackground), 1f, 0f,
            ThemeManager.IsDark, "menubar");
    }

    private void UpdateNativeMaterial()
    {
        EnsureNativeMaterial();
        if (_nativeMaterial is null) return;
        var tokens = MacTokens.For(ThemeManager.IsDark);
        _nativeMaterial.SetTheme(MacTokenDictionary.ParseArgb(tokens.MenuBarBackground), 1f, ThemeManager.IsDark);
        // 背板胜出时把 WPF 的静态底色让出来（保留 Glow/Edge 等细线层）；失败则恢复实心底。
        BarBackground.Opacity = _nativeMaterial.IsActive ? 0 : 1;
    }

    // ---------- 前台应用名（Task 8） ----------

    private void StartForegroundTracking()
    {
        // 共享钩子（ForegroundWatcher）+ 2s 兜底对账：钩子漏报（如跨桌面/权限窗口）时定时器仍能纠正。
        _foregroundSubscription = ForegroundWatcher.Subscribe(_ => RefreshForegroundApp());
        _fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fallbackTimer.Tick += (_, _) => RefreshForegroundApp();
        _fallbackTimer.Start();
        RefreshForegroundApp();
    }

    private void StopForegroundTracking()
    {
        if (_fallbackTimer is not null)
        {
            _fallbackTimer.Stop();
            _fallbackTimer = null;
        }
        _foregroundSubscription?.Dispose(); // 退订（最后一个订阅者退订时共享钩子才卸载）
        _foregroundSubscription = null;
    }

    public void RefreshForegroundApp()
    {
        if (_closing) return;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _lastFgHwnd) return;
        _lastFgHwnd = fg;
        if (IsForeignWindow(fg)) _lastForeignFgHwnd = fg;   // 菜单快照的可信来源（本进程窗口不参与）

        string name;
        if (fg == IntPtr.Zero) name = "Beautiful";
        else
        {
            NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            if (pid == _ownPid)
            {
                name = "Beautiful";
            }
            else
            {
                var path = pid != 0 ? NativeMethods.GetProcessPath(pid) : null;
                name = path is { Length: > 0 }
                    ? Path.GetFileNameWithoutExtension(path)
                    : FallbackWindowTitle(fg);
            }
        }
        if (name == _lastAppName) return;
        _lastAppName = name;
        ForegroundAppText.Text = name;
    }

    private static string FallbackWindowTitle(IntPtr hwnd)
    {
        // 走 WindowEligibility 的统一收口：内含 IsHungAppWindow 早退，
        // 避免向可能挂死的前台线程 SEND 读标题冻住我们自己的 UI 线程（终审 I-7 同类残留）。
        var title = Services.WindowEligibility.GetWindowTitle(hwnd).Trim();
        return title.Length > 0 ? title : "Beautiful";
    }

    /// <summary>该窗口是否属于其它进程（本进程的顶栏/弹层/面板一律返回 false）。</summary>
    private bool IsForeignWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return tid != 0 && pid != _ownPid;
    }

    /// <summary>
    /// 取"弹出前真实前台"快照：<br/>
    /// ①当前前台属其它进程 → 顶栏/弹层还没抢到焦点，直接采用（最新）；<br/>
    /// ②否则（点顶栏按钮后前台已是本进程的顶栏或弹层）→ 回退到最近一次外部前台。<br/>
    /// 弹层显示后 <see cref="RefreshForegroundApp"/> 会把 <see cref="_lastFgHwnd"/> 指向弹层乃至已销毁的弹层，
    /// 而本快照只接受外部窗口，故"窗口"菜单的勾选/最小化/关闭目标不会被污染（评审发现 2）。
    /// </summary>
    private void TakeForegroundSnapshot()
    {
        var fg = NativeMethods.GetForegroundWindow();
        _menuFgSnapshot = IsForeignWindow(fg) ? fg : _lastForeignFgHwnd;
    }

    // ---------- 左区：logo + 菜单（Task 9） ----------

    private void BuildLeftRegion()
    {
        // logo（Beautiful 应用菜单）前插到应用名之前
        LeftHost.Children.Insert(0, MakeLogoButton());

        // 菜单按钮追加到应用名之后（文件/编辑/视图/窗口/帮助）
        LeftHost.Children.Add(MakeMenuButton("文件", () => TopBarMenus.FileMenu()));
        LeftHost.Children.Add(MakeMenuButton("编辑", () => TopBarMenus.EditMenu()));
        LeftHost.Children.Add(MakeMenuButton("视图", () => TopBarMenus.ViewMenu(_context.Tracker())));
        LeftHost.Children.Add(MakeMenuButton("窗口", () => TopBarMenus.WindowMenu(_context.Tracker(), ForegroundHandle)));
        LeftHost.Children.Add(MakeMenuButton("帮助", () => TopBarMenus.HelpMenu()));

        // 前台应用名：macOS "应用菜单"位。无跨应用控制能力，按裁定保持非交互（仅跟随显示应用名），
        // 应用级操作集中在 logo 菜单——见 task-9-report 决策说明。
    }

    private Button MakeLogoButton()
    {
        // 原创圆形强调色标记（非 Apple 商标）：强调色圆底 + 白色 "B"，随主题变色（SetResourceReference）。
        var mark = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            Child = new TextBlock
            {
                Text = "B",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
        mark.SetResourceReference(Border.BackgroundProperty, "ThemeAccent");
        var b = NewBarButton(mark);
        b.Margin = new Thickness(0, 0, 6, 0);
        b.Click += (_, _) => ShowMenuUnder(b, TopBarMenus.BeautifulMenu(_context));
        return b;
    }

    private Button MakeMenuButton(string label, Func<System.Collections.Generic.List<MenuItemView>> build)
    {
        var b = NewBarButton(new TextBlock { Text = label, FontSize = 12 });
        // 早于 Click 构造菜单的一刻：鼠标按下即记录前台快照（评审发现 2 的修复点）。
        b.PreviewMouseDown += (_, _) => TakeForegroundSnapshot();
        // build() 在 FluentMenu.Show 之前求值，故此处再取一次：键盘（Enter/Space）触发时没有 PreviewMouseDown。
        b.Click += (_, _) => { TakeForegroundSnapshot(); ShowMenuUnder(b, build()); };
        return b;
    }

    private Button NewBarButton(object content)
        => new() { Content = content, Style = (Style)FindResource("BarButton") };

    /// <summary>在按钮下缘（物理像素锚点）向下弹 FluentMenu。顶栏贴屏幕顶边 → 弹层自然向下展开（不会上翻出屏）。</summary>
    /// <summary>点前台应用名 = 弹出该应用的真实 Win32 菜单（P4-T3）；快照取在菜单显示前。</summary>
    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        TakeForegroundSnapshot();
        var fg = ForegroundHandle;
        if (fg == IntPtr.Zero) return;
        ShowMenuUnder(AppMenuButton, TopBarMenus.AppMenu(fg, _lastAppName));
    }

    private void ShowMenuUnder(FrameworkElement anchor, System.Collections.Generic.List<MenuItemView> items)    {
        var p = anchor.PointToScreen(new Point(0, anchor.ActualHeight)); // 设备像素
        FluentMenu.Show(new FluentPopupHost(), new Point(p.X, p.Y), items, item => item.Invoke?.Invoke());
        // 菜单已显示：此后前台变成弹层自身。这里仍刷新应用名（让 UI 与真实前台一致），
        // 但**不再**是"窗口"菜单的判定来源——菜单用的是显示前的 _menuFgSnapshot（评审发现 2）。
        RefreshForegroundApp();
    }

    // ---------- 右区：状态项（Task 9） ----------

    private void BuildRightRegion()
    {
        // 媒体迷你（无会话隐藏）
        (_mediaButton, _mediaText) = MakeStatusItem("媒体", iconFont: false);
        _mediaButton.Visibility = Visibility.Collapsed;
        _mediaText.MaxWidth = 190;
        _mediaButton.Click += (_, _) => { _context.OpenMediaPanel(); };
        RightHost.Children.Add(_mediaButton);

        // 顶栏常驻托盘条（v3.4，用户点名要 macOS 菜单栏的后台图标）。v3.2 裁定"点开才读"的理由是
        // 经典路从屏幕抠像素、常驻就要持续抓屏；现在的口径是：读数 45s 一次 + TaskbarCreated 即时
        // （与"每帧抓屏"不是一个量级），条带只显示读得到的东西，一格都没有就整体隐藏、
        // 并把两路读数写进日志——下次这台机器上读不到时，日志自己会说出原因。
        // 像素抠不到（任务栏被本程序移出屏幕时屏幕上没有那一格）就画占位字形 + 应用名 tooltip：
        // 看得见、点得动，好过看不见。
        _trayStripSource = new TrayIconSource();
        _trayStripHost = new StackPanel { Orientation = Orientation.Horizontal };
        _trayStripHost.Visibility = Visibility.Collapsed;
        RightHost.Children.Add(_trayStripHost);

        // 时钟（形态与步长见 ApplyClockSettings；点击 → 日历浮层）。
        // 【宽度变化不跳动】RightHost 是 HorizontalAlignment=Right 的横向栈：时钟变宽/变窄只会让它在
        // **左侧**扩张，右邻（音量/网络/电量/启动器/控制中心）始终钉在条带右缘不动。因此"显示秒"这种
        // 增减 3 个字符的改动不会让整个状态区左右抖动，唯一会位移的是最左的媒体迷你，而它有 MaxWidth 钳制。
        // 输入法指示（P4-T10）：时钟左侧一个小徽标（中 / EN / あ）。只读 InputLanguageManager，
        // 不可用就显示 EN——不猜、不写假状态。
        (_inputButton, _inputText) = MakeStatusItem("输入法", iconFont: false);
        _inputButton.ToolTip = "当前输入法";
        _inputButton.Click += (_, _) => OpenInputSettings();
        StartInputBadge();

        (_clockButton, _clockText) = MakeStatusItem("时钟", iconFont: false);
        // 左键 = 通知中心（Big Sur 语义）；右键保留原来的月历浮层（不想读系统通知时的轻量入口）
        _clockButton.Click += (_, _) => OpenClockFlyout();
        _clockButton.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenCalendar();
        };
        RightHost.Children.Add(_clockButton);

        // 音量（点击 → 自家音量面板；原来是 sndvol.exe，整块 UI 都是 Windows 的）
        var (vol, _) = MakeStatusItem("\uE767", iconFont: true); // Segoe MDL2 Volume
        _volumeButton = vol;
        vol.Click += Volume_Click;
        RightHost.Children.Add(vol);

        // 网络（点击 → 自家 WiFi 面板；状态由每分钟定时器对账）
        var (net, netText) = MakeStatusItem("\uE701", iconFont: true); // Segoe MDL2 Globe
        _networkText = netText;
        _wifiButton = net;
        net.Click += Wifi_Click;
        RightHost.Children.Add(net);

        // 电量（仅存在电池时显示；每分钟刷新）
        var (bat, batText) = MakeStatusItem("", iconFont: false);
        _batteryText = batText;
        bat.Visibility = Visibility.Collapsed;
        RightHost.Children.Add(bat);

        // 托盘图标区入口（v3.2）：点开自家面板，把通知区域（含"隐藏的图标"）里的第三方图标平铺出来。
        // 刻意<b>不</b>做"常驻在顶栏上直接排开图标"：经典读取路是从屏幕截图上抠图标，常驻就意味着
        // 要么持续跨进程读 explorer、要么把 Windows 自己的隐藏区展开来抠——后者会在顶栏上留下
        // 一次可见的展开动作，与"常驻表面不做实时抓屏"的裁定冲突。点开才读、读一次就停。
        if (SettingsService.Current.ShowTrayInTopBar)
        {
            var (tray, _) = MakeStatusItem("\uE91C", iconFont: true); // Segoe Fluent: 向上飞轮（与 Windows 隐藏区同语义）
            AutomationProperties.SetName(tray, "托盘图标");
            tray.ToolTip = "托盘图标：通知区域里被收起的应用";
            _trayButton = tray;
            tray.Click += Tray_Click;
            RightHost.Children.Add(tray);
        }

        // 启动器入口（v3 T7，放大镜字形与搜索语义一致）：放在控制中心<b>左侧</b>而不是最右——
        // 最右一位是"快捷设置"的既有位置（macOS/Win11 都是），新入口不该去抢它，
        // 而验收脚本也是按"控制中心"这个名字点最右一颗的（位置口径不变）。
        // ToolTip 里刻意不写热键：热键是设置项，改了顶栏按钮不会被重建，写上去就会变成过期承诺。
        var (launcherButton, _) = MakeStatusItem("\uE721", iconFont: true);
        AutomationProperties.SetName(launcherButton, "启动器");
        launcherButton.ToolTip = "启动器：搜索并启动应用";
        launcherButton.Click += (_, _) => _context.OpenLauncher();
        RightHost.Children.Add(launcherButton);

        // 控制中心入口（v3 T7，最右一位 = macOS/Win11 的"快捷设置"位置）：
        // 字形取已在托盘/顶栏菜单里验证过能渲染的 \uE790（与状态项同一套 Segoe 图标字体 + 同一字号），
        // 打开态用样式同款的主题面板底色做持久高亮（见 ToggleControlCenter）。
        var (controlCenter, _) = MakeStatusItem("\uE790", iconFont: true);
        _controlCenterButton = controlCenter;
        AutomationProperties.SetName(controlCenter, "控制中心");
        controlCenter.ToolTip = "控制中心";
        controlCenter.Click += (_, _) => ToggleControlCenter();
        RightHost.Children.Add(controlCenter);

        AttachMedia(_context.Media());
        UpdateClock();
        UpdateNetwork();
        UpdateBattery();
    }

    /// <summary>创建一个扁平状态按钮（内含文本块）。<paramref name="iconFont"/>=true 用 Segoe 图标字体渲染 <paramref name="text"/>。</summary>
    private (Button button, TextBlock text) MakeStatusItem(string text, bool iconFont)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = iconFont ? IconFontDip : 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (iconFont)
            tb.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var b = NewBarButton(tb);
        return (b, tb);
    }

    // ---------- 顶栏常驻托盘条（v3.4） ----------

    /// <summary>后台读两路 → 回投 → 渲染。45s 一拍 + TaskbarCreated 即时；与面板互不干扰（各自一份 source）。</summary>
    private async void RefreshTrayStrip()
    {
        if (_trayStripReading || _trayStripSource is null) return;
        _trayStripReading = true;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => _trayStripSource.Read());
            if (!IsLoaded) return;
            _trayStripItems = result.Items;
            RenderTrayStrip();
            // 空读必须留痕：这台机器（24H2 + 任务栏被移出屏幕）上两路各自读到了什么，
            // 是"托盘条为什么是空的"的第一手判据——用户能提供的只有截图，截图里没有日志。
            if (result.Items.Count == 0)
                Logger.Info($"顶栏托盘条：空读（经典路[{result.ClassicStatus}] UIA[{result.UiaStatus}]）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"顶栏托盘条读取异常: {ex.Message}");
        }
        finally
        {
            _trayStripReading = false;
        }
    }

    private void RenderTrayStrip()
    {
        if (_trayStripHost is null) return;
        _trayStripHost.Children.Clear();
        var visible = _trayStripItems.Take(8).ToList();
        if (visible.Count == 0)
        {
            _trayStripHost.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var item in visible) _trayStripHost.Children.Add(MakeTrayStripTile(item));
        _trayStripHost.Visibility = Visibility.Visible;
    }

    /// <summary>一格常驻图标：有像素贴像素，没有画占位字形（首字），tooltip 永远写应用名与来源。</summary>
    private Button MakeTrayStripTile(TrayIconItem item)
    {
        object content;
        if (item.Image is { } img)
        {
            content = new System.Windows.Controls.Image
                { Source = img, Width = 20, Height = 20, Stretch = Stretch.Uniform };
        }
        else
        {
            content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Label) ? "·" : item.Label.Trim().Substring(0, 1),
                FontSize = 12,
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var tile = NewBarButton(content);
        tile.Width = 32;
        tile.ToolTip = string.IsNullOrWhiteSpace(item.Label)
            ? $"托盘图标（{item.Origin}，应用未提供名称）"
            : $"{item.Label}（{item.Origin}）";
        tile.Opacity = item.CanActivate ? 1.0 : 0.55;
        AutomationProperties.SetName(tile, string.IsNullOrWhiteSpace(item.Label)
            ? "托盘图标（应用没给名字）"
            : $"托盘图标 {item.Label}");
        tile.Click += async (_, _) =>
        {
            var (ok, error) = await System.Threading.Tasks.Task.Run(
                () => _trayStripSource!.Activate(item, Beautiful.Core.Tray.TrayClickAction.LeftClick));
            if (!ok) Logger.Warn($"托盘条转发失败: {error ?? "无原因"}");
        };
        tile.MouseRightButtonUp += async (_, _) =>
        {
            var (ok, error) = await System.Threading.Tasks.Task.Run(
                () => _trayStripSource!.Activate(item, Beautiful.Core.Tray.TrayClickAction.RightClick));
            if (!ok) Logger.Warn($"托盘条右键转发失败: {error ?? "无原因"}");
        };
        return tile;
    }

    private void StartTrayStrip()
    {
        if (!SettingsService.Current.ShowTrayInTopBar) return;
        // 起两拍再读：顶栏刚立时 explorer 侧的通知区域可能还没就位，立即读只会拿到一次空样本。
        _trayStripTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _trayStripTimer.Tick += (_, _) => RefreshTrayStrip();
        _trayStripTimer.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, RefreshTrayStrip);
    }

    // ---------- 右区：状态刷新（网络/电量每分钟一条定时器；时钟单独一条，秒显时才逐秒） ----------

    private void StartStatusTracking()
    {
        // 终审 M-1：首帧对齐分钟边界——启动时刻若在 mm:50，固定 1 分钟间隔会让状态到 mm+1:50 才跳，
        // 最多累计滞后约 2 分钟。首 Interval 只等到下一整分（DispatcherTimer 每次 Tick 按当前
        // Interval 重排），随后固定 1 分钟，此后每分钟整点刷新。
        // v3.1：时钟从这条定时器里**拆出去**单独走（见 RestartClockTimer），因为它现在可能要逐秒刷新，
        // 而网络/电量逐秒去查是纯浪费（电量是 P/Invoke、网络是枚举网卡）。
        _statusTimer = new DispatcherTimer { Interval = SecondsToNextMinuteBoundary() };
        _statusTimer.Tick += (_, _) =>
        {
            if (_statusTimer is not null) _statusTimer.Interval = TimeSpan.FromMinutes(1);
            UpdateNetwork(); UpdateBattery();
        };
        _statusTimer.Start();
        ApplyClockSettings();   // 建/重排时钟定时器（顺带把文本立刻画出来，首帧不空白）
    }

    private void StopStatusTracking()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    /// <summary>
    /// 设置页勾选"秒/日期/星期/12 小时制"后的即时生效入口（App.ApplyTopBarClock 调用）：
    /// 文本马上重算 + 定时器按新形态重排步长与对齐。顶栏关闭时没有实例，下次创建由
    /// <see cref="OnSourceInitialized"/> 经同一方法取当前设置，因此不需要额外通知。
    /// </summary>
    public void ApplyClockSettings()
    {
        if (_closing || _clockText is null) return;
        var format = ClockFormat.From(SettingsService.Current);
        UpdateClock();
        RestartClockTimer(format);
        Logger.Info($"顶栏时钟形态 秒={format.ShowSeconds} 日期={format.ShowDate} 星期={format.ShowWeekday} " +
                    $"24小时制={format.Use24Hour} 步长={format.TickSeconds}s 模板={format.Pattern}");
    }

    /// <summary>
    /// 时钟定时器：<b>首个 Interval 是"到下一个边界的余量"</b>（显示秒 → 下一整秒，关秒 → 下一整分），
    /// 落地后即归到 <see cref="ClockFormat.TickSeconds"/>。因此：
    /// ① 关秒时绝不会逐秒醒（v2"定时器对齐"的既有裁定不回归）；
    /// ② 开秒时也不会变成 100ms 轮询——一秒一次、且正好压在秒边界上；
    /// ③ 勾选变化当场重排（<see cref="DispatcherTimer.Start"/> 会按新 Interval 重新开始计时）。
    /// </summary>
    private void RestartClockTimer(ClockFormat format)
    {
        if (_clockTimer is null)
        {
            _clockTimer = new DispatcherTimer();
            _clockTimer.Tick += (_, _) =>
            {
                // 每拍重读形态：步长永远与"是否显示秒"一致，不必额外搭一条设置变更通知。
                var f = ClockFormat.From(SettingsService.Current);
                if (_clockTimer is not null) _clockTimer.Interval = TimeSpan.FromSeconds(f.TickSeconds);
                UpdateClock();
            };
        }
        var now = DateTime.Now;
        _clockTimer.Interval = format.ShowSeconds
            ? TimeSpan.FromMilliseconds(1000 - now.Millisecond)                    // 到下一整秒（1..1000ms）
            : SecondsToNextMinuteBoundary();                                       // 到下一整分
        _clockTimer.Start();
    }

    /// <summary>到下一个分钟边界的余量（毫秒级精度，避免 60-Second 那种"整分还差半秒也要等下一拍"的偏差）。</summary>
    private static TimeSpan SecondsToNextMinuteBoundary()
    {
        var now = DateTime.Now;
        var rest = TimeSpan.FromSeconds(59 - now.Second) + TimeSpan.FromMilliseconds(1000 - now.Millisecond);
        // 恰好压在分边界上时余量为 0，DispatcherTimer 的 Interval 必须 > 0。
        return rest <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : rest;
    }

    private void UpdateClock()
    {
        if (_clockText is null) return;
        // 折算全在 Core 的纯函数里（模板 + 渲染），顶栏只负责把结果写进 TextBlock。
        _clockText.Text = ClockFormatter.Format(DateTime.Now, ClockFormat.From(SettingsService.Current).Pattern);
    }

    private void UpdateNetwork()
    {
        if (_networkText is null) return;
        bool on;
        try { on = NetworkInterface.GetIsNetworkAvailable(); }
        catch { on = true; }
        _networkText.Opacity = on ? 1.0 : 0.4;
        _networkText.ToolTip = on ? "网络已连接" : "网络不可用";
    }

    private void UpdateBattery()
    {
        if (_batteryText is null) return;
        if (!NativeMethods.GetSystemPowerStatus(out var sps) ||
            sps.BatteryFlag is NativeMethods.BatteryFlagNoBattery or NativeMethods.BatteryFlagUnknown)
        {
            // 无电池（台式机/虚拟机）→ 隐藏电量项
            if (_batteryText.Parent is FrameworkElement fe) fe.Visibility = Visibility.Collapsed;
            return;
        }
        int pct = sps.BatteryLifePercent == NativeMethods.BatteryPercentUnknown ? 0 : sps.BatteryLifePercent;
        bool charging = sps.ACLineStatus == 1 || (sps.BatteryFlag & NativeMethods.BatteryChargingBit) != 0;
        _batteryText.Text = charging ? $"{pct}% \u26A1" : $"{pct}%";
        _batteryText.ToolTip = charging ? $"电量 {pct}%（充电中）" : $"电量 {pct}%";
        if (_batteryText.Parent is FrameworkElement fe2) fe2.Visibility = Visibility.Visible;
    }

    // ---------- 媒体迷你 ----------

    private void AttachMedia(MediaViewModel? vm)
    {
        if (ReferenceEquals(_subscribedMedia, vm)) return;
        if (_subscribedMedia is not null) _subscribedMedia.PropertyChanged -= OnMediaChanged;
        _subscribedMedia = vm;
        if (vm is not null) vm.PropertyChanged += OnMediaChanged;
        ApplyMediaState();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name is nameof(MediaViewModel.HasSession) or nameof(MediaViewModel.TrackTitle)
            or nameof(MediaViewModel.Artist) or nameof(MediaViewModel.CapsuleText))
            ApplyMediaState();
    }

    private void ApplyMediaState()
    {
        if (_mediaButton is null || _mediaText is null) return;
        var vm = _subscribedMedia;
        if (vm is null || !vm.HasSession)
        {
            _mediaButton.Visibility = Visibility.Collapsed; // 无 SMTC 会话：整项隐藏
            return;
        }
        var line = vm.CapsuleText;
        if (string.IsNullOrWhiteSpace(line))
            line = string.IsNullOrWhiteSpace(vm.Artist) ? vm.TrackTitle : $"{vm.Artist} – {vm.TrackTitle}";
        _mediaText.Text = line;
        _mediaButton.ToolTip = line;
        _mediaButton.Visibility = Visibility.Visible;
    }

    // ---------- 时钟点击：通知中心 / 日历浮层 ----------

    /// <summary>
    /// 输入法指示（P4-T10）：1s 一次读 <see cref="System.Windows.Input.InputLanguageManager"/>。
    /// 语言切换是通过焦点窗口生效的，没有"语言变化"事件可订阅（InputLanguageChanged 只在自家窗口收到），
    /// 所以轮询是最省事且不会漏的方式；一次读取是纯内存操作，1s 一拍几乎无成本。
    /// </summary>
    private void StartInputBadge()
    {
        UpdateInputBadge();
        _inputTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => UpdateInputBadge(), Dispatcher);
        _inputTimer.Start();
    }

    private void UpdateInputBadge()
    {
        if (_inputText is null) return;
        var badge = Core.TopBar.StatusTiles.InputBadge(
            System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage?.Name);
        if (_inputText.Text != badge) _inputText.Text = badge;
    }

    /// <summary>点输入法徽标：打开 Windows 的"语言和区域"设置（我们没有自绘的输入法选择器）。</summary>
    private void OpenInputSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:keyboard") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开输入法设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 时钟左键：默认打开通知中心（Phase 4-T5）；设置里关掉后回退月历浮层。
    /// 面板自己处理"再点一次收起"（见 NotificationCenterWindow 的关闭时间戳）。
    /// <para>
    /// alpha.15：套上与 <see cref="OpenInputSettings"/> 同款的兜底——这是顶栏附属面板里唯一一条
    /// 没有本地 try/catch 的链路，任何环境性异常都会升级成全局"发生界面异常"弹框
    /// （用户反馈：点时钟报错）。现在：异常记日志并回退月历浮层，时钟永不白点。
    /// </para>
    /// </summary>
    private void OpenClockFlyout()
    {
        try
        {
            if (!SettingsService.Current.NotificationCenterEnabled || _clockButton is null)
            {
                OpenCalendar();
                return;
            }

            var p = _clockButton.PointToScreen(new Point(0, _clockButton.ActualHeight));
            NotificationCenterWindow.ShowOrToggle(Services.NotificationService.Current, p);
        }
        catch (Exception ex)
        {
            Logger.Error("时钟点击：通知中心打开失败，回退月历浮层", ex);
            OpenCalendar();
        }
    }

    private void OpenCalendar()
    {
        try
        {
            if (_clockButton is null) return;
            var p = _clockButton.PointToScreen(new Point(0, _clockButton.ActualHeight));
            var host = new FluentPopupHost { Content = new CalendarFlyout() };
            // 与 FluentMenu 同源存活语义：弹层持有前台激活即保活（否则光标仍停在上方时钟按钮、
            // 落在弹层矩形外 → 默认看门狗 80ms 内即自关）。失活（点别处）后回退矩形判定自然关闭。
            host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
            host.ShowAt(new Point(p.X, p.Y));
        }
        catch (Exception ex)
        {
            // 日历浮层也失败就不再回退（防递归/防连环弹框）：记日志、静默，时钟本身仍可用
            Logger.Error("时钟点击：月历浮层打开失败", ex);
        }
    }

    // ---------- 控制中心（v3 T7） ----------

    /// <summary>
    /// 切换控制中心面板：没开则开、开着则关。落位/翻转/背板全部交给 <see cref="FluentPopupHost.ShowAt"/>
    /// （与日历浮层、托盘菜单同一套锚点显示器 DPI + 工作区钳制 + 边缘翻转，绝不会出屏）。
    /// <para>
    /// 【"再点一次收起"的竞态与抑制】面板是激活式弹层：按下顶栏按钮的那一刻按钮所在窗口成为前台，
    /// 面板失活 → 看门狗（40ms × 2 拍）把它关掉，而 <c>Click</c> 可能在它之后才到。若此时按
    /// "<c>_controlCenterHost</c> 已是 null"去判断，就会**把刚被收起的面板又开一遍**（用户看到的是
    /// "点了没反应"）。因此记住最近一次关闭时刻，<see cref="ReopenGuardMs"/> 内的这次点击只认作
    /// "收起"，不重开；超过该窗口后照常开合。两条路径都以"屏上不再留面板"收尾。
    /// </para>
    /// <para>
    /// 【生命周期对称】本方法是唯一创建点：面板与宿主都构造完成后才接线；关闭回调里先清字段、
    /// 再解 Esc 按键挂点、最后 <see cref="ControlCenterPanel.Shutdown"/> 释放音频 COM 引用。
    /// 面板与顶栏之间只有"按钮 → 宿主"这一条正向引用与一条宿主上的实例事件，**没有任何静态挂点**，
    /// 宿主关闭后整条对象图可回收（10 次开合的句柄/工作集取证见提交说明）。
    /// </para>
    /// </summary>
    private void ToggleControlCenter()
    {
        if (_controlCenterHost is { } open)
        {
            open.Close();
            return;
        }
        if ((DateTime.UtcNow - _controlCenterClosedAt).TotalMilliseconds < ReopenGuardMs) return;
        OpenControlCenter();
    }

    /// <summary>
    /// 网络图标点击：已开即收（与控制中心一致的"再点一下关掉"语义），否则在按钮下缘开自家 WiFi 面板。
    /// 原来这里直接 <c>ms-settings:network-status</c> ——用户明确要求"单独做一个 wifi 栏，不要使 windows 的东西"。
    /// </summary>
    private void Wifi_Click(object sender, RoutedEventArgs e)
    {
        if (_wifiHost is { IsLoaded: true }) { _wifiHost.Close(); return; }
        OpenWifiPanel();
    }

    private void OpenWifiPanel()
    {
        if (_wifiButton is null) return;
        // 锚点 = 按钮下缘的设备像素；ShowAt 按**可见卡片**对齐（v3.2 修掉了投影留白被算进偏移的错位）
        var anchor = _wifiButton.PointToScreen(new Point(0, _wifiButton.ActualHeight));
        var host = new FluentPopupHost();
        var panel = new WifiPanel();
        panel.AttachHost(host);
        host.Content = panel;
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.Closed += (_, _) =>
        {
            _wifiHost = null;
            _wifiButton?.ClearValue(Control.BackgroundProperty);
            panel.Shutdown();          // 释放 wlanapi 句柄（幂等）
            Logger.Info("WiFi 面板已关闭");
        };
        _wifiHost = host;
        _wifiButton.SetResourceReference(Control.BackgroundProperty, "ThemePanelBackground");
        host.ShowAt(anchor);
        Logger.Info("WiFi 面板已打开");
    }

    /// <summary>音量图标点击：已开即收，否则开自家音量面板（替掉原来的 sndvol.exe）。</summary>
    private void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (_volumeHost is { IsLoaded: true }) { _volumeHost.Close(); return; }
        OpenVolumePanel();
    }

    private void OpenVolumePanel()
    {
        if (_volumeButton is null) return;
        var anchor = _volumeButton.PointToScreen(new Point(0, _volumeButton.ActualHeight));
        var host = new FluentPopupHost();
        var panel = new VolumePanel();
        panel.AttachHost(host);
        host.Content = panel;
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.Closed += (_, _) =>
        {
            _volumeHost = null;
            _volumeButton?.ClearValue(Control.BackgroundProperty);
            panel.Shutdown();          // 归还音频端点 COM 引用（幂等）
            Logger.Info("音量面板已关闭");
        };
        _volumeHost = host;
        _volumeButton.SetResourceReference(Control.BackgroundProperty, "ThemePanelBackground");
        host.ShowAt(anchor);
        Logger.Info("音量面板已打开");
    }

    /// <summary>托盘按钮点击：已开即收，否则开托盘图标面板。</summary>
    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        if (_trayHost is { IsLoaded: true }) { _trayHost.Close(); return; }
        OpenTrayPanel();
    }

    private void OpenTrayPanel()
    {
        if (_trayButton is null) return;
        var anchor = _trayButton.PointToScreen(new Point(0, _trayButton.ActualHeight));
        var host = new FluentPopupHost();
        var panel = new TrayPanel();
        panel.AttachHost(host);
        host.Content = panel;
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.Closed += (_, _) =>
        {
            _trayHost = null;
            _trayButton?.ClearValue(Control.BackgroundProperty);
            panel.Shutdown();          // 释放 OpenProcess/VirtualAllocEx 的句柄（幂等）
            Logger.Info("托盘图标面板已关闭");
        };
        _trayHost = host;
        _trayButton.SetResourceReference(Control.BackgroundProperty, "ThemePanelBackground");
        host.ShowAt(anchor);
        Logger.Info("托盘图标面板已打开");
    }

    /// <summary>诊断入口（<c>--control-center</c>）：直接打开控制中心面板，供截图/验收使用。</summary>
    public void ShowControlCenterForDiagnostics() => OpenControlCenter();

    private void OpenControlCenter()
    {
        if (_controlCenterButton is null) return;
        // 锚点 = 按钮下缘的设备像素（PointToScreen 返回物理 px，与 ShowAt 的入参口径一致）
        var anchor = _controlCenterButton.PointToScreen(new Point(0, _controlCenterButton.ActualHeight));
        var host = new FluentPopupHost();
        var panel = new ControlCenterPanel(_context);
        panel.AttachHost(host);          // 面板需要能在触发"打开设置/重载 Dock"时收起自己的宿主
        host.Content = panel;
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.PreviewKeyDown += ControlCenter_PreviewKeyDown;
        host.Closed += OnControlCenterClosed;
        _controlCenterHost = host;
        // 打开态持久高亮：走 DynamicResource 引用（换主题时高亮跟着变色），关闭时 ClearValue 交还给样式的 Transparent
        _controlCenterButton.SetResourceReference(Control.BackgroundProperty, "ThemePanelBackground");
        host.ShowAt(new Point(anchor.X, anchor.Y));
        Logger.Info("控制中心已打开");
    }

    /// <summary>Esc 收起面板（与 FluentMenu 的键盘语义一致；弹层是激活式窗口，按键必然落在它身上）。</summary>
    private void ControlCenter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _controlCenterHost?.Close();
    }

    private FrameworkElement? host_Content_AsControlCenter()
        => _controlCenterHost?.Content as FrameworkElement;

    private void OnControlCenterClosed(object? sender, EventArgs e)
    {
        // 先停状态采样：面板是每次新建的，不在关闭路径停就是"开一次多一个 5s 定时器"。
        (host_Content_AsControlCenter() as Controls.ControlCenterPanel)?.DisposeStatus();

        var host = sender as FluentPopupHost;
        if (host is not null)
        {
            // 与 OpenControlCenter 的订阅一一对称解绑（宿主虽是瞬态对象，解绑也让"谁挂着谁"一目了然）
            host.PreviewKeyDown -= ControlCenter_PreviewKeyDown;
            host.Closed -= OnControlCenterClosed;
        }
        _controlCenterHost = null;
        _controlCenterClosedAt = DateTime.UtcNow;
        _controlCenterButton?.ClearValue(Control.BackgroundProperty);
        (host?.Content as ControlCenterPanel)?.Shutdown();  // 释放音频端点 COM 引用（幂等）
        Logger.Info("控制中心已关闭");
    }
}
