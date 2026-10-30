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
            (_, _) => ApplyClock(), Dispatcher);
        _clockTimer.Start();

        if (_service is not null)
        {
            _service.Updated += OnServiceUpdated;
        }

        Loaded += (_, _) =>
        {
            PositionAtRightEdge();
            Rebuild();
            PanelScroll.ScrollToTop();
            _ = EnsureServiceStartedAsync();
        };
        Closed += (_, _) =>
        {
            _lastClosedUtc = DateTime.UtcNow;
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
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
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
            _current.Close();
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

        var anchor = AnchorPhysical ?? new Point(
            (SystemParameters.PrimaryScreenWidth / 2) * 1.0,
            (SystemParameters.PrimaryScreenHeight / 2) * 1.0);

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
        var leftPx = monitor.Work.Right - widthPx;
        var topPx = monitor.Work.Top;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, new IntPtr(-1), leftPx, topPx, widthPx, heightPx,
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
            Rebuild();
            return;
        }

        await _service.StartAsync();
        Rebuild();
    }

    private void OnServiceUpdated(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Rebuild));
            return;
        }

        Rebuild();
    }

    /// <summary>整块重画（卡片数量有限、结构简单，重建比增量维护更不容易错）。</summary>
    private void Rebuild()
    {
        CardsHost.Children.Clear();

        var items = _service?.Store.Items ?? Array.Empty<NotificationItem>();
        var status = _service?.StatusText ?? "通知服务未启用";

        CountText.Text = items.Count == 0 ? string.Empty : $"{items.Count} 条";
        ClearButton.IsEnabled = items.Count > 0;

        foreach (var item in items)
        {
            CardsHost.Children.Add(BuildCard(item));
        }

        var readable = _service is { Access: NotificationAccess.Allowed };
        EmptyCard.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            EmptyTitle.Text = readable ? "没有新通知" : "无法读取通知";
            EmptyDetail.Text = readable ? "" : status;
            OpenSettingsButton.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
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
