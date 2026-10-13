using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Beautiful.App.Dock;

/// <summary>
/// 底部居中悬浮 Dock：AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS，ABE_BOTTOM 全宽条带）
/// + macOS 式高斯放大动画（15ms DispatcherTimer 驱动逐帧 lerp——brief 原定的
/// CompositionTarget.Rendering 在窗口非激活/渲染管线空闲时停止回调会冻结中间态，
/// 改用等效 60fps 定时器已经 controller 认可；RenderTransform 自底部中心缩放 + Margin 补偿）。
/// 主题由 ThemeManager 驱动（Task 13）：构造期 Apply(HarmonyOs)，订阅 ThemeChanged 重设
/// 亚克力/间距；放大开关与 MaxScale 直读 ThemeManager.Current（σ 2.0 固定，控制器裁定），
/// 其余外观（底色/圆角/图标尺寸/字体/指示）经主题字典 DynamicResource 即时生效。
/// </summary>
public partial class DockWindow : AcrylicWindow
{
    private const double DockHeightDip = 76;  // 三套内置主题 DockHeight 均为 76（设置化时随 theme.DockHeight 重定位，Task 16/17）
    private const double Sigma = 2.0;         // 高斯 σ（控制器裁定：不随主题变化）
    private const double LerpFactor = 0.35;   // 每帧插值系数
    private const double MarginPerSide = 6;   // Margin 补偿：6·(scale-1)

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

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;
    private NativeMethods.APPBARDATA _abd;
    private NativeMethods.RECT _screenRect; // AppBar 定位后的窗口物理矩形（物理光标校验用）
    private bool _appbarRegistered;
    private bool _closing; // OnClosing 起置位：阻止关闭前挂起的 BeginInvoke 重注册在 ABM_REMOVE 后复活 AppBar
    private HwndSource? _source;

    private double _baseMargin = 3; // ItemSpacing/2，随主题更新（ApplyScale 每帧覆写 Margin 的基准；与 XAML 初始值一致）

    private double[]? _currentScales;
    private double[]? _targetScales;
    private DispatcherTimer? _animTimer;

    public DockWindow(TaskbarManager? taskbar = null)
    {
        _taskbar = taskbar;
        _smtc = new SmtcService();
        _mediaVm = new MediaViewModel(_smtc, Dispatcher); // 先于 InitializeComponent：XAML 模板可能经 RelativeSource 绑定 MediaVm
        InitializeComponent();
        _vm = new DockViewModel(_tracker);
        DataContext = _vm;
        _itemContextMenu = (ContextMenu)Resources["ItemContextMenu"];
        ThemeManager.ThemeChanged += OnThemeChanged;
        // 任务栏策略切换/explorer 重启重放（TaskbarManager.Apply/Restore → PolicyApplied）后，
        // AppBar 条带需重新协商：任务栏真被移出屏幕 → Dock 贴底；还原/保留 → 让位于任务栏上方。
        if (_taskbar is not null) _taskbar.PolicyApplied += OnTaskbarPolicyApplied;
        // 初始主题：设置项驱动（settings.json 的 Theme，缺省鸿蒙；设置窗口切换即持久化）
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
        Loaded += OnWindowLoaded;
    }

    /// <summary>Dock 数据源（设置窗口编辑固定应用后经此重载固定块）。</summary>
    public DockViewModel Vm => _vm;

    /// <summary>媒体 VM（XAML 媒体条目模板经 RelativeSource 绑定到本属性）。</summary>
    public MediaViewModel MediaVm => _mediaVm;

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
    /// </summary>
    private void ApplyTheme(ThemeDefinition theme)
    {
        if (theme.BlurEnabled)
            ApplyAcrylic(Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                        theme.AcrylicTint.G, theme.AcrylicTint.B));
        else
            DisableAcrylic();
        _baseMargin = theme.ItemSpacing / 2; // 下一动画帧起按新间距覆写容器 Margin
        _baseCenters = null;                 // 尺寸参数可能变化 → 命中测试基准中心强制重算
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _tracker.Start();
        // brief 原定 CompositionTarget.Rendering 驱动逐帧 lerp；实测窗口非激活/渲染管线空闲时
        // Rendering 停止回调，动画会冻结在中间态，故改用 15ms DispatcherTimer（等效 60fps）。
        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _animTimer.Tick += (_, _) => OnAnimationFrame();
        _animTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;                             // 先于 ABM_REMOVE：作废队列中尚未执行的 BeginInvoke 重注册
        _mediaPanel?.Close();                        // 面板随 Dock 一起关闭（Closed 处理器会清空引用）
        _animTimer?.Stop();                           // 停止动画驱动
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
    /// Hide 策略下 AppBar 注册/重注册会引发 shell 重布局，Win11 可能把任务栏摆回屏幕内：
    /// T+0.8s / T+3s 各重申一次隐藏并重新协商条带（任务栏真正移出后 Dock 才能贴底）。
    /// 非 Hide 策略 Reassert 为 no-op，不调度。
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

