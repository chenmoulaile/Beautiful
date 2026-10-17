using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;
using Brush = System.Windows.Media.Brush;      // UseWindowsForms 隐式 using 与 System.Drawing.Brush 冲突
using Color = System.Windows.Media.Color;      // 同上
using HorizontalAlignment = System.Windows.HorizontalAlignment;   // 与 WinForms 的同名枚举冲突
using Panel = System.Windows.Controls.Panel;   // 与 WinForms 的 Panel 冲突

namespace Beautiful.App.Services;

/// <summary>
/// "任务栏玻璃壳"：盖在 <b>原生任务栏</b>上的一层点击穿透装饰，把它从"贴底的直角色带"
/// 读成"浮在屏底的一条玻璃"。这是 #6 的落地方案 —— <b>只美化，不接管</b>。
/// <para>
/// 【为什么不移动/不隐藏/不拦截任何东西】原生任务栏的按钮、跳转列表、Win+T、通知角标全部保持
/// 原样可用；我们只做它自己做不到的三件事：面向屏幕内侧的圆角收口、一条 1px 高光亮边、
/// 一条主题强调色的发丝线。窗口带 <c>WS_EX_TRANSPARENT</c>，鼠标事件直接穿到下面的任务栏上，
/// 所以这不是"另一个任务栏"，也就不会有"两个任务栏抢位"的那类故障。
/// </para>
/// <para>
/// 【为什么用 1s 轮询而不是事件】任务栏矩形会变的原因很多（分辨率、缩放、图标大小、任务栏
/// 被拖到别的边、explorer 重启）。这些没有一个能靠单条 WinEvent 覆盖全，而一次
/// <c>GetWindowRect</c> 的成本可以忽略。轮询只做"变了才动"，不变时零副作用。
/// </para>
/// <para>
/// 【全屏避让（v3.8）】置顶是一次"霸占"：不加约束地浮着，就会盖在全屏游戏与 F11 视频之上——
/// 那是一条用户关不掉、又不属于任何应用的横线。每轮多读一次前台窗口（<see cref="FullscreenProbe"/>，
/// 纯查表式 API），由 <see cref="FullscreenAvoidance"/> 裁定"有窗口盖住整块屏就落到它下面"。
/// 最大化窗口不触发（它只占工作区，够不到任务栏那一条），所以日常用机不受影响。
/// </para>
/// </summary>
public sealed class TaskbarGlassOverlay : IDisposable
{
    /// <summary>轮询周期：够快跟上分辨率变化，又不会在任务管理器里看见我们。</summary>
    private const int PollMs = 1000;

