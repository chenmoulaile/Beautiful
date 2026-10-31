using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Notifications;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextWrapping = System.Windows.TextWrapping;
using TextTrimming = System.Windows.TextTrimming;
using TextAlignment = System.Windows.TextAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Beautiful.App.TopBar;

/// <summary>
/// Big Sur 通知中心（Phase 4-T5）：右侧贴边全高面板 = 今天 widget（时钟 + 月历）+ 通知卡片列表。
/// <para>
/// 交互语义按 macOS：点顶栏时钟开/关；点面板外自动收起；Esc 关闭；点卡片跳转发通知的应用。
/// 数据来自 <see cref="NotificationService"/>（进程内 UserNotificationListener），面板只负责渲染，
/// 不自己读系统、不缓存第二份历史——历史只有 <see cref="NotificationStore"/> 一份。
/// </para>
/// <para>
/// 【为什么"再点一次收起"要看时间戳】点顶栏时钟会让顶栏成为前台，面板随即失活并自关，
/// 而这次点击的 Click 事件在自关**之后**才到达。若不做抑制，用户会看到"点了没反应"
/// （关了又立刻被同一个点击打开）。这里用 250ms 的关闭时间戳把这对事件收敛成一次"收起"。
/// </para>
/// </summary>
public partial class NotificationCenterWindow : Window
{
    /// <summary>面板宽度（DIP）：macOS 通知中心约 350–370。</summary>
    private const double PanelWidthDip = 360;

    /// <summary>关掉后多久内的再次调用视为"同一次点击的余波"（见类型注释）。</summary>
    private static readonly TimeSpan ReopenSuppress = TimeSpan.FromMilliseconds(250);

    private static NotificationCenterWindow? _current;
    private static DateTime _lastClosedUtc = DateTime.MinValue;

    private readonly NotificationService? _service;
    private readonly DispatcherTimer _clockTimer;
    private bool _positioned;

    public NotificationCenterWindow(NotificationService? service)
    {
        InitializeComponent();
        _service = service;

        CalendarHost.Child = new CalendarFlyout();

        // 内嵌月历/卡片在构造期可能触发 RequestBringIntoView，把 ScrollViewer 滚到中间去，
        // 表现为"大时钟被切掉一半"。面板是固定内容列，不需要任何自动滚动到可视区。
        PanelScroll.RequestBringIntoView += (_, e) => e.Handled = true;
        ApplyClock();

        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
            (_, _) => SafeApplyClock(), Dispatcher);
        _clockTimer.Start();

        if (_service is not null)
        {
            _service.Updated += OnServiceUpdated;
        }

