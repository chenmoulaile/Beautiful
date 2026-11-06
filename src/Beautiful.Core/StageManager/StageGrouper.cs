using System;
using System.Collections.Generic;

namespace Beautiful.Core.StageManager;

/// <summary>
/// 台前调度（macOS Stage Manager 等效）的一个候选窗口，由 App 侧枚举产物折叠而成。
/// 入参序列约定为 MRU 序（首位 = 最近使用，与切换器的 Z 序快照同口径）。
/// </summary>
/// <param name="Handle">顶层窗口句柄。</param>
/// <param name="AppId">应用分组标识（exe 路径或 "cls:类名" 兜底，与切换器同源）。</param>
/// <param name="AppName">应用显示名。</param>
/// <param name="Title">窗口标题。</param>
/// <param name="IsMinimized">窗口是否最小化（最小化窗口仍入组——点击时要还原它）。</param>
public readonly record struct StageWindow(IntPtr Handle, string AppId, string AppName, string Title, bool IsMinimized);

/// <summary>舞台条上的一个"应用"卡片：同 AppId 的窗口按 MRU 序归并。</summary>
/// <param name="AppId">分组标识（取首次出现项原文，保留大小写用于显示/取图标）。</param>
/// <param name="AppName">显示名（取首次出现项）。</param>
/// <param name="Windows">该应用的窗口列表（MRU 序；具体 List 类型与 SwitcherApp.Windows 同一口径，
/// 归并途中要逐窗追加）。</param>
/// <param name="IsCurrent">是否为当前前台应用所在的组（舞台条上的高亮位）。</param>
public sealed record StageGroup(string AppId, string AppName, List<StageWindow> Windows, bool IsCurrent)
{
    /// <summary>该卡片的缩略候选窗口（DWM 实况缩略图的源），见 <see cref="StageGrouper.PickThumbnail"/>。</summary>
    public IntPtr ThumbnailHandle => StageGrouper.PickThumbnail(Windows);
}

/// <summary>
/// 台前调度分组与排序的纯函数（wp-macos-parity r5）：MRU 窗口快照 → 舞台条卡片序列。
/// 与切换器的 <c>WindowListMerger</c> 同属"按应用归并"一族，但不复用其类型——
/// 舞台条需要窗口级最小化位（切换器的 WindowEntry 不携带），且排序语义不同：
/// 切换器是"全最小化应用沉底"，台前调度是"前台组固定置顶、其余严格 MRU"。
/// 全部方法无副作用、不修改入参集合，可在任意线程调用（Core 零 Win32 依赖）。
/// </summary>
public static class StageGrouper
{
    /// <summary>舞台条可见卡片上限（macOS 侧栏也是有限个最近项；超出者按 MRU 截断）。</summary>
    public const int DefaultMaxGroups = 8;

    /// <summary>
    /// MRU 窗口快照 → 舞台条分组。规则：
    /// ① 句柄去重（防御 EnumWindows 与前台事件重报）；② 按 AppId 归并（大小写不敏感，
    /// Windows 路径语义；组名/AppName 取首次出现项）；③ 含 <paramref name="foreground"/> 的组
    /// 整体移到首位并标 <see cref="StageGroup.IsCurrent"/>，其余保持 MRU 相对顺序（稳定）。
    /// <paramref name="foreground"/> 为 Zero 或不在列表中（前台是我们的面板/桌面）时无当前组。
    /// </summary>
    /// <exception cref="ArgumentNullException">入参序列为 null。</exception>
    public static List<StageGroup> Build(IEnumerable<StageWindow> mruWindows, IntPtr foreground)
    {
        ArgumentNullException.ThrowIfNull(mruWindows);

        var groups = new List<StageGroup>();
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenHandles = new HashSet<IntPtr>();
        int currentIdx = -1;

        foreach (var w in mruWindows)
        {
            if (!seenHandles.Add(w.Handle)) continue;   // 句柄去重
            var id = w.AppId ?? string.Empty;
            if (indexById.TryGetValue(id, out var idx))
            {
                groups[idx].Windows.Add(w);
            }
            else
            {
                indexById[id] = groups.Count;
                groups.Add(new StageGroup(id, w.AppName ?? string.Empty, [w], IsCurrent: false));
            }
            if (w.Handle == foreground) currentIdx = indexById[id];
        }

        // 前台组置顶（稳定移动：其余组保持 MRU 相对顺序）。
        if (currentIdx > 0)
        {
            var current = groups[currentIdx];
            groups.RemoveAt(currentIdx);
            groups.Insert(0, current);
        }
        if (currentIdx >= 0)
        {
            var g = groups[0];
            groups[0] = g with { IsCurrent = true };
        }
        return groups;
    }

    /// <summary>
    /// 卡片缩略候选：组内第一个未最小化窗口（MRU 序优先"此刻看得见画面"的窗口——
    /// 最小化窗口的 DWM 缩略图只剩最后一帧甚至空白）；全最小化回退首窗（点击时还原它）。
    /// 空组返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr PickThumbnail(IReadOnlyList<StageWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) return IntPtr.Zero;
        for (var i = 0; i < windows.Count; i++)
            if (!windows[i].IsMinimized) return windows[i].Handle;
        return windows[0].Handle;
    }

    /// <summary>
    /// 截断到前 <paramref name="maxGroups"/> 张卡片（超出部分按 MRU 丢弃，下拍对账自然回来）。
    /// 约定入参来自 <see cref="Build"/>（当前组恒在首位），因此截断永不丢当前组；
    /// maxGroups &lt;= 0 返回空列表（舞台条无卡可显）。返回新列表，不修改入参。
    /// </summary>
    public static List<StageGroup> Limit(IReadOnlyList<StageGroup> groups, int maxGroups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var result = new List<StageGroup>();
        if (maxGroups <= 0) return result;
        for (var i = 0; i < groups.Count && result.Count < maxGroups; i++)
            result.Add(groups[i]);
        return result;
    }
}
