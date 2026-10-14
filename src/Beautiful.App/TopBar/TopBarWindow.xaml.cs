using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Themes;
using Beautiful.Core.TopBar;
using Brush = System.Windows.Media.Brush;
// UseWindowsForms 隐式全局 using 与下列 WPF 类型同名，定向消歧：
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment; // 消歧：WinForms 隐式 using 带入同名枚举（含 Center/Left/Right）
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Beautiful.App.TopBar;

/// <summary>
/// macOS 风格顶部菜单栏（"finder 栏"，对标 MyDockFinder 顶栏）：主屏顶部全宽 <see cref="BarHeightDip"/> DIP
/// 条带，ABE_TOP AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS），最大化窗口自动让位顶栏+Dock 双条带。
/// <para>
/// 【背板形态】与 Dock（分层、两侧点击穿透）不同：本窗口全宽矩形、无穿透需求，故
/// <c>AllowsTransparency=false</c> 非分层 + <see cref="AcrylicWindow.ApplyBackdrop"/>(Acrylic, 无圆角)
/// 走 DWMWA_SYSTEMBACKDROP_TYPE 真系统背板（Task 4 结论的正向应用）。
/// </para>
/// <para>
/// 【Task 9：菜单与状态项】左区 = logo（Beautiful 应用菜单）+ 前台应用名（粗体）+ 文件/编辑/视图/窗口/帮助；
/// 右区 = 媒体迷你 + 时钟 + 音量 + 网络 + 电量。全部菜单/浮层经 <see cref="FluentMenu"/>/
/// <see cref="FluentPopupHost"/> 承载（共享 macOS 质感与背板规则）。依赖（跟踪器/媒体 VM/系统动作）经
/// <see cref="TopBarContext"/> 注入，顶栏不持有 Dock 生命周期。
/// 状态项配色/字体走 DynamicResource（换主题即时生效，无需重建）；时钟/网络/电量共用一条每分钟
/// <see cref="DispatcherTimer"/>（常驻表面，无逐秒分配）；前台应用名沿用 WinEventHook + 2s 对账（Task 8）。
/// </para>
/// </summary>
public partial class TopBarWindow : AcrylicWindow
{
    private const double BarHeightDip = 30; // macOS 顶栏视觉高度（DIP；AppBar 条带同值）
    private const int IconFontDip = 13;

    private readonly TopBarContext _context;

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;
    private NativeMethods.APPBARDATA _abd;
    private bool _appbarRegistered;
    private bool _closing;
    private HwndSource? _source;

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
    private DispatcherTimer? _statusTimer;   // 每分钟：时钟/网络/电量对账
    private Button? _mediaButton;
    private TextBlock? _mediaText;
    private Button? _clockButton;
    private TextBlock? _clockText;
    private TextBlock? _networkText;
    private TextBlock? _batteryText;
    private MediaViewModel? _subscribedMedia;

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
        if (_subscribedMedia is not null) _subscribedMedia.PropertyChanged -= OnMediaChanged;
        FluentMenu.CloseCurrent(); // 防弹层残留（重载/退出时）
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

    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        var stripH = (int)Math.Round(BarHeightDip * dpiScale);
        var screenW = NativeMethods.GetSystemMetrics(0);
        var screenH = NativeMethods.GetSystemMetrics(1);

        _abd.uEdge = NativeMethods.AbeTop;
        _abd.rc = new NativeMethods.RECT { Left = 0, Top = 0, Right = screenW, Bottom = stripH };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var availableTop = _abd.rc.Top;
        _abd.rc.Bottom = availableTop + stripH;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        var expect = $"(0,{availableTop})-({screenW},{availableTop + stripH})";
        var actual = $"({_abd.rc.Left},{_abd.rc.Top})-({_abd.rc.Right},{_abd.rc.Bottom})";
        Logger.Info($"顶栏 AppBar(ABE_TOP) QUERYPOS 可用顶边={availableTop}/screenH={screenH}" +
                    $" SETPOS rc={actual} 期望={expect} {(actual == expect ? "一致" : "不一致")}");
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            _abd.rc.Left, _abd.rc.Top,
            _abd.rc.Right - _abd.rc.Left, _abd.rc.Bottom - _abd.rc.Top,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);

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
            handled = true;
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            handled = true;
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
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
        var tint = Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                  theme.AcrylicTint.G, theme.AcrylicTint.B);
        var path = ApplyBackdrop(tint,
            theme.BlurEnabled ? BackdropKind.Acrylic : BackdropKind.None,
            roundedCorners: false);
        var hasBlur = path is BackdropPath.SystemBackdrop or BackdropPath.MicaLegacy
                                        or BackdropPath.CompositionAccent;
        Background = hasBlur
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(0xFF, tint.R, tint.G, tint.B));
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
        var len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "Beautiful";
        var sb = new System.Text.StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString().Trim();
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
    private void ShowMenuUnder(FrameworkElement anchor, System.Collections.Generic.List<MenuItemView> items)
    {
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

        // 时钟 + 日期（每分钟更新；点击 → 日历浮层）
        (_clockButton, _clockText) = MakeStatusItem("时钟", iconFont: false);
        _clockButton.Click += (_, _) => OpenCalendar();
        RightHost.Children.Add(_clockButton);

        // 音量（点击 → 系统混音器 sndvol）
        var (vol, _) = MakeStatusItem("\uE767", iconFont: true); // Segoe MDL2 Volume
        vol.Click += (_, _) => TopBarMenus.Launch("sndvol.exe");
        RightHost.Children.Add(vol);

        // 网络（点击 → ms-settings:network；状态由每分钟定时器对账）
        var (net, netText) = MakeStatusItem("\uE701", iconFont: true); // Segoe MDL2 Globe
        _networkText = netText;
        net.Click += (_, _) => TopBarMenus.Launch("ms-settings:network-status");
        RightHost.Children.Add(net);

        // 电量（仅存在电池时显示；每分钟刷新）
        var (bat, batText) = MakeStatusItem("", iconFont: false);
        _batteryText = batText;
        bat.Visibility = Visibility.Collapsed;
        RightHost.Children.Add(bat);

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

    // ---------- 状态刷新（每分钟共用一条定时器；无常驻逐秒分配） ----------

    private void StartStatusTracking()
    {
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _statusTimer.Tick += (_, _) => { UpdateClock(); UpdateNetwork(); UpdateBattery(); };
        _statusTimer.Start();
    }

    private void StopStatusTracking()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void UpdateClock()
    {
        if (_clockText is null) return;
        _clockText.Text = ClockFormatter.Format(DateTime.Now, ClockFormatter.FullPattern);
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

    // ---------- 时钟点击：日历浮层（经 FluentPopupHost） ----------

    private void OpenCalendar()
    {
        if (_clockButton is null) return;
        var p = _clockButton.PointToScreen(new Point(0, _clockButton.ActualHeight));
        var host = new FluentPopupHost { Content = new CalendarFlyout() };
        // 与 FluentMenu 同源存活语义：弹层持有前台激活即保活（否则光标仍停在上方时钟按钮、
        // 落在弹层矩形外 → 默认看门狗 80ms 内即自关）。失活（点别处）后回退矩形判定自然关闭。
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.ShowAt(new Point(p.X, p.Y));
    }
}
