using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using Beautiful.Core.Settings;

namespace Beautiful.App.ScreenCorners;

/// <summary>
/// 屏幕圆角遮罩服务（对标 MyDockFinder ScreenRound）：按设置在每块显示器的四角维护遮罩窗。
/// 1s 轮询对账（显示器集合 / 半径 / 显隐 + 置顶重申），设置变更走 <see cref="ApplySettings"/>
/// 立即对账；被真全屏前台窗口盖住的屏，四角让位——判据复用 Core <see cref="FullscreenAvoidance"/>
/// （与 Dock/顶栏避让同一套，最大化不触发），不然黑色圆角会一直压在全屏游戏/视频的画面上。
/// <para>
/// 【哲学】附属表面：轮询与对账任何一步失败只记日志、保持现状，绝不连坐主 UI
/// （与触发角 / 全屏避让同一处置）。全量重取显示器（<see cref="MonitorGeometry.Surfaces"/>），
/// 不缓存拓扑——拔插屏 / 改缩放后下一次对账自然对齐。
/// </para>
/// </summary>
public sealed class ScreenCornerService : IDisposable
{
    /// <summary>四角遍历次序固定（Enum.GetValues 的顺序依赖枚举声明，这里显式列出让对账可预测）。</summary>
    private static readonly ScreenCorner[] Corners =
        [ScreenCorner.TopLeft, ScreenCorner.TopRight, ScreenCorner.BottomLeft, ScreenCorner.BottomRight];

    private readonly DispatcherTimer _timer;
    private readonly List<ScreenCornerOverlay> _overlays = new();
    private bool _disposed;

    public ScreenCornerService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
    }

    /// <summary>设置页开关 / 半径变更的即时生效入口：不等下一拍，立即对账。</summary>
    public void ApplySettings() => Sync();

    private void Sync()
    {
        if (_disposed) return;
        try
        {
            var settings = SettingsService.Current;
            if (!settings.ScreenCornersEnabled)
            {
                if (_overlays.Count > 0)
                {
                    DestroyAll();
                    Logger.Info("屏幕圆角：设置已关闭，遮罩全部撤除");
                }
                return;
            }

            var monitors = MonitorGeometry.Surfaces();
            // 每屏排除（alpha.29 残项清零）：被排除的屏从对账里整体退出——① 撤除与 ② 建摆都用
            // 过滤后的列表，排除状态变化后已有遮罩随下一次对账自然撤掉（无需专门撤除入口）。
            // 句柄身份取不到（拔屏瞬间）按不排除处理：宁少排除一块屏，不静默多排除。
            var excludedKeys = settings.ScreenCornersMonitors;
            var active = new List<MonitorGeometry.MonitorSurface>(monitors.Count);
            foreach (var surface in monitors)
            {
                var identity = MonitorGeometry.Identify(surface.Handle);
                if (identity is { } id && ScreenCornerMonitors.IsExcluded(excludedKeys, id.Index, id.IsPrimary))
                    continue;
                active.Add(surface);
            }
            var foreground = FullscreenProbe.Read();
            var maskColor = settings.ScreenCornerMaskColor;   // 遮罩色（Canonical 后原文），色变即下一拍对账生效

            // ① 撤除：所属显示器已不在屏（矩形比对即可——句柄会因拓扑变化失效，矩形是稳定标识），
            //    或已被排除名单点名（后者留一行日志，"为什么圆角没了"可 grep）。
            for (var i = _overlays.Count - 1; i >= 0; i--)
            {
                var gone = true;
                foreach (var m in active)
                    if (m.Rect == _overlays[i].MonitorRect) { gone = false; break; }
                if (gone)
                {
                    var excludedNow = false;
                    foreach (var m in monitors)
                        if (m.Rect == _overlays[i].MonitorRect) { excludedNow = true; break; }
                    _overlays[i].Close();
                    _overlays.RemoveAt(i);
                    if (excludedNow) Logger.Info("屏幕圆角：该显示器已被排除名单点名，撤除其四角遮罩");
                }
            }

            // ② 建 / 摆 / 显隐 / 置顶重申
            foreach (var monitor in active)
            {
                var radiusPx = ScreenCornerPlan.RadiusPx(settings.ScreenCornerRadius, monitor.DpiScale);
                // 该屏被真全屏盖住 → 四角让位；该浮着 → 每轮重申（NeedsReassert 的哲学：
                // 让位是稳定态做够一次就停手，浮着是易失态必须反复施加，不然 z 序被抢后遮罩悄悄沉底）
                var covered = !FullscreenAvoidance.Evaluate(monitor.Rect, foreground).KeepTopmost;
                foreach (var corner in Corners)
                {
                    var square = ScreenCornerPlan.CornerSquare(monitor.Rect, corner, radiusPx);
                    if (square.Width <= 0 || square.Height <= 0) continue;   // 退化屏：跳过
                    var overlay = Ensure(monitor.Rect, corner, radiusPx, monitor.DpiScale, maskColor);
                    overlay.Place(monitor.Rect, radiusPx, monitor.DpiScale, maskColor);
                    if (covered)
                    {
                        if (overlay.IsVisible) overlay.Hide();
                    }
                    else
                    {
                        if (!overlay.IsVisible) overlay.Show();
                        overlay.Reassert();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"屏幕圆角对账失败（保持现状）: {ex.GetType().Name} {ex.Message}");
        }
    }

    private ScreenCornerOverlay Ensure(PhysicalRect monitorRect, ScreenCorner corner, int radiusPx, double dpiScale, string maskColor)
    {
        foreach (var overlay in _overlays)
            if (overlay.Corner == corner && overlay.MonitorRect == monitorRect)
                return overlay;
        var created = new ScreenCornerOverlay(corner, monitorRect, radiusPx, dpiScale, maskColor);
        _overlays.Add(created);
        created.Show();   // SourceInitialized 里落穿透样式与物理矩形，这里只负责显示
        Logger.Info($"屏幕圆角：遮罩已建 {corner} " +
                    $"({monitorRect.Left},{monitorRect.Top})-({monitorRect.Right},{monitorRect.Bottom}) r={radiusPx}px");
        return created;
    }

    private void DestroyAll()
    {
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        DestroyAll();
    }
}
