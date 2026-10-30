using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Controls;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Beautiful.App.Controls;

/// <summary>
/// 音量 HUD 窗口（Phase 4-T7）：屏幕上方居中的小圆角面板，喇叭图标 + 16 格音量条 + 读数。
/// <para>
/// 生命周期完全由 <see cref="VolumeOsdController"/> 控制：每次音量变化重建窗口内容并把淡出计时清零，
/// 计时结束（保持 900ms + 淡出 250ms）自动关闭。窗口本身不接鼠标、不抢焦点，
/// 也不会出现在任务栏——它是一块"看得见但摸不着"的提示层。
/// </para>
/// <para>
/// 位置按"锚点所在显示器的工作区"算：多屏时 HUD 跟着音量键使用者所在的屏，而不是永远主屏。
/// </para>
/// </summary>
public partial class VolumeOsdWindow : Window
{
    private readonly List<Rectangle> _segments = [];
    private readonly DispatcherTimer _fadeTimer;
    private DateTime _shownAt;
    private bool _closed;

    public VolumeOsdWindow()
    {
        InitializeComponent();
        BuildSegments();

        _fadeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
            (_, _) => Tick(), Dispatcher);
    }

    /// <summary>诊断入口用的窗口句柄。</summary>
    public static IntPtr HandleOf(VolumeOsdWindow? window)
    {
        if (window is null)
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

    /// <summary>按当前音量/静音状态重画，并（重新）开始自动淡出。</summary>
    public void ShowState(int percent, bool muted, TimeSpan? hold = null)
    {
        var clamped = VolumeOsd.Clamp(percent);
        var lit = VolumeOsd.LitSegments(clamped, muted);

        GlyphText.Text = VolumeOsd.GlyphFor(clamped, muted) switch
        {
            VolumeGlyph.Mute => "\uE74F",     // 喇叭带斜杠
            VolumeGlyph.Low => "\uE993",      // 无弧
            VolumeGlyph.Medium => "\uE994",   // 一道弧
            _ => "\uE767"                     // 三道弧
        };

        // macOS 的 HUD 不显示数字：读数只留给日志与状态栏，面板上保持"图标 + 格子"的原样。
        LevelText.Text = string.Empty;
        LevelText.Visibility = Visibility.Collapsed;

        for (var i = 0; i < _segments.Count; i++)
        {
            _segments[i].Fill = i < lit ? Brushes.White : SegmentIdleBrush();
        }

        Opacity = 1.0;
        _shownAt = DateTime.UtcNow;
        HoldFor = hold ?? VolumeOsd.HoldDuration;

        _closed = false;   // 上一次已淡出关闭：这次重新显示必须清掉标志，否则 Tick 直接 return
        if (!IsVisible)
        {
            try
            {
                Show();
            }
            catch (InvalidOperationException)
            {
                // 窗口已被销毁（Close 之后不能再 Show）：由控制器重建实例
                throw;
            }
        }

        PositionAtTopCenter();
        _fadeTimer.Stop();
        _fadeTimer.Start();
    }

    private TimeSpan HoldFor { get; set; } = VolumeOsd.HoldDuration;

    private void BuildSegments()
    {
        for (var i = 0; i < VolumeOsd.Segments; i++)
        {
            var segment = new Rectangle
            {
                // 6×16 的竖条 + 3 间距：macOS 的音量格是"细高条"，横条会看着像电池电量。
                Width = 6,
                Height = 16,
                RadiusX = 2,
                RadiusY = 2,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                Fill = SegmentIdleBrush()
            };
            _segments.Add(segment);
            SegmentHost.Children.Add(segment);
        }
    }

    private static Brush SegmentIdleBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private void Tick()
    {
        if (_closed)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _shownAt;
        var opacity = VolumeOsd.OpacityAfter(elapsed, HoldFor);
        Opacity = opacity;
        if (opacity <= 0)
        {
            _fadeTimer.Stop();
            _closed = true;
            Close();
        }
    }

    /// <summary>贴在锚点所在显示器工作区的上沿居中（macOS 的音量 HUD 也在上方）。</summary>
    private void PositionAtTopCenter()
    {
        // 用光标所在显示器：多屏时 HUD 跟着"手正在哪块屏上按音量键"，而不是永远主屏。
        var area = NativeMethods.GetCursorPos(out var cursor)
            ? MonitorGeometry.AreaForPoint(cursor.X, cursor.Y)
            : MonitorGeometry.AreaForPoint(1, 1);
        if (area is null)
        {
            return;
        }

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var widthPx = (int)Math.Round(ActualWidth * scale);
        var heightPx = (int)Math.Round(ActualHeight * scale);
        if (widthPx <= 0 || heightPx <= 0)
        {
            return;   // 还没完成布局：等第一次 Show 之后的重排
        }

        var leftPx = monitor.Work.Left + ((monitor.Work.Width - widthPx) / 2);
        // 顶部 1/8 处（macOS 的 OSD 靠近上沿但不是贴着菜单栏）；小屏也至少留 40 DIP，避免被顶栏压住。
        var topPx = monitor.Work.Top + (int)Math.Round(Math.Max(40, monitor.Work.Height / 8.0) * scale);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(handle, new IntPtr(-1), leftPx, topPx, widthPx, heightPx,
            NativeMethods.SwpNoactivate | NativeMethods.SwpNozorder);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 点击穿透 + 不抢焦点：HUD 盖在别人窗口上时，鼠标划过/点击必须落到下面的窗口。
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const int gwlExStyle = -20;
        const int wsExTransparent = 0x00000020;
        const int wsExNoActivate = 0x08000000;
        const int wsExToolWindow = 0x00000080;

        var style = NativeMethods.GetWindowLongPtr(handle, gwlExStyle);
        style |= wsExTransparent | wsExNoActivate | wsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, gwlExStyle, style);
    }
}
