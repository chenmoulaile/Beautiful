using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Notifications;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Beautiful.App.TopBar;

/// <summary>
/// 单条通知横幅（Phase 4-T5 续）：右上角滑入的圆角卡片，3.8 秒后自动淡出，悬停暂停计时。
/// <para>
/// 点击卡片 = 打开来源应用（与通知中心同一套 AUMID 逻辑）；不点就自己消失，不进历史——
/// 历史已经由 <see cref="Beautiful.Core.Notifications.NotificationStore"/> 完整保留，
/// 横幅只是"此刻提醒一下"，不应该成为第二个数据源。
/// </para>
/// </summary>
public partial class NotificationBannerWindow : Window
{
    /// <summary>默认停留时长（macOS banner 约 4 秒）。</summary>
    private static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(3800);

    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);

    private readonly DispatcherTimer _timer;
    private readonly NotificationItem _item;
    private TimeSpan _hold;
    private DateTime _shownAt;
    private bool _closing;

    private NotificationBannerWindow(NotificationItem item)
    {
        InitializeComponent();
        _item = item;
        Apply(item);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render,
            (_, _) => Tick(), Dispatcher);

        MouseEnter += (_, _) => _timer.Stop();     // 悬停暂停：用户正在读，不该消失
        MouseLeave += (_, _) =>
        {
            _shownAt = DateTime.UtcNow;
            _hold = TimeSpan.FromMilliseconds(1200);
            _timer.Start();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ActivateSource(item);
            Close();
        };

        Loaded += (_, _) => _shownAt = DateTime.UtcNow;
        Closed += (_, _) => NotificationBannerHost.OnClosed(this);
    }

    internal static NotificationBannerWindow Create(NotificationItem item)
        => new(item);

    /// <summary>显示并开始计时（<paramref name="hold"/> 只给诊断入口用于延长停留）。</summary>
    internal void Begin(TimeSpan? hold = null)
    {
        _hold = hold ?? DefaultHold;
        _shownAt = DateTime.UtcNow;
        Show();
        _timer.Start();
    }

    internal double BannerHeight => ActualHeight > 1 ? ActualHeight : 96;

    private void Tick()
    {
        if (_closing)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _shownAt;
        if (elapsed < _hold)
        {
            return;
        }

        var over = elapsed - _hold;
        if (over >= FadeOut)
        {
            _closing = true;
            _timer.Stop();
            Close();
            return;
        }

        Opacity = 1.0 - (over.TotalMilliseconds / FadeOut.TotalMilliseconds);
    }

    private void Apply(NotificationItem item)
    {
        AppText.Text = item.DisplayApp;
        TitleText.Text = item.Headline;
        BodyText.Text = item.Detail;
        AppInitial.Text = item.Initial;
        AppBadge.Background = InitialColor(item.DisplayApp);
        ToolTip = string.IsNullOrWhiteSpace(item.AppId) ? null : $"打开 {item.DisplayApp}";

        if (string.IsNullOrWhiteSpace(item.Detail))
        {
            BodyText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>与通知中心一致：按应用名稳定取色。</summary>
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

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(hash) % palette.Length]));
        brush.Freeze();
        return brush;
    }

    private static void ActivateSource(NotificationItem item)
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
            Logger.Info("通知横幅：已按 AUMID 打开来源应用");
        }
        catch (Exception ex)
        {
            Logger.Warn($"通知横幅：打开来源应用失败 {ex.GetType().Name} {ex.Message}");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不抢焦点、不进 Alt+Tab/任务栏：横幅是"提示层"，不该打断用户当前操作。
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const int gwlExStyle = -20;
        const int wsExNoActivate = 0x08000000;
        const int wsExToolWindow = 0x00000080;
        var style = NativeMethods.GetWindowLongPtr(handle, gwlExStyle);
        style |= wsExNoActivate | wsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, gwlExStyle, style);
    }
}