    /// <summary>ABE_BOTTOM 全宽条带：QUERYPOS → 修正高度 → SETPOS → SetWindowPos，并同步 WPF 侧 DIP 布局属性。</summary>
    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        var stripH = (int)Math.Round(DockHeightDip * dpiScale);
        var screenW = NativeMethods.GetSystemMetrics(0);  // SM_CXSCREEN
        var screenH = NativeMethods.GetSystemMetrics(1);  // SM_CYSCREEN

        _abd.uEdge = NativeMethods.AbeBottom;
        _abd.rc = new NativeMethods.RECT { Left = 0, Top = screenH - stripH, Right = screenW, Bottom = screenH };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        _abd.rc.Top = _abd.rc.Bottom - stripH;            // 修正：底边让位既有 AppBar/任务栏，高度以期望为准
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        Logger.Info($"AppBar SETPOS rc=({_abd.rc.Left},{_abd.rc.Top})-({_abd.rc.Right},{_abd.rc.Bottom})");
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

    // ---------- macOS 式放大 ----------

    private void ItemsHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ThemeManager.Current.ShowMagnification) return;
        // WPF 会在布局变化后按"最后已知位置"合成 MouseMove；光标跳跃离窗时这些陈旧事件
        // 会把放大目标重置回旧条目。以 Win32 物理光标位置为准：不在窗口内的一律忽略。
        if (IsCursorOutsideWindow()) return;
        var hover = HitTestIndex(e.GetPosition(ItemsHost));
        if (hover is int idx)
            _targetScales = Magnifier.ComputeScales(_vm.Items.Count, idx, ThemeManager.Current.MaxScale, Sigma);
    }

    private void ItemsHost_MouseLeave(object sender, MouseEventArgs e) => _targetScales = null;

    /// <summary>物理光标（GetCursorPos）是否已不在本窗口矩形内；AppBar 未就绪时视为在窗内。</summary>
    private bool IsCursorOutsideWindow()
    {
        if (!_appbarRegistered) return false;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        return pt.X < _screenRect.Left || pt.X >= _screenRect.Right
            || pt.Y < _screenRect.Top || pt.Y >= _screenRect.Bottom;
    }

    private void OnAnimationFrame()
    {
        var count = _vm.Items.Count;
        if (count == 0) return;
        // 主题关闭放大（ShowMagnification=false）→ 丢弃悬停目标，图标平滑回落到 1（而非冻结在放大态）
        if (!ThemeManager.Current.ShowMagnification) _targetScales = null;
        // 兜底：跳跃移动可能丢失 MouseLeave（WPF 不再收到该窗口的消息）；
        // 物理光标已离开窗口则强制回落，防止放大状态卡死。
        if (_targetScales is not null && IsCursorOutsideWindow()) _targetScales = null;
        if (_targetScales is not null && _targetScales.Length != count) _targetScales = null; // 条目增删 → 丢弃陈旧目标
        if (_currentScales is not null && _currentScales.Length != count) _currentScales = null;

        var target = _targetScales ?? AllOnes(count);
        _currentScales ??= AllOnes(count);

        for (var i = 0; i < count; i++)
        {
            var cur = _currentScales[i];
            var tgt = target[i];
            var next = Math.Abs(cur - tgt) < 0.001 ? tgt : cur + (tgt - cur) * LerpFactor;
            _currentScales[i] = next;
            ApplyScale(i, next);
        }
    }

    /// <summary>缩放自底部中心（RenderTransformOrigin=0.5,1）+ 水平 Margin 补偿，避免相邻图标视觉重叠。</summary>
    private void ApplyScale(int index, double scale)
    {
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        if (container.RenderTransform is not ScaleTransform st)
        {
            container.RenderTransformOrigin = new Point(0.5, 1);
            st = new ScaleTransform(1, 1);
            container.RenderTransform = st;
        }
        if (st.ScaleX != scale) st.ScaleX = st.ScaleY = scale;
        // Margin 每帧整值覆写：基准 ItemSpacing/2 必须并入公式（scale=1 时恰为基准值，与 XAML 初始值一致）
        var m = _baseMargin + MarginPerSide * (scale - 1);
        container.Margin = new Thickness(m, 0, m, 0);
    }

    private static double[] AllOnes(int count)
    {
        var arr = new double[count];
        Array.Fill(arr, 1.0);
        return arr;
    }

    // ---------- 命中测试 / 点击 ----------

    private double[]? _baseCenters; // 每个容器基准中心（ItemsHost 坐标系，未放大布局）

    /// <summary>
    /// 以"最近基准中心"求悬停下标，而非 InputHitTest。原因：Margin 补偿随动画逐帧改变布局，
    /// WPF 会在布局变化后合成新的 MouseMove，InputHitTest 的结果随之抖动，形成
    /// 放大 → 布局移位 → 悬停翻转 → 再放大的反馈振荡（实测 MOVE/LEAVE 交替）。
    /// 容器 ActualWidth 不含自身 Margin，不受放大动画影响，故基准中心恒定 → 命中结果只取决于物理光标位置。
    /// </summary>
    private int? HitTestIndex(Point posInItemsHost)
    {
        if (_baseCenters is null || _baseCenters.Length != _vm.Items.Count) RefreshBaseCenters();
        if (_baseCenters is null || _baseCenters.Length == 0) return null;

        int best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _baseCenters.Length; i++)
        {
            var d = Math.Abs(posInItemsHost.X - _baseCenters[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void RefreshBaseCenters()
    {
        var n = _vm.Items.Count;
        if (n == 0) { _baseCenters = null; return; }
        var centers = new double[n];
        double acc = 0;
        for (var i = 0; i < n; i++)
        {
            var w = ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe && fe.ActualWidth > 0
                ? fe.ActualWidth
                : 58.0;
            centers[i] = acc + w / 2;
            acc += w;
        }
        _baseCenters = centers;
    }

    private void ItemsHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var idx = HitTestIndex(e.GetPosition(ItemsHost));
        if (idx is int i && i < _vm.Items.Count) ActivateItem(_vm.Items[i]);
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
    /// 右键条目弹出固定菜单（媒体/回收站无固定语义不弹）。手动开关而非容器 ContextMenu：
    /// 实测 ItemContainerStyle 挂 ContextMenu 时 ContextMenuService 在本窗口把事件路由到
    /// ItemsControl 层（Source=ItemsControl），容器菜单永远不打开——绕开服务，复用
    /// 放大器的基准中心命中测试直接定位条目。
    /// </summary>
    private void ItemsHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_itemContextMenu is null) return;
        var idx = HitTestIndex(e.GetPosition(ItemsHost));
        if (idx is not int i || i >= _vm.Items.Count) return;
        var item = _vm.Items[i];
        if (item.Kind is DockItemKind.Media or DockItemKind.RecycleBin) return;
        _contextItem = item;
        if (_itemContextMenu.Items[0] is MenuItem pin)
            pin.Visibility = item is { Kind: DockItemKind.Window, LaunchPath.Length: > 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (_itemContextMenu.Items[1] is MenuItem unpin)
            unpin.Visibility = item.Kind == DockItemKind.Pinned ? Visibility.Visible : Visibility.Collapsed;
        _itemContextMenu.PlacementTarget = ItemsHost;
        _itemContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _itemContextMenu.IsOpen = true;
        e.Handled = true;
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

    /// <summary>面板置于胶囊上方并水平居中（钳制在屏幕内）；胶囊中心经 PointToScreen（物理 px）→ DIP 换算。</summary>
    private void PositionMediaPanel()
    {
        const double panelW = 420, panelH = 520, edgeMargin = 8, gap = 6;
        var panel = _mediaPanel;
        if (panel is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1;

        double centerDipX = Left + Width / 2; // 兜底：Dock 中心
        var mediaIndex = -1;                  // 媒体条目恒为 suffix 末位，仍按 Kind 扫描以稳健
        for (var i = _vm.Items.Count - 1; i >= 0; i--)
            if (_vm.Items[i].Kind == DockItemKind.Media) { mediaIndex = i; break; }
        if (mediaIndex >= 0 &&
            ItemsHost.ItemContainerGenerator.ContainerFromIndex(mediaIndex) is FrameworkElement capsule &&
            capsule.ActualWidth > 0)
        {
            var screenPt = capsule.PointToScreen(new Point(capsule.ActualWidth / 2, 0));
            centerDipX = screenPt.X / dpi;
        }

        var screenW = NativeMethods.GetSystemMetrics(0) / dpi; // SM_CXSCREEN
        var left = Math.Clamp(centerDipX - panelW / 2, edgeMargin, Math.Max(edgeMargin, screenW - panelW - edgeMargin));
        var top = Math.Max(edgeMargin, Top - panelH - gap);
        panel.Left = left;
        panel.Top = top;
    }
}
