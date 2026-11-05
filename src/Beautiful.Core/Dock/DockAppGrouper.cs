using System;
using System.Collections.Generic;

namespace Beautiful.Core.Dock;

/// <summary>
/// 合并输入里的一扇窗。顺序契约：调用方必须按「最近使用在前」给出同一应用的窗口序列，
/// <see cref="DockAppGrouper"/> 只保序、绝不重排（重排需要真 MRU 数据，跟踪器没有，编出来的"最近"是撒谎）。
/// </summary>
/// <param name="Handle">顶层窗口句柄。</param>
/// <param name="Title">窗口标题（挂最小化窗口也要显示——它同样算「运行中」）。</param>
/// <param name="IsMinimized">是否最小化（<b>不</b>影响「运行中」判定，只影响代表窗的选取）。</param>
public readonly record struct DockAppWindow(IntPtr Handle, string Title, bool IsMinimized);

/// <summary>合并输入的一个固定条目。</summary>
/// <param name="AppKey">exe 规范名（调用方按同一把尺子产出；空 = 解析不出，不参与合并）。</param>
/// <param name="PinnedPath">dock.json 里的原始写法（.lnk 或 exe），用于回查图标/启动路径。</param>
public readonly record struct DockPinnedEntry(string AppKey, string PinnedPath);

/// <summary>合并输入的一扇运行窗口（带分组键）。</summary>
public readonly record struct DockWindowEntry(string AppKey, DockAppWindow Window);

/// <summary>
/// 合并后的一个 Dock 格：<b>一格 = 一个应用</b>（真 macOS Dock 的语义）。
/// 背后挂着 0..n 扇窗（顺序 = 入参顺序，首元素即「代表窗」），固定与运行同应用时 <see cref="PinnedPath"/>
/// 与 <see cref="Windows"/> 同时非空——这一格既要点得动启动/切换，也要给得出「窗口▸」右键菜单。
/// </summary>
public sealed record DockAppCell
{
    /// <summary>分组键（exe 规范名，忽略大小写；空串 = 不可合并的窗口独占一格）。</summary>
    public required string AppKey { get; init; }

    /// <summary>固定项的 dock.json 原写法；null = 纯运行格（未固定）。</summary>
    public string? PinnedPath { get; init; }

    /// <summary>该应用的窗口列表，「最近使用在前」（入参保序）。最小化的窗也在列。</summary>
    public IReadOnlyList<DockAppWindow> Windows { get; init; } = Array.Empty<DockAppWindow>();

    /// <summary>运行指示布尔：有窗即运行中——<b>含全最小化</b>（用户裁定：最小化仍算「运行中」，点一颗亮着灭掉的点是假指示）。</summary>
    public bool IsRunning => Windows.Count > 0;

    /// <summary>代表窗（悬停预览/点击切换的目标）；无窗为 null。</summary>
    public DockAppWindow? Head => Windows.Count > 0 ? Windows[0] : null;
}