/// <summary>
/// 横幅栈管理器：最多同时 3 条，新的在上，旧的被挤掉；所有定位按"右上角 + 堆叠高度"重算。
/// <para>
/// 这里不做持久化、不做队列（被挤掉的直接关掉）：macOS 的横幅同样是"当前这一刻"的提示，
/// 真正的历史在通知中心里，重复呈现反而会造成"同一条通知看了两遍"的错觉。
/// </para>
/// </summary>
internal static class NotificationBannerHost
{
    /// <summary>同时存在的横幅上限。</summary>
    private const int MaxVisible = 3;

    /// <summary>右上角留白（DIP）：与通知中心面板同一视觉节奏。</summary>
    private const double EdgeMarginDip = 14;

    /// <summary>横幅之间的间距（DIP）。</summary>
    private const double GapDip = 8;

    private static readonly List<NotificationBannerWindow> Open = [];

    /// <summary>新通知到达：挤掉最旧的，显示新的，然后重排。</summary>
    public static void Show(NotificationItem item, TimeSpan? hold = null)
    {
        while (Open.Count >= MaxVisible)
        {
            var oldest = Open[0];
            Open.RemoveAt(0);
            try
            {
                oldest.Close();
            }
            catch (InvalidOperationException)
            {
                // 已关：忽略
            }
        }

        var window = NotificationBannerWindow.Create(item);
        Open.Add(window);
        window.Begin(hold);
        InvalidateLayout();
    }

    internal static void OnClosed(NotificationBannerWindow window)
    {
        Open.Remove(window);
        InvalidateLayout();
    }

    /// <summary>诊断/截图入口：当前最上面那条的句柄。</summary>
    public static IntPtr DiagnosticsHandle
    {
        get
        {
            if (Open.Count == 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                return new System.Windows.Interop.WindowInteropHelper(Open[^1]).Handle;
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero;
            }
        }
    }

    public static void CloseAll()
    {
        foreach (var window in Open.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // 已关：忽略
            }
        }

        Open.Clear();
    }

    /// <summary>按"最新在上"重排：从工作区右上角往下堆。</summary>
    private static void InvalidateLayout()
    {
        if (Open.Count == 0)
        {
            return;
        }

        var area = NativeMethods.GetCursorPos(out var cursor)
            ? MonitorGeometry.AreaForPoint(cursor.X, cursor.Y)
            : MonitorGeometry.AreaForPoint(1, 1);
        if (area is null)
        {
            return;
        }

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var topDip = 34.0;   // 让开顶栏（24）+ 一点呼吸

        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var window = Open[i];
            // alpha.17：重排由"别的横幅关闭/新横幅到达"触发，任何一条的布局异常都不该把
            // 其余横幅与调用方（通知服务的事件链）一起拖下——逐条隔离。
            try
            {
                window.UpdateLayout();   // SizeToContent 的高度要等布局跑完才准
                var widthPx = (int)Math.Round(window.ActualWidth * scale);
                var heightPx = (int)Math.Round(window.BannerHeight * scale);
                if (widthPx <= 0 || heightPx <= 0)
                {
                    continue;
                }

                var leftPx = monitor.Work.Right - widthPx - (int)Math.Round(EdgeMarginDip * scale);
                var stackIndex = Open.Count - 1 - i;   // 最新的一条在最上面
                var topPx = monitor.Work.Top + (int)Math.Round(topDip * scale)
                            + (int)Math.Round(stackIndex * ((window.BannerHeight + GapDip) * scale));

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                // SWP_NOSIZE(0x1)：尺寸交给 WPF 的 SizeToContent，这里只摆位置
                NativeMethods.SetWindowPos(handle, new IntPtr(-1), leftPx, topPx, 0, 0,
                    NativeMethods.SwpNoactivate | NativeMethods.SwpNozorder | 0x0001);
            }
            catch (Exception ex)
            {
                Logger.Warn($"通知横幅：单条重排失败（{ex.GetType().Name} {ex.Message}），跳过该条");
            }
        }
    }
}
