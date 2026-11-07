using System;
using System.Collections.Generic;
using Beautiful.App.Services;
using Beautiful.Core.Dock;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Interop;

/// <summary>
/// 智能隐藏的**全量遮挡采样**（alpha.29 残项清零）：枚举全部顶层窗口，返回合格可见
/// 且与 Dock 可见面板带相交的窗口可见矩形集合。判定本体在 Core
/// <see cref="DockHideModeDecider.OccluderOverlapsDock"/>（DockHideFacts.OccludingBounds → 纯几何，
/// 有单测）；这里只做 Win32 取值。
/// <para>
/// 【为什么不用 WindowEligibility.IsEligibleWindow】那份判据含标题查询（GetWindowTextLength /
/// GetWindowText 是 SEND 消息——同步发送到窗口所属线程）。本探针跑在 Dock 条件档 80ms 轮询上，
/// 一次第三方窗口卡顿就可能把 UI 线程拖进消息等待（WindowTracker 那条 2s 遍历有 IsHungAppWindow
/// 前置也仍要等非挂死窗口应答）。这里全程零 SEND：可见性（IsWindowVisible / IsIconic）、
/// 工具窗（GetWindowLongPtr 本地读）、资格与矩形（<see cref="FullscreenProbe.Read(IntPtr)"/> 的
/// DWM 扩展边框 + cloak + pid + shell 判据）都是查表式调用，挂死窗口拖不住我们。
/// 资格过滤与前台判据同一把尺子（ForegroundWindow.IsEligible：可见/未最小化/未 cloaked/
/// 非自家进程/非 shell 桌面），工具窗在这里显式排除（WS_EX_TOOLWINDOW）。
/// </para>
/// <para>
/// 【成本】EnumWindows 一趟 + 每窗几个查表式 API；先用 GetWindowRect 做**宽松粗筛**
/// （矩形外扩 <see cref="PrefilterSlackPx"/> 还碰不到 Dock 带的窗口直接跳过），绝大多数
/// 顶层窗口与贴底条带无交集，DWM 查询只发生在少数候选上。粗筛方向刻意偏松：宁可多送几个
/// 候选给 Core 精判（RectsOverlap 边界相触不算），不可漏掉真遮挡。
/// </para>
/// <para>
/// 【失败语义】枚举异常返回已收集的部分（可能为空）并记日志——Core 对空集合的语义是
/// "退回前台判据"，行为只会退回 alpha.28 的近似档，不会"凭空常驻"或"凭空消失"。
/// </para>
/// </summary>
public static class OccludingWindowProbe
{
    /// <summary>
    /// 粗筛外扩（物理 px）：窗口矩形比可见像素外扩的不可见缩放边 ≤ 13px（本机 2880×1920@200%
    /// 实测最大化 Chrome 报 -13,-13），16px 容差兜住 DWM 扩展边框可能落在窗口矩形之外的窗口。
    /// </summary>
    private const int PrefilterSlackPx = 16;

    /// <summary>
    /// 采一轮：与 <paramref name="band"/>（Dock 可见面板带，物理 px）相交的合格顶层窗口矩形。
    /// 带矩形非法（宽高 ≤ 0，条带尚未协商）返回空集合。
    /// </summary>
    public static List<PhysicalRect> CollectOverlappingBounds(PhysicalRect band)
    {
        var result = new List<PhysicalRect>();
        if (band.Width <= 0 || band.Height <= 0) return result;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if ((GetWindowLongPtr(hwnd, GwlpExStyle) & WsExToolwindow) != 0) return true;
                if (!PrefilterTouches(hwnd, band)) return true;
                if (FullscreenProbe.Read(hwnd) is { IsEligible: true } w
                    && DockHideModeDecider.RectsOverlap(w.Bounds, band))
                {
                    result.Add(w.Bounds);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 遮挡采样失败（本轮退回前台判据）：{ex.GetType().Name} {ex.Message}");
        }
        return result;
    }

    /// <summary>粗筛：窗口矩形（外扩 <see cref="PrefilterSlackPx"/>）与 Dock 带是否可能相交。</summary>
    private static bool PrefilterTouches(IntPtr hwnd, in PhysicalRect band)
        => GetWindowRect(hwnd, out var r)
           && r.Right - PrefilterSlackPx > band.Left && r.Left + PrefilterSlackPx < band.Right
           && r.Bottom - PrefilterSlackPx > band.Top && r.Top + PrefilterSlackPx < band.Bottom;
}
