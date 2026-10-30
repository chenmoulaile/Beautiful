using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;
using Beautiful.Core.Settings;

namespace Beautiful.App.Services;

/// <summary>
/// 触发角（v4 Phase 5-T2）：100ms GetCursorPos 轮询 + Core 驻留状态机；默认四角 disabled。
/// 只读光标、不装鼠标钩子；全屏应用（Core FullscreenAvoidance 判真全屏）时整体抑制。
/// </summary>
public sealed class HotCornerService : IDisposable
{
    private readonly WindowTracker _tracker;
    private readonly DispatcherTimer _timer;
    private readonly HotCornerDwellState _state = new();
    private bool _disposed;

    public HotCornerService(WindowTracker tracker)
    {
        _tracker = tracker;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            if (!NativeMethods.GetCursorPos(out var point)) { _state.Reset(); return; }
            var area = MonitorGeometry.AreaForPoint(point.X, point.Y);
            if (area is null) { _state.Reset(); return; }

            if (SettingsService.Current.FullscreenAvoidanceEnabled)
            {
                var foreground = FullscreenProbe.Read();
                if (foreground is { IsEligible: true } window &&
                    FullscreenAvoidance.CoversMonitor(window.Bounds, area.Value.Monitor))
                {
                    _state.Reset();
                    return;
                }
            }

            var hit = HotCornerPlan.Hit(area.Value.Monitor, point.X, point.Y);
            var fired = _state.Update(hit, Environment.TickCount64);
            if (fired is null) return;

            var action = ActionFor(fired.Value);
            if (string.Equals(action, "disabled", StringComparison.OrdinalIgnoreCase)) return;
            Execute(fired.Value, action);
        }
        catch (Exception ex)
        {
            _state.Reset();
            Logger.Warn($"触发角轮询失败（本拍跳过）: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static string ActionFor(HotCorner corner) => corner switch
    {
        HotCorner.TopLeft => SettingsService.Current.HotCornerTopLeft,
        HotCorner.TopRight => SettingsService.Current.HotCornerTopRight,
        HotCorner.BottomLeft => SettingsService.Current.HotCornerBottomLeft,
        _ => SettingsService.Current.HotCornerBottomRight
    };

    private void Execute(HotCorner corner, string action)
    {
        Logger.Info($"触发角命中：{corner} → {action}");
        switch (action)
        {
            case "showdesktop":
                try
                {
                    foreach (var w in _tracker.Windows)
                        NativeMethods.ShowWindow(w.Handle, NativeMethods.SwMinimize);
                }
                catch (Exception ex) { Logger.Warn($"触发角显示桌面失败: {ex.Message}"); }
                break;
            case "lock":
                try { LockWorkStation(); }
                catch (Exception ex) { Logger.Warn($"触发角锁定失败: {ex.Message}"); }
                break;
            case "sleep":
                try
                {
                    if (!SetSuspendState(false, false, false))
                        Logger.Warn("触发角睡眠请求被系统拒绝");
                }
                catch (Exception ex) { Logger.Warn($"触发角睡眠失败: {ex.Message}"); }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