/// <summary>
/// Dock「按应用合并」的纯算术（病根：跟踪器每窗一格 → 一个应用开三窗就占三格，真 macOS 是一格 + 一颗运行点）。
/// <para>
/// 【为什么放 Core】合并规则（同键归一、固定与运行同应用只留一格、保序、键缺失不合并）是这个特性里
/// 唯一会「画歪」的地方——尤其 explorer.exe 既固定又开着时冒出两格。规则必须脱离 WPF 钉进单测。
/// </para>
/// <para>
/// 【键的口径】AppKey 由调用方产出（exe 规范名，与 <c>DockWindow.ToggleOrActivateRunningWindow</c>
/// 的进程映像基础名同一把尺子），Core 只做忽略大小写的相等比较。键为空的条目永远各自成格：
/// 解析不出 exe 的窗口（权限受限进程）与读不到的固定项都不许被误并进别的应用。
/// </para>
/// </summary>
public static class DockAppGrouper
{
    /// <summary>
    /// 合并：固定块在前（保持 dock.json 顺序），未固定的应用按「首窗出现位置」排在其后。
    /// 规则：同 AppKey 的多窗归一格（组内保序 = 最近使用在前）；固定与运行同键只留固定那一格；
    /// 空键不合并；重复句柄只认首次（防御 EnumWindows 与 ShellHook 重报，与切换器 WindowListMerger 同一纪律）。
    /// </summary>
    public static List<DockAppCell> Group(IReadOnlyList<DockPinnedEntry>? pinned, IReadOnlyList<DockWindowEntry>? windows)
    {
        var cells = new List<DockAppCell>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (pinned is not null)
        {
            foreach (var p in pinned)
            {
                var key = p.AppKey ?? string.Empty;
                var idx = cells.Count;
                cells.Add(new DockAppCell { AppKey = key, PinnedPath = p.PinnedPath });
                // 同键重复固定（dock.json 历史残留）：都保留格子（绝不静默删用户条目），但窗只归第一格
                if (key.Length > 0 && !indexByKey.ContainsKey(key)) indexByKey[key] = idx;
            }
        }

        if (windows is null) return cells;

        var seenHandles = new HashSet<IntPtr>();
        foreach (var e in windows)
        {
            if (!seenHandles.Add(e.Window.Handle)) continue;
            var key = e.AppKey ?? string.Empty;
            if (key.Length == 0)
            {
                // 解析不出 exe 的窗口：独占一格，永不与任何应用合并（误并 = 点 A 切到 B 的窗）
                cells.Add(new DockAppCell { AppKey = string.Empty, Windows = new[] { e.Window } });
                continue;
            }
            if (indexByKey.TryGetValue(key, out var idx))
            {
                var cell = cells[idx];
                var merged = new DockAppWindow[cell.Windows.Count + 1];
                for (var i = 0; i < cell.Windows.Count; i++) merged[i] = cell.Windows[i];
                merged[^1] = e.Window;
                cells[idx] = cell with { Windows = merged };
                continue;
            }
            indexByKey[key] = cells.Count;
            cells.Add(new DockAppCell { AppKey = key, Windows = new[] { e.Window } });
        }

        return cells;
    }

    /// <summary>
    /// 点击一格的选窗裁定（macOS 语义的真动作版）：
    /// <list type="bullet">
    /// <item><description>前台不在组内（应用未激活）→ 代表窗（列表首 = 最近使用在前）；</description></item>
    /// <item><description>前台是代表窗且组内还有别的窗 → 切到组内下一扇「非最小化的最近窗」，没有非最小化的就切任意一扇别的窗
    /// （macOS 此处是 Exposé，我们没有那个能力，就绝不画一个点了没反应的假 Exposé——切窗是真实存在的动作）；</description></item>
    /// <item><description>前台是组内的非代表窗 → 同样切回代表窗；</description></item>
    /// <item><description>前台是组内唯一的窗 → 返回它自己（调用方据此走「点前台图标 = 隐藏」的既有语义）。</description></item>
    /// </list>
    /// 空列表返回 null（格子背后没有窗：纯固定未运行，调用方走启动路径）。
    /// </summary>
    public static DockAppWindow? PickWindowForActivate(IReadOnlyList<DockAppWindow>? windows, IntPtr foreground)
    {
        if (windows is null || windows.Count == 0) return null;
        if (windows.Count == 1) return windows[0];

        var head = windows[0];
        var frontIsInGroup = false;
        for (var i = 0; i < windows.Count; i++)
            if (windows[i].Handle == foreground) { frontIsInGroup = true; break; }

        if (!frontIsInGroup) return head;                       // 未激活 → 切最近（代表）窗
        if (foreground != head.Handle) return head;             // 前台是别的同组窗 → 也切代表窗

        // 前台正是代表窗且多窗 → 组内找下一扇：优先非最小化，其次任意（都排除前台自己）
        foreach (var w in windows)
            if (w.Handle != foreground && !w.IsMinimized) return w;
        foreach (var w in windows)
            if (w.Handle != foreground) return w;
        return head;   // 理论不可达（count>1 必有别的窗），兜底防呆
    }
}