    private readonly Window _window = new();
    private readonly Border _veil = new();
    private readonly Border _highlight = new();
    private readonly Border _hairline = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(PollMs) };

    private bool _lastVisible;
    private PhysicalRect _lastBar = new(0, 0, 0, 0);
    private OverlayZOrder? _lastZOrder;
    private bool _disposed;

    public TaskbarGlassOverlay()
    {
        _window.WindowStyle = WindowStyle.None;
        _window.AllowsTransparency = true;
        _window.Background = null;
        _window.ShowInTaskbar = false;
        _window.ShowActivated = false;
        _window.Topmost = true;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.Focusable = false;
        _window.IsHitTestVisible = false;

        // 三层叠在同一个 Border 里：面纱在最下，亮边与发丝线贴在面向屏幕内侧的那条边
        var root = new Grid();
        root.Children.Add(_veil);
        root.Children.Add(_highlight);
        root.Children.Add(_hairline);
        _window.Content = root;

        _veil.Opacity = 0.9;
        _highlight.VerticalAlignment = VerticalAlignment.Top;
        _highlight.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        _hairline.VerticalAlignment = VerticalAlignment.Top;
        _hairline.Background = AccentBrush();

        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            var ex = GetWindowLongPtr(hwnd, GwlpExStyle);
            // WS_EX_TRANSPARENT 是"只美化不接管"的技术前提：没有它这层就会吃掉任务栏的点击
            SetWindowLongPtr(hwnd, GwlpExStyle, new IntPtr(ex | WsExTransparent | WsExToolwindow));
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
        Logger.Info("任务栏玻璃壳：已挂载（点击穿透，不接管原生任务栏）");
    }

    /// <summary>重新读一次任务栏矩形并决定画/不画/重摆。</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var bar = TaskbarRectPx();
        var area = bar is null ? null : MonitorGeometry.AreaForPoint(
            (bar.Value.Left + bar.Value.Right) / 2, (bar.Value.Top + bar.Value.Bottom) / 2);
        if (bar is null || area is null)
        {
            Hide();
            return;
        }

        var monitor = area.Value.Monitor;
        if (!TaskbarGlassMath.IsDecoratable(bar.Value, monitor))
        {
            // 原生任务栏被隐藏（策略 hide）或位置读不到时，装饰必须一起消失——
            // 留一条悬空的高光比没有装饰更难看
            Hide();
            return;
        }

        var edge = TaskbarGlassMath.DetectEdge(bar.Value, monitor);
        if (edge == GlassEdge.Unknown)
        {
            Hide();
            return;
        }

        var width = bar.Value.Right - bar.Value.Left;
        var height = bar.Value.Bottom - bar.Value.Top;
        var shortSide = Math.Min(width, height);
        var scale = area.Value.DpiScale <= 0 ? 1.0 : area.Value.DpiScale;
        var corner = TaskbarGlassMath.CornerRadius(width, height);
        var highlight = TaskbarGlassMath.EdgeThickness(scale, shortSide, TaskbarGlassMath.PreferredHighlightPx);
        var hairline = TaskbarGlassMath.EdgeThickness(scale, shortSide, TaskbarGlassMath.PreferredHairlinePx);

        var sameGeometry = _lastVisible && _lastBar.Left == bar.Value.Left && _lastBar.Top == bar.Value.Top
                           && _lastBar.Right == bar.Value.Right && _lastBar.Bottom == bar.Value.Bottom
                           && _edge == edge;

        _veil.CornerRadius = Corners(edge, corner / scale);
        _highlight.Height = edge is GlassEdge.Left or GlassEdge.Right ? double.NaN : highlight / scale;
        _highlight.Width = edge is GlassEdge.Left or GlassEdge.Right ? highlight / scale : double.NaN;
        _hairline.Height = edge is GlassEdge.Left or GlassEdge.Right ? double.NaN : hairline / scale;
        _hairline.Width = edge is GlassEdge.Left or GlassEdge.Right ? hairline / scale : double.NaN;
        ApplyAlignment(edge);

        if (!sameGeometry)
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, bar.Value.Left, bar.Value.Top, width, height,
                    SwpNozorder | SwpNoactivate);
            _lastBar = bar.Value;
            _edge = edge;
        }

        if (!_lastVisible)
        {
            _window.Show();
            _lastVisible = true;
        }

        ApplyZOrderAgainstFullscreen(monitor);
    }

    /// <summary>
    /// 每轮问一次"这块屏被谁盖住了"，据此决定玻璃壳浮着还是落到那个窗口之下。
    /// <para>
    /// 【为什么置顶要每轮重施、让位只做一次】见 <see cref="FullscreenAvoidance.NeedsReassert"/>：
    /// 置顶是会被别人抢走的状态，不重施就是用户口中的"美化层不见了"；而让位状态下反复插位会跟
    /// 全屏窗口抢 Z 序。所以这里"每轮调用"不等于"每轮 SetWindowPos"。
    /// </para>
    /// <para>
    /// 【先改 WPF 的 <c>Topmost</c> 再自己 <c>SetWindowPos</c>】只调 Win32 而不改属性，WPF 仍记着
    /// "我置顶"，下一次它自己维护 Z 序时（ShowActivated/InvalidateVisual 路径）会把我们顶回去。
    /// </para>
    /// </summary>
    private void ApplyZOrderAgainstFullscreen(PhysicalRect monitor)
    {
        var decision = FullscreenAvoidance.Evaluate(monitor, FullscreenProbe.Read());
        if (!FullscreenAvoidance.NeedsReassert(_lastZOrder, decision)) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        _window.Topmost = decision.KeepTopmost;
        var insertAfter = decision.KeepTopmost ? HwndTopmost
                        : decision.PlaceAfter != IntPtr.Zero ? decision.PlaceAfter
                        : HwndNotopmost;

        // 只有裁定翻转才写日志：浮着状态下每秒都要重施置顶（那是 NeedsReassert 的裁定），
        // 跟着刷就会把日志冲走——而"让位/恢复"这两行正是真机取证要看的唯一证据。
        var flipped = _lastZOrder is null || _lastZOrder.Value.KeepTopmost != decision.KeepTopmost;
        if (SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate | SwpAsyncwindowpos))
        {
            _lastZOrder = decision;
            if (flipped)
                Logger.Info(decision.KeepTopmost
                    ? $"玻璃壳：无全屏遮挡，施加置顶（任务栏 {monitor.Width}x{monitor.Height}）"
                    : $"玻璃壳：让位于全屏窗口 0x{decision.PlaceAfter.ToInt64():X}");
        }
    }

    private GlassEdge _edge;

    /// <summary>亮边/发丝线贴在面向屏幕内侧的那条边：贴底时在上沿，贴顶时在下沿，以此类推。</summary>
    private void ApplyAlignment(GlassEdge edge)
    {
        var (vHighlight, hHighlight) = edge switch
        {
            GlassEdge.Bottom => (VerticalAlignment.Top, HorizontalAlignment.Stretch),
            GlassEdge.Top => (VerticalAlignment.Bottom, HorizontalAlignment.Stretch),
            GlassEdge.Left => (VerticalAlignment.Stretch, HorizontalAlignment.Right),
            _ => (VerticalAlignment.Stretch, HorizontalAlignment.Left),   // Right
        };
        _highlight.VerticalAlignment = vHighlight;
        _highlight.HorizontalAlignment = hHighlight;
        _hairline.VerticalAlignment = vHighlight;
        _hairline.HorizontalAlignment = hHighlight;
        // 发丝线压在亮边下面一点：两者同宽同位时只有发丝线可见，亮边退化成它的柔光
        Panel.SetZIndex(_hairline, 2);
        Panel.SetZIndex(_highlight, 1);
    }

    private static CornerRadius Corners(GlassEdge edge, double r)
    {
        var (tl, tr, bl, br) = TaskbarGlassMath.RoundedCorners(edge);
        return new CornerRadius(
            tl ? r : 0, tr ? r : 0, br ? r : 0, bl ? r : 0);
    }

    /// <summary>主题强调色（与顶栏/Dock 同一来源）；取不到就用一个中性蓝，不硬编成"某个主题的色"。</summary>
    private static Brush AccentBrush()
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("ThemeAccent") is Brush brush) return brush;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // 资源还没就绪（构造早于主题应用）：回落到下面的固定色
        }
        return new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0xC2, 0xFF));
    }

    /// <summary>原生任务栏的实际矩形（物理像素）。读不到返回 null。</summary>
    private static PhysicalRect? TaskbarRectPx()
    {
        var hwnd = FindWindowClass("Shell_TrayWnd");
        if (hwnd == IntPtr.Zero) return null;
        if (!GetWindowRect(hwnd, out var r)) return null;
        return new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    private void Hide()
    {
        if (!_lastVisible) return;
        _lastVisible = false;
        // 让位状态作废：重新显示时第一件事必须是把置顶施加回去，否则会出现
        // "上一次让给某个全屏窗口、这一次那窗口早没了、装饰层却一直沉在桌面下"。
        _lastZOrder = null;
        try { _window.Hide(); }
        catch (InvalidOperationException) { /* 窗口还没建好：本来就没显示 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= null;
        try { _window.Close(); }
        catch (InvalidOperationException) { /* 尚未 Show 过，无窗口可关 */ }
    }
}
