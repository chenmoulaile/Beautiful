using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Themes;
using Brushes = System.Windows.Media.Brushes;   // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突
using Color = System.Windows.Media.Color;       // 同上：ThemeDefinition 的 tint 是 System.Drawing.Color，此处转 Media 口径

namespace Beautiful.App.TopBar;

/// <summary>
/// macOS 风格顶部菜单栏（"finder 栏"，对标 MyDockFinder 顶栏）：主屏顶部全宽 <see cref="BarHeightDip"/> DIP
/// 条带，ABE_TOP AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS），最大化窗口自动让位顶栏+Dock 双条带。
/// <para>
/// 【背板形态】与 Dock（分层、两侧点击穿透）不同：本窗口全宽矩形、无穿透需求，故
/// <c>AllowsTransparency=false</c> 非分层 + <see cref="AcrylicWindow.ApplyBackdrop"/>(Acrylic, 无圆角)
/// 走 DWMWA_SYSTEMBACKDROP_TYPE 真系统背板——应用内首个常驻真模糊表面（Task 4 结论的正向应用）。
/// 非分层窗口无逐像素 alpha：三档全败/主题禁模糊时代码把窗口 Background 铺成不透明 tint（同 FluentPopupHost 约定）。
/// </para>
/// <para>
/// 【与底部 Dock AppBar 共存】shell 工作区 = 屏幕矩形逐条带裁剪所有已注册 AppBar；ABE_TOP 与 ABE_BOTTOM
/// 分占上下两边互不破坏（strip-probe 实测两条均生效）。顶栏条带与任务栏策略无关，故不订阅
/// TaskbarManager.PolicyApplied，也不需要 Dock 那套 ScheduleHideSettle（底部条带协商由 Dock 侧负责）。
/// </para>
/// <para>
/// 【前台应用名】主路径 SetWinEventHook(EVENT_SYSTEM_FOREGROUND, OUTOFCONTEXT)：事件驱动、零轮询，
/// 回调经安装线程（UI 线程）消息泵派发；同句柄去重（进程名不随标题变，命中即跳过全部系统调用）。
/// 备路径 2s DispatcherTimer 对账（hook 漏报/初始状态兜底，与 WindowTracker 同口径）。
/// 前台属于本进程（顶栏自身/Dock/弹层被激活）时显示 "Beautiful"。
/// </para>
/// <para>
/// 菜单与状态项属 Task 9：LeftHost（logo 按钮、应用名、各菜单）/ RightHost（状态项）为约定插入点。
/// </para>
/// </summary>
public partial class TopBarWindow : AcrylicWindow
{
    private const double BarHeightDip = 30; // macOS 顶栏视觉高度（DIP；AppBar 条带同值）

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;
    private NativeMethods.APPBARDATA _abd;
    private bool _appbarRegistered;
    private bool _closing; // OnClosing 起置位：阻止挂起的 BeginInvoke 重注册在 ABM_REMOVE 后复活 AppBar
    private HwndSource? _source;

    // 前台应用名跟踪（见类注释：事件驱动 + 2s 对账）
    private NativeMethods.WinEventDelegate? _winEventProc; // 字段持有防委托 GC（回调野指针即崩溃）
    private IntPtr _winEventHook;
    private DispatcherTimer? _fallbackTimer;
    private IntPtr _lastFgHwnd;
    private string _lastAppName = "";
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public TopBarWindow()
    {
        InitializeComponent();
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
        ApplyTheme(ThemeManager.Current); // 初始主题：App 启动序已 Apply(SettingsService 主题)，此处取 Current
        StartForegroundTracking();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;          // 先于 ABM_REMOVE：作废队列中的重注册
        StopForegroundTracking();
        UnregisterAppBar();       // ABM_REMOVE 必须在 hwnd 销毁前
        _source?.RemoveHook(WndProc);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged; // 解除订阅，防闭包引用已关闭窗口
        base.OnClosed(e);
    }

    private void RegisterAndPositionAppBar()
    {
        if (_closing) return;                         // 关闭竞态：TaskbarCreated/WM_DISPLAYCHANGE 挂起的重注册不再执行
        if (_appbarRegistered) UnregisterAppBar();    // 重注册路径：先 ABM_REMOVE
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
    /// ABE_TOP 全宽条带：QUERYPOS → 修正高度（顶边锚定 shell 允许的上沿）→ SETPOS → SetWindowPos，
    /// 并同步 WPF 侧 DIP 布局属性。与 Dock 的 ABE_BOTTOM 流程镜像：底部 Dock 用 availableBottom 锚底，
    /// 顶部本栏用 availableTop 锚顶（正常返回 0；仅左右竖向 AppBar 占住顶角时让位下移）。
    /// </summary>
    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        var stripH = (int)Math.Round(BarHeightDip * dpiScale);
        var screenW = NativeMethods.GetSystemMetrics(0);  // SM_CXSCREEN（主屏全宽，Task 8 仅主屏）
        var screenH = NativeMethods.GetSystemMetrics(1);  // SM_CYSCREEN

        _abd.uEdge = NativeMethods.AbeTop;
        _abd.rc = new NativeMethods.RECT { Left = 0, Top = 0, Right = screenW, Bottom = stripH };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var availableTop = _abd.rc.Top;                    // shell 允许我们占到的顶边（0 = 正常；既有条带让位）
        _abd.rc.Bottom = availableTop + stripH;            // 修正：高度以期望为准，顶边锚定可用上沿
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        var expect = $"(0,{availableTop})-({screenW},{availableTop + stripH})";
        var actual = $"({_abd.rc.Left},{_abd.rc.Top})-({_abd.rc.Right},{_abd.rc.Bottom})";
        Logger.Info($"顶栏 AppBar(ABE_TOP) QUERYPOS 可用顶边={availableTop}/screenH={screenH}" +
                    $" SETPOS rc={actual} 期望={expect} {(actual == expect ? "一致" : "不一致")}");
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            _abd.rc.Left, _abd.rc.Top,
            _abd.rc.Right - _abd.rc.Left, _abd.rc.Bottom - _abd.rc.Top,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);

