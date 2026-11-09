using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;

namespace Beautiful.App.Dock;

/// <summary>
/// 副屏精简条（多屏 Dock 实例化步骤 4）：由 <c>DockMonitorProfiles</c> 每屏覆盖
/// （<c>showDock=false</c> 且 <c>slim=true</c>）启用的轻量 surface——图标行 + 点击切窗，
/// 无媒体面板、无角标/进度管线、无放大场、无隐藏模式。
/// <para>
/// 【生命周期与所有权】WindowTracker 由 App 注入（进程级共享，本窗口不拥有、不 Dispose）；
/// AppBar 注册/注销、TaskbarCreated/WM_DISPLAYCHANGE 重注册与 <see cref="DockWindow"/> 同一套
/// Win32 纪律的精简版（一个屏一条 AppBar，SHAppBarMessage 天然支持多 AppBar 各自协商）。
/// 落位用构造传入的序号选择器钉住**一块屏**——精简条和完整 Dock 一样不能跟着全局选择器漂。
/// </para>
/// <para>
/// 【主题】全部 DynamicResource：应用级字典由 ThemeManager 在 Dock 构造/主题切换时维护，
/// 本窗口零订阅（不挂 ThemeChanged/IconThemeChanged，OnClosed 没有解绑面）。
/// </para>
/// </summary>
public partial class SlimDockWindow : AcrylicWindow
{
    /// <summary>可见面板高（DIP）：固定小图标形态（26 DIP 图标 + 上下内边距），AppBar 预留同高。</summary>
    private const double PanelDip = 40;

    private readonly WindowTracker _tracker;      // App 注入的共享跟踪器（不拥有）
    private readonly string _monitorSelector;     // 本实例钉住的屏（primary 或 1..16 序号串）
    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;

    /// <summary>--exit / --settings 进程间信号（与 DockWindow 同款）：本窗口也是主 UI，
    /// 按标题找窗口的信号可能落在精简条上，必须有人接（BeginInvoke 转发到 App 单例）。</summary>
    private readonly uint _exitRequestMsg = NativeMethods.ExitRequestMessage;
    private readonly uint _settingsRequestMsg = NativeMethods.SettingsRequestMessage;

    private NativeMethods.APPBARDATA _abd;
    private PhysicalRect _windowTarget;   // 最近一次协商出的目标窗口矩形（ABN_POSCHANGED 重申用）
    private bool _appbarRegistered;
    private bool _closing;                // OnClosing 起置位：作废挂起的重注册/重申

    public SlimDockWindow(WindowTracker tracker, string monitorSelector)
    {
        _tracker = tracker;
        _monitorSelector = monitorSelector;
        InitializeComponent();
        DataContext = _tracker;   // XAML 经 {Binding Windows} 读共享跟踪器的窗口集合
    }