        Loaded += (_, _) =>
        {
            // alpha.17：Loaded 之后的一切都不在点击的 try/catch 保护圈内，任何环境性异常
            // 都会升级成全局"发生界面异常"弹框（真机反馈：通知中心"又炸了"）。逐段兑底：
            // 定位/重画/滑入任一段失败，面板都保持基础形态可用，服务启动照常。
            try
            {
                PositionAtRightEdge();
                Rebuild();
                PanelScroll.ScrollToTop();
                PlaySlideIn();
            }
            catch (Exception ex)
            {
                Logger.Error("通知中心：初始化失败（面板保持基础形态）", ex);
            }
            _ = EnsureServiceStartedAsync();
        };
        Closed += (_, _) =>
        {
            _lastClosedUtc = DateTime.UtcNow;
            _slidePhase = SlidePhase.None;
            UnhookSlide();
            _clockTimer.Stop();
            if (_service is not null)
            {
                _service.Updated -= OnServiceUpdated;
            }

            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
        Deactivated += (_, _) => AnimateClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AnimateClose();
            }
        };
    }

    /// <summary>打开面板；已经开着则收起（顶栏时钟点击的语义）。</summary>
    /// <param name="service">通知服务（可为 null：纯 UI 预览）。</param>
    /// <param name="anchorPhysical">锚点（物理像素，通常是时钟按钮的屏幕位置）；null 用主屏工作区。</param>
    public static void ShowOrToggle(NotificationService? service, Point? anchorPhysical = null)
    {
        if (_current is { IsLoaded: true })
        {
            _current.AnimateClose();   // alpha.18：对称滑出，不再是硬切消失
            return;
        }

        if (DateTime.UtcNow - _lastClosedUtc < ReopenSuppress)
        {
            return;   // 同一次点击的余波：让它保持关闭
        }

        var window = new NotificationCenterWindow(service) { AnchorPhysical = anchorPhysical };
        _current = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// 关闭当前面板（顶栏重载/退出时用）。alpha.15：之前顶栏 OnClosed 只收控制中心不收这里，
    /// 重载顶栏会把通知中心留在屏上（孤儿面板）。
    /// </summary>
    public static void CloseCurrent()
    {
        if (_current is not { IsLoaded: true }) return;
        try { _current.Close(); }
        catch (InvalidOperationException) { /* 已在关闭中 */ }
    }

    /// <summary>诊断/截图入口用的窗口句柄（未打开返回 Zero）。</summary>
    public static IntPtr GetDiagnosticsHandle()
    {
        if (_current is not { IsLoaded: true } window)
        {
            return IntPtr.Zero;
        }

        try
        {
            return new System.Windows.Interop.WindowInteropHelper(window).Handle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private Point? AnchorPhysical { get; init; }

    /// <summary>
    /// 兜底锚点 = 主屏工作区的物理像素中心。alpha.15 修正：<see cref="AreaForPoint"/> 吃的是
    /// 物理像素（类型注释明说），之前这里给的是 SystemParameters 的 DIP——高 DPI 机器上
    /// 兜底路径会把面板定位到错误的位置/显示器。物理坐标唯一来源是 <see cref="MonitorGeometry"/>。
    /// </summary>
    private static Point PrimaryCenterPhysical()
    {
        // 主屏 = 屏幕虚拟桌面原点所在那块：直接探原点；探不到再探 (1,1)（负坐标副屏拓扑）。
        var area = MonitorGeometry.AreaForPoint(0, 0) ?? MonitorGeometry.AreaForPoint(1, 1);
        if (area is not { } a)
        {
            return new Point(0, 0);   // 不可知：原点即物理 (0,0)，面板会落在主屏左上（可接受兜底）
        }
        return new Point(
            a.Work.Left + (a.Work.Right - a.Work.Left) / 2.0,
            a.Work.Top + (a.Work.Bottom - a.Work.Top) / 2.0);
    }

    // ---------- 滑入/滑出（alpha.18 重做） ----------

    private enum SlidePhase { None, In, Out }

    private SlidePhase _slidePhase = SlidePhase.None;
    private int _finalLeftPx, _topPx, _widthPx = -1, _heightPx;
    private double _slideFromPx;
    private int _slideDurationMs = 1;
    private Action? _slideCompleted;
    private Stopwatch? _slideWatch;
    private bool _slideHooked;

    /// <summary>
    /// 从屏幕右缘外推入（Big Sur 通知中心的真实语义：窗口本体从边缘推出来）。
    /// alpha.18 重做：旧版只把内容平移 20px——窗口背景不透明，20px 位移肉眼不可见，
    /// 观感等于"突然出现/卡在那里"（真机反馈原话）。窗口级 SetWindowPos 逐帧推进：
    /// 尺寸不变不触发 WPF 重排，DWM 合成下平滑；320ms easeOutCubic（起步快、落位缓）。
    /// </summary>
    private void PlaySlideIn() => StartSlide(SlidePhase.In, 320);

    /// <summary>对称滑出后关闭（Esc / 点面板外 / 时钟二次点击）。直接 Close 的路径不受影响。</summary>
    public void AnimateClose()
    {
        if (_slidePhase == SlidePhase.Out) return;   // 已在滑出
        if (!IsLoaded || !_positioned)
        {
            try { Close(); } catch (InvalidOperationException) { /* 已在关闭中 */ }
            return;
        }

        StartSlide(SlidePhase.Out, 220, () =>
        {
            try { Close(); } catch (InvalidOperationException) { /* 已在关闭中 */ }
        });
    }

    private void StartSlide(SlidePhase phase, int durationMs, Action? completed = null)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _widthPx <= 0)
        {
            completed?.Invoke();
            return;
        }

        _slideFromPx = NativeMethods.GetWindowRect(handle, out var r) ? r.Left : _finalLeftPx + _widthPx;
        _slideDurationMs = Math.Max(1, durationMs);
        _slideCompleted = completed;
        _slidePhase = phase;
        _slideWatch = Stopwatch.StartNew();
        if (!_slideHooked)
        {
            _slideHooked = true;
            CompositionTarget.Rendering += OnSlideFrame;
        }
    }

    private void OnSlideFrame(object? sender, EventArgs e)
    {
        if (_slidePhase == SlidePhase.None || _slideWatch is null)
        {
            UnhookSlide();
            return;
        }

        var t = _slideWatch.Elapsed.TotalMilliseconds / _slideDurationMs;
        var target = _slidePhase == SlidePhase.In ? _finalLeftPx : _finalLeftPx + _widthPx;
        if (t >= 1.0)
        {
            SetSlideLeft(target);
            UnhookSlide();
            _slidePhase = SlidePhase.None;
            var done = _slideCompleted;
            _slideCompleted = null;
            done?.Invoke();
            return;
        }

        var p = 1.0 - Math.Pow(1.0 - t, 3);          // easeOutCubic
        SetSlideLeft((int)Math.Round(_slideFromPx + (target - _slideFromPx) * p));
    }

    private void SetSlideLeft(int leftPx)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(handle, new IntPtr(-1), leftPx, _topPx, 0, 0,
            NativeMethods.SwpNoactivate | NativeMethods.SwpNozorder | NativeMethods.SwpNosize);
    }

    private void UnhookSlide()
    {
        if (!_slideHooked) return;
        _slideHooked = false;
        CompositionTarget.Rendering -= OnSlideFrame;
        _slideWatch = null;
    }

    // ---------- 定位 ----------

    /// <summary>
    /// 贴到锚点所在显示器的右边缘：高度 = 工作区高（已扣掉顶栏/Dock 的 AppBar 预留），
    /// 宽度固定 360 DIP。用物理像素 SetWindowPos 落地，避免混合 DPI 下 DIP 换算误差。
    /// </summary>
    private void PositionAtRightEdge()
    {
        if (_positioned)
        {
            return;
        }

        var anchor = AnchorPhysical ?? PrimaryCenterPhysical();

        var area = MonitorGeometry.AreaForPoint((int)anchor.X, (int)anchor.Y);
        if (area is null)
        {
            Logger.Warn("通知中心：取不到显示器几何，保持系统默认位置");
            return;
        }

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var widthPx = (int)Math.Round(PanelWidthDip * scale);
        var heightPx = monitor.Work.Height;
        _finalLeftPx = monitor.Work.Right - widthPx;
        _topPx = monitor.Work.Top;
        _widthPx = widthPx;
        _heightPx = heightPx;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // alpha.18：先落在【屏幕右缘外】——Show() 到滑入开播之间窗口不能露脸，
            // 否则先在最终位置闪一帧再跳回屏外（闪烁）。由 PlaySlideIn 负责推入。
            NativeMethods.SetWindowPos(handle, new IntPtr(-1), monitor.Work.Right, _topPx, widthPx, heightPx,
                NativeMethods.SwpNoactivate | NativeMethods.SwpNozorder);
        }

        Width = widthPx / scale;
        Height = heightPx / scale;
        _positioned = true;
        Logger.Info($"通知中心：定位 屏=0x{monitor.Monitor.Left:X}..{monitor.Work.Right} " +
                    $"工作区=({monitor.Work.Left},{monitor.Work.Top})-({monitor.Work.Right},{monitor.Work.Bottom}) " +
                    $"dpi={scale:0.##} 窗口={widthPx}x{heightPx}px");
    }

    // ---------- 时钟 widget ----------

    private void ApplyClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = $"{now:M月d日} {WeekName(now.DayOfWeek)}";
    }

    private static string WeekName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日"
    };

    // ---------- 数据 ----------

    private async System.Threading.Tasks.Task EnsureServiceStartedAsync()
    {
        if (_service is null || _service.Started)
        {
            SafeRebuild();
            return;
        }

        try
        {
            await _service.StartAsync();
        }
        catch (Exception ex)
        {
            // StartAsync 内部已有兜底，但 WinRT 投影的意外形态（类型加载/线程模型）可能从 await 侧漏出；
            // 这里必须接住——面板已经打开，不能让一次服务启动失败把整个面板炸掉。
            Logger.Error("通知中心：服务启动异常（面板保持打开，显示未授权态）", ex);
        }
        SafeRebuild();
    }

    private void OnServiceUpdated(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(SafeRebuild));
            return;
        }

        SafeRebuild();
    }

    /// <summary>事件路径的重画入口：服务层轮询/推送都可能把异常带进这里（在点击保护圈之外），
    /// 单次重画失败只记日志，面板与定时器都保住。</summary>
    private void SafeRebuild()
    {
        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            Logger.Error("通知中心：重画失败（面板保持现状）", ex);
        }
    }

    private void SafeApplyClock()
    {
        try
        {
            ApplyClock();
        }
        catch (Exception ex)
        {
            Logger.Error("通知中心：时钟刷新失败", ex);
        }
    }

    /// <summary>整块重画（卡片数量有限、结构简单，重建比增量维护更不容易错）。</summary>
    private void Rebuild()
    {
        CardsHost.Children.Clear();

        var items = _service?.Store.Items ?? Array.Empty<NotificationItem>();
        var status = _service?.StatusText ?? "通知服务未启用";

        CountText.Text = items.Count == 0 ? string.Empty : $"{items.Count} 条";
        ClearButton.IsEnabled = items.Count > 0;

        var failed = 0;
        foreach (var item in items)
        {
            try
            {
                CardsHost.Children.Add(BuildCard(item));
            }
            catch (Exception ex)
            {
                // 单张卡片的投影异常（字符/资源/模板）不该把整个面板拖下；跳过并计数
                failed++;
                Logger.Warn($"通知中心：卡片构建跳过 {ex.GetType().Name} {ex.Message}");
            }
        }

        var readable = _service is { Access: NotificationAccess.Allowed };
        EmptyCard.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            EmptyTitle.Text = readable ? "没有新通知" : "无法读取通知";
            EmptyDetail.Text = readable ? "" : status;
            OpenSettingsButton.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
        }

        if (failed > 0)
        {
            CountText.Text = $"{items.Count} 条（{failed} 条异常跳过）";
        }
    }

    private Border BuildCard(NotificationItem item)
    {
        var accent = InitialColor(item.DisplayApp);
        var initial = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = accent,
            Child = new TextBlock
            {
                Text = item.Initial,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(initial);

        var appName = new TextBlock
        {
            Text = item.DisplayApp,
            FontSize = 11,
            Margin = new Thickness(7, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ResourceBrush("MacTextSecondary")
        };
        Grid.SetColumn(appName, 1);
        header.Children.Add(appName);

        var time = new TextBlock
        {
            Text = item.RelativeTime(DateTimeOffset.Now),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResourceBrush("MacTextTertiary")
        };
        Grid.SetColumn(time, 2);
        header.Children.Add(time);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = item.Headline,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Foreground = ResourceBrush("MacTextPrimary")
        });

        var detail = item.Detail;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 46,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = ResourceBrush("MacTextSecondary")
            });
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ResourceBrush("MacControlFill"),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Child = panel,
            ToolTip = string.IsNullOrWhiteSpace(item.AppId) ? null : $"打开 {item.DisplayApp}"
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ActivateSourceApp(item);
        };
        return card;
    }

    private Brush ResourceBrush(string key)
        => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>应用字标底色：按应用名稳定取色（同一应用每次同一颜色，不随列表顺序变）。</summary>
    private static Brush InitialColor(string app)
    {
        string[] palette =
        [
            "#FF7A5CFA", "#FF3FA7E8", "#FF35B36B", "#FFE0A030", "#FFE0603F", "#FF9B59B6", "#FF4A90D9"
        ];
        var hash = 0;
        foreach (var ch in app)
        {
            hash = unchecked((hash * 31) + ch);
        }

        var index = Math.Abs(hash) % palette.Length;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[index]));
        brush.Freeze();
        return brush;
    }

    // ---------- 动作 ----------

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        _ = ClearAsync();
    }

    private async System.Threading.Tasks.Task ClearAsync()
    {
        await _service!.ClearAllAsync();
        Rebuild();
    }

    private void OpenNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开通知设置失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 点卡片 → 打开发通知的应用。有 AUMID 走 <c>shell:AppsFolder\&lt;AUMID&gt;</c>（UWP/打包应用），
    /// 没有就什么都不做——**不猜**可执行文件路径，也不弹一个"找不到"的错误框。
    /// </summary>
    private static void ActivateSourceApp(NotificationItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AppId))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $@"shell:AppsFolder\{item.AppId}",
                UseShellExecute = true
            });
            Logger.Info("通知中心：已按 AUMID 打开来源应用");
        }
        catch (Exception ex)
        {
            Logger.Warn($"通知中心：打开来源应用失败 {ex.GetType().Name} {ex.Message}");
        }
    }
}
