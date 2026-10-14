using System;
using System.Collections.Generic;

namespace Beautiful.Core.Switcher;

/// <summary>
/// 切换器的一个候选窗口。<paramref name="Priority"/> 为调用方给出的 Z 序/MRU 序号（仅透传供 UI
/// 显示与调试，<see cref="WindowListMerger.GroupByApp"/> 不据此重排——入参序列本身已有序）。
/// </summary>
/// <param name="Handle">顶层窗口句柄。</param>
/// <param name="AppId">应用分组标识（exe 路径，大小写不敏感；取不到路径时由调用方回退类名）。</param>
/// <param name="AppName">应用显示名。</param>
/// <param name="Title">窗口标题。</param>
/// <param name="Priority">Z 序序号（越小越靠前，透传）。</param>
public readonly record struct WindowEntry(IntPtr Handle, string AppId, string AppName, string Title, int Priority);

/// <summary>切换器面板上的一个"应用"条目：同 AppId 的多个窗口按 Z 序归并在 <paramref name="Windows"/> 内。</summary>
/// <param name="AppId">分组标识（取首次出现项的 AppId 原文，保留大小写用于显示/取图标）。</param>
/// <param name="AppName">显示名（取首次出现项）。</param>
/// <param name="Windows">该应用的窗口列表，按 Z 序（= 入参出现顺序）。</param>
public sealed record SwitcherApp(string AppId, string AppName, List<WindowEntry> Windows);

/// <summary>
/// 窗口列表 → 切换器应用分组的纯函数（Task 10，macOS Cmd+Tab 语义：按应用分组、同应用多窗口归并）。
/// 全部方法无副作用、不修改入参集合，可在任意线程调用。
/// </summary>
public static class WindowListMerger
{
    /// <summary>
    /// 按 AppId 归并已按 Z 序/MRU 排列的窗口：应用顺序 = 该应用首个窗口的位置（首现即 MRU 序），
    /// 组内窗口顺序 = 入参顺序（Z 序）。同句柄重复出现只保留首次（防御 EnumWindows 与前台事件重报）。
    /// AppId 比对大小写不敏感（Windows 路径语义），但 <see cref="SwitcherApp.AppId"/> 保留首次出现的原文。
    /// </summary>
    /// <exception cref="ArgumentNullException">入参序列为 null。</exception>
    public static List<SwitcherApp> GroupByApp(IEnumerable<WindowEntry> zOrderMru)
    {
        ArgumentNullException.ThrowIfNull(zOrderMru);

        var result = new List<SwitcherApp>();
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenHandles = new HashSet<IntPtr>();

        foreach (var entry in zOrderMru)
        {
            if (!seenHandles.Add(entry.Handle)) continue; // 句柄去重
            var id = entry.AppId ?? string.Empty;
            if (indexById.TryGetValue(id, out var idx))
            {
                result[idx].Windows.Add(entry);
                continue;
            }
            indexById[id] = result.Count;
            result.Add(new SwitcherApp(id, entry.AppName ?? string.Empty, new List<WindowEntry> { entry }));
        }
        return result;
    }

    /// <summary>
    /// 最小化惩罚：把"所有窗口都被最小化"的应用整体沉到列表末尾（其余保持 MRU 相对顺序，稳定排序）。
    /// 返回**新列表**，不修改入参。判定最小化需要 Win32 调用，故由调用方以委托注入（Core 保持零依赖）。
    /// </summary>
    /// <param name="apps">已分组的应用列表。</param>
    /// <param name="isMinimized">窗口级最小化判定（通常 <c>IsIconic</c>）。</param>
    public static List<SwitcherApp> ApplyMinimizedPenalty(
        IReadOnlyList<SwitcherApp> apps, Func<IntPtr, bool> isMinimized)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(isMinimized);

        var keep = new List<SwitcherApp>();
        var demoted = new List<SwitcherApp>();
        foreach (var app in apps)
        {
            // "全部窗口最小化"才降级；空组（理论不应出现）按最小化处理，避免它抢走首位。
            var allMinimized = true;
            foreach (var w in app.Windows)
                if (!isMinimized(w.Handle)) { allMinimized = false; break; }

            (allMinimized ? demoted : keep).Add(app);
        }
        keep.AddRange(demoted);
        return keep;
    }

    /// <summary>
    /// 循环步进：<paramref name="current"/> 为当前高亮下标，backward=false 前进、true 后退，结果恒落在
    /// <c>[0, count)</c>。约定 <c>current &lt; 0</c> 表示"尚未选中任何项"（面板刚打开的哨兵值）：
    /// 前进取首项 0、后退取末项 count-1；<c>current &gt;= count</c>（列表在按住期间变短）按取模收敛。
    /// <paramref name="count"/> &lt;= 0 返回 -1（无可选项）。
    /// </summary>
    public static int NextIndex(int current, int count, bool backward)
    {
        if (count <= 0) return -1;
        if (current < 0) return backward ? count - 1 : 0;

        var next = ((current + (backward ? -1 : 1)) % count + count) % count; // 双取模处理负余数
        return next;
    }
}