    /// <summary>诊断截图用的窗口句柄（未创建时 Zero）。</summary>
    public IntPtr GetDiagnosticsHandle() => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        _appbarMsg = NativeMethods.RegisterWindowMessage("AppBarMessage");
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        RegisterAndPositionBar();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 共享跟踪器可能尚未启动（例如主屏也配置成了精简条、进程内没有完整 Dock 的机器）：
        // 重复调用为 no-op（_started 守卫），不会造成第二次 shell-hook 注册或第二只对账定时器。
        _tracker.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        UnregisterBar();   // ABM_REMOVE 必须在 hwnd 销毁前（与 DockWindow 同一硬约束）
        base.OnClosing(e);
    }

    // ---------- AppBar（DockWindow 同纪律的精简版） ----------

    private void RegisterAndPositionBar()
    {
        if (_closing) return;
        if (_appbarRegistered) UnregisterBar();   // 重注册路径：先 ABM_REMOVE
        _abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _appbarMsg
        };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref _abd);
        _appbarRegistered = true;
        Logger.Info($"精简条 AppBar ABM_NEW 完成 hwnd=0x{_hwnd.ToInt64():X} monitor={_monitorSelector}");
        PositionBar();
    }

    private void UnregisterBar()
    {
        if (!_appbarRegistered) return;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref _abd);
        _appbarRegistered = false;
    }

    /// <summary>
    /// ABE_BOTTOM 条带定位：预留带 = 面板高（<see cref="PanelDip"/>），无放大余量
    /// （精简条没有放大场，headroom 恒 0）。几何来源与 <see cref="DockWindow.PositionAsAppBar"/>
    /// 同一套（<see cref="MonitorGeometry.ForEdge"/> + <see cref="StripMath"/>），
    /// 先写 WPF DIP 再落物理 SetWindowPos 的顺序硬约束同样保留。
    /// </summary>
    private void PositionBar()
    {
        if (!_appbarRegistered || _closing) return;
        var target = MonitorGeometry.ForEdge(_hwnd, StripEdge.Bottom, PanelDip, 0, _monitorSelector);
        if (target is null) return;   // 拔屏瞬间：保持上次几何
        var t = target.Value;
        var panelH = t.Reserve.Height;   // 预留带高（上报 shell，决定工作区）

        _abd.uEdge = NativeMethods.AbeBottom;
        _abd.rc = MonitorGeometry.ToNative(t.Reserve);
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var available = MonitorGeometry.ToCore(_abd.rc);
        var reserve = StripMath.Reserve(available, panelH, StripEdge.Bottom);
        _abd.rc = MonitorGeometry.ToNative(reserve);
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        var placed = MonitorGeometry.ToCore(_abd.rc);
        var window = StripMath.WindowRect(placed, 0, StripEdge.Bottom);
        Logger.Info($"精简条 AppBar SETPOS rc=({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom})" +
                    $" monitor=0x{t.Monitor.ToInt64():X} dpi={t.DpiScale:0.##}");

        Left = StripMath.ToDip(window.Left, t.DpiScale);
        Top = StripMath.ToDip(window.Top, t.DpiScale);
        Width = Math.Max(1, StripMath.ToDip(window.Width, t.DpiScale));
        Height = Math.Max(1, StripMath.ToDip(window.Height, t.DpiScale));
        UpdateLayout();
        _windowTarget = window;
        ApplyPhysicalRect(window);
    }

    /// <summary>把窗口矩形重申为目标矩形（只 SetWindowPos 不重发 ABM_SETPOS，防"重申→广播→再重申"环）。</summary>
    private void AssertWindowRect()
    {
        if (_closing || !_appbarRegistered) return;
        if (_windowTarget.Width <= 0 || _windowTarget.Height <= 0) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out var now)) return;
        if (now.Left == _windowTarget.Left && now.Top == _windowTarget.Top &&
            now.Right == _windowTarget.Right && now.Bottom == _windowTarget.Bottom) return;
        ApplyPhysicalRect(_windowTarget);
    }

    private void ApplyPhysicalRect(PhysicalRect window)
        => NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, window.Left, window.Top, window.Width, window.Height,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 进程间信号与 DockWindow 同款转发：按标题送达的第一扇主 UI 窗口都必须接得住。
        if (_exitRequestMsg != 0 && msg == _exitRequestMsg)
        {
            handled = true;
            Dispatcher.BeginInvoke(App.ExitFromExternal);
            return IntPtr.Zero;
        }
        if (_settingsRequestMsg != 0 && msg == _settingsRequestMsg)
        {
            handled = true;
            Dispatcher.BeginInvoke(App.OpenSettingsFromExternal);
            return IntPtr.Zero;
        }
        if (_appbarMsg != 0 && msg == _appbarMsg)
        {
            handled = true;
            if (wParam.ToInt32() == NativeMethods.AbnPosChanged) Dispatcher.BeginInvoke(AssertWindowRect);
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            handled = true;   // explorer 重建任务栏 → ABM_REMOVE + 重注册
            Dispatcher.BeginInvoke(RegisterAndPositionBar);
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RegisterAndPositionBar);
        }
        return IntPtr.Zero;
    }

    // ---------- 点击切窗 ----------

    /// <summary>点击条目 = 激活/还原（最小化则还原置前，前台则由跟踪器按既有语义处理）。
    /// 前台提示取现值：精简条没有 DockWindow 的"悬停前台快照"链路，ActivateOrMinimize 内部
    /// 对前台提示只是 hint（null 也可），此处不复制那套采样。</summary>
    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WindowItem item) return;
        Logger.Info($"精简条点击：'{item.Title}' minimized={item.IsMinimized}");
        _tracker.ActivateOrMinimize(item, NativeMethods.GetForegroundWindow());
        e.Handled = true;
    }
}