        // 同步 WPF 侧属性，避免 Show() 按内容重新计算窗口尺寸
        Left = _abd.rc.Left / dpiScale;
        Top = _abd.rc.Top / dpiScale;
        Width = Math.Max(1, (_abd.rc.Right - _abd.rc.Left) / dpiScale);
        Height = Math.Max(1, (_abd.rc.Bottom - _abd.rc.Top) / dpiScale);
    }

    private void UnregisterAppBar()
    {
        if (!_appbarRegistered) return;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref _abd);
        _appbarRegistered = false;
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
            handled = true; // explorer 重建：仅重注册顶栏条带（任务栏策略重放由 DockWindow 的同一消息负责）
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
            return IntPtr.Zero;
        }
        // WM_DISPLAYCHANGE：分辨率/拓扑变化 → 重协商条带。
        // 与 Dock 同一禁令：WM_SETTINGCHANGE 不得在此重注册（ABM_SETPOS 自身广播 → 无限循环）。
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
        }
        return IntPtr.Zero;
    }

    // ---------- 主题 ----------

    private void OnThemeChanged(ThemeDefinition theme) => ApplyTheme(theme);

    /// <summary>
    /// 主题落地：模糊启用 → ApplyBackdrop(Acrylic) 真系统背板；禁用（Linux）→ ApplyBackdrop(None)
    /// 显式清背板。非分层窗口按胜出路径切窗口底色：有模糊 → Transparent 让系统模糊透出（半透明
    /// BarBackground 叠在玻璃层上，tint 经 ThemeDockBackground 内容层生效，同 Task 4 结论），
    /// 全败/无模糊 → 不透明 tint 兜底（防黑洞）。全宽贴屏幕顶边矩形，不开 DWM 圆角（roundedCorners=false）。
    /// </summary>
    private void ApplyTheme(ThemeDefinition theme)
    {
        if (_closing) return;
        var tint = Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                  theme.AcrylicTint.G, theme.AcrylicTint.B);
        var path = ApplyBackdrop(tint,
            theme.BlurEnabled ? BackdropKind.Acrylic : BackdropKind.None,
            roundedCorners: false);
        var hasBlur = path is BackdropPath.SystemBackdrop or BackdropPath.MicaLegacy
                                        or BackdropPath.CompositionAccent;
        Background = hasBlur
            ? Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(Color.FromArgb(0xFF, tint.R, tint.G, tint.B));
    }

    // ---------- 前台应用名 ----------

    private void StartForegroundTracking()
    {
        _winEventProc = OnWinEvent; // 必须字段持有：无托管引用时委托可被 GC，非托管回调触发野指针
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground,
            IntPtr.Zero, _winEventProc, 0, 0, NativeMethods.WineventOutOfContext);
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
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnwinEventHook(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;
    }

    /// <summary>
    /// EVENT_SYSTEM_FOREGROUND 回调（OUTOFCONTEXT：在安装钩子的线程消息泵上派发，即 UI 线程）。
    /// 事件可能落后于真实前台（排队期间又切了窗），以 GetForegroundWindow 现值为准。
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (NativeMethods.GetForegroundWindow() != hwnd) return;
        if (Dispatcher.CheckAccess()) RefreshForegroundApp();
        else Dispatcher.BeginInvoke(RefreshForegroundApp); // 防御：非派发线程回调时转投 UI 线程
    }

    /// <summary>
    /// 刷新前台应用名（public：Task 9 菜单弹出/自绘链路可直接调用）。同句柄去重——应用名取自进程，
    /// 不随窗口标题变化，命中缓存即零系统调用（空闲时仅定时器每 2s 一次 GetForegroundWindow）。
    /// </summary>
    public void RefreshForegroundApp()
    {
        if (_closing) return;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _lastFgHwnd) return;
        _lastFgHwnd = fg;

        string name;
        if (fg == IntPtr.Zero) name = "Beautiful";
        else
        {
            NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            if (pid == _ownPid)
            {
                name = "Beautiful"; // 本进程窗口（顶栏/Dock/弹层）在前台
            }
            else
            {
                var path = pid != 0 ? NativeMethods.GetProcessPath(pid) : null;
                name = path is { Length: > 0 }
                    ? Path.GetFileNameWithoutExtension(path)
                    : FallbackWindowTitle(fg); // 进程路径不可得（权限/商店沙箱）→ 退窗口标题
            }
        }
        if (name == _lastAppName) return;
        _lastAppName = name;
        ForegroundAppText.Text = name;
    }

    private static string FallbackWindowTitle(IntPtr hwnd)
    {
        var len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "Beautiful";
        var sb = new System.Text.StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString().Trim();
        return title.Length > 0 ? title : "Beautiful";
    }
}
