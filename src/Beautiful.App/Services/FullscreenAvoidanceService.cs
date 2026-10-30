using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;

namespace Beautiful.App.Services;

/// <summary>
/// Dock/顶栏的全屏避让服务（v4 Phase 3-T6）：1s 轮询前台窗口的 DWM 可见矩形，
/// 判定全在 Core <see cref="FullscreenAvoidance"/>；这里只做 Win32 取值与 Show/Hide 落地。
/// <para>
/// 误判方向：宁可"该让位时没让"（全屏边多一条 Dock），也不"没全屏就消失"——
/// Core 的资格条件已经把最小化/cloaked/本进程/shell/最大化全部排除。
/// 只读：不注入、不发输入、不改前台窗口。
/// </para>
/// </summary>
public sealed class FullscreenAvoidanceService : IDisposable
{
    private readonly Window _dock;
    private readonly Window? _topBar;
    private readonly DispatcherTimer _timer;
    private bool _dockHidden;
    private bool _topHidden;
    private bool _disposed;

    public FullscreenAvoidanceService(Window dock, Window? topBar)
    {
        _dock = dock;
        _topBar = topBar;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    private void Evaluate()
    {
        if (_disposed) return;
        try
        {
            if (!SettingsService.Current.FullscreenAvoidanceEnabled)
            {
                Restore(_dock, ref _dockHidden);
                if (_topBar is not null) Restore(_topBar, ref _topHidden);
                return;
            }

            var foreground = FullscreenProbe.Read();
            var hideDock = ShouldHide(_dock, foreground);
            var hideTop = _topBar is not null && ShouldHide(_topBar, foreground);

            Apply(_dock, hideDock, ref _dockHidden);
            if (_topBar is not null) Apply(_topBar, hideTop, ref _topHidden);
        }
        catch (Exception ex)
        {
            Logger.Warn($"全屏避让判定失败（保持现状）: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static bool ShouldHide(Window window, ForegroundWindow? foreground)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var area = MonitorGeometry.AreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        if (area is null) return false;
        return !FullscreenAvoidance.Evaluate(area.Value.Monitor, foreground).KeepTopmost;
    }

    private static void Apply(Window window, bool hide, ref bool hidden)
    {
        if (hide == hidden) return;
        if (hide)
        {
            window.Hide();
            hidden = true;
            Logger.Info("全屏避让：菜单栏/Dock 已让位（真全屏）");
        }
        else
        {
            window.Show();
            hidden = false;
            Logger.Info("全屏避让：退出全屏，菜单栏/Dock 已恢复");
        }
    }

    private static void Restore(Window window, ref bool hidden)
    {
        if (!hidden) return;
        window.Show();
        hidden = false;
        Logger.Info("全屏避让：设置关闭，已恢复常驻表面");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
