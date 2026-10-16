using System;
using System.Collections.Generic;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 右键菜单的一个动作标识。Core 只决定"有哪些条目、什么顺序、能不能用、为什么不能用"，
/// 真正的 Win32/进程操作在 App 侧的 <c>DockContextMenuExecutor</c> 里按此枚举分派——
/// 危险代码只有一个家，菜单纯逻辑又能脱离 WPF 单测（本次缺陷的根因就是菜单内容写死在 code-behind 里）。
/// </summary>
public enum DockContextAction
{
    /// <summary>打开（ShellExecute 默认动词，.lnk 保留其参数与工作目录）。</summary>
    Open,

    /// <summary>打开新窗口（再启动一次；浏览器类附 <c>-new-window</c>）。</summary>
    OpenNewWindow,

    /// <summary>以管理员身份运行（runas 动词）。</summary>
    RunAsAdmin,

    /// <summary>打开文件位置（explorer /select）。</summary>
    OpenFileLocation,

    /// <summary>属性…（properties 动词）。</summary>
    Properties,

    /// <summary>固定到 Dock（写 dock.json）。</summary>
    Pin,

    /// <summary>取消固定（写 dock.json）。</summary>
    Unpin,

    /// <summary>窗口列表父项（只展开、不执行）。</summary>
    WindowList,

    /// <summary>切到该窗口（复用切换器的前台切换路径）。</summary>
    SwitchToWindow,

    /// <summary>显示所有窗口（把该应用被最小化的窗口全部还原，一个都不藏）。</summary>
    ShowAllWindows,

    /// <summary>关闭窗口（该应用全部窗口的正常关闭 = WM_CLOSE）。</summary>
    CloseAllWindows,

    /// <summary>退出（macOS 的 Quit：同样是正常关闭其所有窗口）。</summary>
    Quit,

    /// <summary>最小化本窗口。</summary>
    Minimize,

    /// <summary>最大化 / 还原本窗口（标签随当前状态取反）。</summary>
    MaximizeOrRestore,

    /// <summary>关闭本窗口（WM_CLOSE）。</summary>
    CloseWindow,

    /// <summary>打开回收站。</summary>
    OpenRecycleBin,

    /// <summary>清空回收站（shell API，需二次确认）。</summary>
    EmptyRecycleBin,

    /// <summary>结束任务（强制，需二次确认 + 进程黑名单）。</summary>
    ForceKill
}

/// <summary>
/// 菜单需要的一个窗口快照：App 从 <c>WindowTracker</c>/<c>WindowEligibility</c> 取好后喂给 Core，
/// Core 因此不需要（也不允许）碰任何 Win32 调用。
/// </summary>
/// <param name="Handle">顶层窗口句柄。</param>
/// <param name="Title">标题（已经过挂死守卫，见 <c>WindowEligibility.GetWindowTitle</c>）。</param>
/// <param name="ProcessId">所属进程 id（0 = 取不到，如权限受限进程）。</param>
/// <param name="ProcessName">进程名（空 = 取不到，"结束任务"据此判黑名单）。</param>
/// <param name="IsMinimized">是否最小化（IsIconic）。</param>
/// <param name="IsMaximized">是否最大化（IsZoomed，决定"最大化/还原"的标签）。</param>
public readonly record struct DockWindowSnap(
    IntPtr Handle, string Title, uint ProcessId, string ProcessName,
    bool IsMinimized, bool IsMaximized);

/// <summary>构建一次 Dock 右键菜单的全部输入（一次弹出的快照，构建后不再变化）。</summary>
public sealed record DockContextMenuInput
{
    /// <summary>条目种类：分隔符/媒体不弹菜单（返回空表）。</summary>
    public required DockItemKind Kind { get; init; }

    /// <summary>启动目标原路径（dock.json 里的写法，可能是 .lnk）。</summary>
    public string LaunchPath { get; init; } = string.Empty;

    /// <summary><see cref="LaunchPath"/> 这个文件当前是否存在（"打开文件位置""属性…"的可用性依据）。</summary>
    public bool LaunchPathExists { get; init; }

    /// <summary>解析后的目标是否为可执行文件（.exe）：不是就没有可靠的"以管理员身份运行"。</summary>
    public bool HasExecutableTarget { get; init; }

    /// <summary>该条目的目标是否已经在固定列表里（运行窗口条目据此在"固定/取消固定"间二选一）。</summary>
    public bool IsPinned { get; init; }

    /// <summary>该条目对应的所有窗口：Pinned = 同可执行文件的全部窗口；Window = 同进程的全部窗口。</summary>
    public IReadOnlyList<DockWindowSnap> Windows { get; init; } = Array.Empty<DockWindowSnap>();

    /// <summary>Kind=Window 时本条目自己那一格对应的窗口句柄（Pinned/回收站传 0）。</summary>
    public IntPtr ThisWindowHandle { get; init; }

    /// <summary>
    /// <b>弹出菜单那一刻</b>的真实前台窗口句柄：弹层显示后前台就是菜单自己，必须由 App 在 Show 之前取好。
    /// 0 = 未知（此时不判"已在前台"，避免把可用条目错误置灰）。
    /// </summary>
    public IntPtr ForegroundHandle { get; init; }

    /// <summary>回收站当前是否有内容（SHQueryRecycleBin 的真实读数，决定"清空回收站"是否需要置灰）。</summary>
    public bool RecycleBinHasItems { get; init; }

    /// <summary>
    /// 本机是否真的能打开这个目标的"属性"页（App 侧用 <c>properties</c> 动词实测后的结论）。
    /// 【为什么把这个交给 App】Win11 24H2 真机实测：该动词对 <b>快捷方式</b>正常弹出属性页，
    /// 对<b>裸 .exe</b> 一律返回 1155（ERROR_NO_ASSOCIATION，系统侧不再注册），而 shell 的按序号属性页 API
    /// 在我们这个常驻进程内调用会抛（同样真机实测）。既然做不到，就不给这一格——
    /// 比"灰着说清原因"更诚实的是"不给一个点了没反应的入口"。
    /// </summary>
    public bool PropertiesPageAvailable { get; init; }
}

/// <summary>
/// 菜单树的一个条目（Core 产出的纯数据；App 侧一对一映射成 <c>MenuItemView</c>）。
/// <para>
/// <see cref="DisabledReason"/> 非空即代表"这一格真的做不到"，App 渲染时把它拼进标签（"以管理员身份运行（目标不是可执行文件）"）——
/// 绝不留没有理由的灰项，也绝不留点了没反应的死项。
/// </para>
/// </summary>
public sealed class DockContextMenuEntry
{
    public required string Id { get; init; }
    public required DockContextAction Action { get; init; }
    public required string Label { get; init; }

    /// <summary>分隔线：只有 <see cref="IsSeparator"/> 为真时其余字段无意义。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>假 = 置灰不可交互，且 <see cref="DisabledReason"/> 必须给出可见原因。</summary>
    public bool IsEnabled { get; init; } = true;

    public string? DisabledReason { get; init; }

    /// <summary>勾选态（窗口子菜单里"当前前台那一格"），渲染为 ✓ 列。</summary>
    public bool IsChecked { get; init; }

    /// <summary>破坏性动作：App 必须把它渲染成"再点一次确认"的子菜单，绝不单次点击即生效。</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>确认格子的文案（点它才真正执行）。</summary>
    public string? ConfirmLabel { get; init; }

    /// <summary>子菜单（窗口列表、确认链）。</summary>
    public IReadOnlyList<DockContextMenuEntry>? Children { get; init; }

    // ---------- 执行参数（App 侧执行器只读这些，不再回头猜） ----------
    public IntPtr WindowHandle { get; init; }
    public uint ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? TargetPath { get; init; }

    /// <summary>
    /// 分隔线。注意：分隔符同样要有一个 <see cref="DockContextAction"/> 值（记录里它是"非动作行"），
    /// 这里填的是无害占位 <see cref="DockContextAction.Open"/>——**按 Action 检索条目的代码必须先排除
    /// <see cref="IsSeparator"/>**（否则一条分隔线会被当成一个"打开"项命中），App 侧的渲染与执行都遵守这一条。
    /// </summary>
    internal static DockContextMenuEntry Sep(string id) => new() { Id = id, Action = DockContextAction.Open, Label = string.Empty, IsSeparator = true };
}

/// <summary>
/// Dock 右键菜单的**决策内核**（纯函数，无 Win32、无 WPF，可单测）。
/// <para>
/// 用户的原话是"dock 栏中右键只有固定到 dock 栏的选项……这不还原"。macOS Dock 与 Windows 跳转列表
/// 给的是同一族语义（打开 / 窗口列表 / 关闭 / 退出 / 强制退出 / 固定 / 属性），差别只在**每种条目能给哪几样**。
/// 把这件事写成一张表而不是散在 code-behind 的 if 里，才能把"没有窗口就没有窗口子菜单"
/// "系统关键进程不给结束任务"这类规则钉死并测住。
/// </para>
/// </summary>
public static class DockContextMenuPlan
{
    /// <summary>
    /// 禁止"结束任务"的进程（小写、去 .exe 后比较）。杀了它们不是关一个窗口，而是把用户的桌面/会话掀掉，
    /// 而 Dock 是常驻进程、点一次菜单不该有这种威力。
    /// </summary>
    private static readonly HashSet<string> KillProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "system", "memory compression"
    };

    /// <summary>进程名是否属于"绝不可结束"名单（空名返回 false：由调用方按"取不到进程"另判）。</summary>
    public static bool IsKillProtected(string? processName)
    {
        var name = NormalizeProcessName(processName);
        return name.Length > 0 && KillProtected.Contains(name);
    }

    /// <summary>进程名归一：去空格、去尾随 .exe（<c>Process.ProcessName</c> 通常不含扩展名，路径取出的名字含）。</summary>
    private static string NormalizeProcessName(string? processName)
    {
        var name = (processName ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>
    /// 生成一次右键的菜单树。分隔符与媒体胶囊返回**空表**（调用方据此不弹菜单，保持现状）。
    /// </summary>
    public static IReadOnlyList<DockContextMenuEntry> Build(DockContextMenuInput input) => input.Kind switch
    {
        DockItemKind.Pinned => PinnedMenu(input),
        DockItemKind.Window => WindowMenu(input),
        DockItemKind.RecycleBin => RecycleBinMenu(input),
        _ => Array.Empty<DockContextMenuEntry>()
    };

    /// <summary>dock.json 里那条路径的文件已经不在盘上了：启动类与 shell 类动作此刻都做不到。</summary>
    private const string MissingTarget = "启动目标文件不存在";

    /// <summary>窗口所属进程的可执行路径读不到（权限受限进程 / 商店应用宿主）：固定类动作没有可写的路径。</summary>
    private const string NoExePath = "无法确定该窗口的程序路径";

    /// <summary>固定应用条目：未运行 = 启动 + 固定 + shell 三项；运行中再多一段"窗口/关闭/退出"与末尾的强制结束。</summary>
    private static IReadOnlyList<DockContextMenuEntry> PinnedMenu(DockContextMenuInput input)
    {
        var running = input.Windows.Count > 0;
        // 启动两条同样要"目标文件还在"：文件被删/移走后 Process.Start 只会抛一次我们兜住的异常，
        // 屏上什么也不发生——那正是一格"点了没反应的死项"，与"打开文件位置"同一把尺子处理（置灰 + 说清原因）。
        var missing = !input.LaunchPathExists;
        var items = new List<DockContextMenuEntry>
        {
            new()
            {
                Id = "open", Action = DockContextAction.Open, Label = "打开", TargetPath = input.LaunchPath,
                IsEnabled = !missing, DisabledReason = missing ? MissingTarget : null
            },
            new()
            {
                Id = "newwin", Action = DockContextAction.OpenNewWindow, Label = "打开新窗口", TargetPath = input.LaunchPath,
                IsEnabled = !missing, DisabledReason = missing ? MissingTarget : null
            },
            DockContextMenuEntry.Sep("sep0"),
        };

        // 只有真有窗口才有这一段：空窗口列表挂一个"窗口（0）"子菜单是假入口，规则由测试钉住
        if (running)
        {
            items.Add(new DockContextMenuEntry
            {
                Id = "windows",
                Action = DockContextAction.WindowList,
                Label = "窗口",
                Children = WindowListChildren(input.Windows, input.ForegroundHandle)
            });
            items.Add(new DockContextMenuEntry { Id = "showall", Action = DockContextAction.ShowAllWindows, Label = "显示所有窗口" });
            items.Add(new DockContextMenuEntry { Id = "closeall", Action = DockContextAction.CloseAllWindows, Label = "关闭窗口" });
            items.Add(new DockContextMenuEntry { Id = "quit", Action = DockContextAction.Quit, Label = "退出" });
            items.Add(DockContextMenuEntry.Sep("sep1"));
        }

        items.Add(new DockContextMenuEntry { Id = "unpin", Action = DockContextAction.Unpin, Label = "取消固定", TargetPath = input.LaunchPath });
        items.Add(DockContextMenuEntry.Sep(running ? "sep2" : "sep1"));

        // 三条 shell 项：目标解析不出来时不是"隐藏"而是"置灰 + 说清为什么"，用户才知道这不是我们的 bug
        items.Add(new DockContextMenuEntry
        {
            Id = "runas",
            Action = DockContextAction.RunAsAdmin,
            Label = "以管理员身份运行",
            TargetPath = input.LaunchPath,
            IsEnabled = input.HasExecutableTarget,
            DisabledReason = input.HasExecutableTarget ? null : "目标不是可执行文件"
        });
        items.Add(ShellPathEntry("location", DockContextAction.OpenFileLocation, "打开文件位置", input));
        // 属性页在本机打不开时**整格不给**（见 DockContextMenuInput.PropertiesPageAvailable 的实测说明）
        if (input.PropertiesPageAvailable)
            items.Add(ShellPathEntry("props", DockContextAction.Properties, "属性…", input));

        if (running)
        {
            items.Add(DockContextMenuEntry.Sep("sep3"));
            items.Add(ForceKillEntry(KillTargetFor(input.Windows, input.ForegroundHandle)));
        }

        return items;
    }

    /// <summary>运行窗口条目：窗口级动作在前，固定语义居中，强制结束垫底。</summary>
    private static IReadOnlyList<DockContextMenuEntry> WindowMenu(DockContextMenuInput input)
    {
        var mine = Find(input.Windows, input.ThisWindowHandle);
        var siblings = input.Windows;               // 同进程的全部窗口
        var title = mine?.Title ?? string.Empty;
        var procName = NormalizeProcessName(mine?.ProcessName);
        var procId = mine?.ProcessId ?? 0u;

        var isForeground = input.ForegroundHandle != IntPtr.Zero && input.ThisWindowHandle == input.ForegroundHandle;
        // 固定语义要有**一个可写进 dock.json 的路径**。运行窗口的路径来自进程映像名，权限受限进程
        // （以管理员身份运行的窗口）与部分商店应用宿主读不到 → 空路径。此时"固定到 Dock"若还给亮的，
        // 点下去 <c>DockViewModel.PinExecutable</c> 会因空路径守卫直接 return，用户看到的是"这一格是死的"。
        // 置灰并说明成因。
        var hasPath = input.LaunchPath.Length > 0;
        var items = new List<DockContextMenuEntry>
        {
            new()
            {
                Id = "switch", Action = DockContextAction.SwitchToWindow, Label = "切到该窗口",
                WindowHandle = input.ThisWindowHandle,
                IsEnabled = !isForeground,
                DisabledReason = isForeground ? "该窗口已在前台" : null
            },
            new()
            {
                Id = "min", Action = DockContextAction.Minimize, Label = "最小化",
                WindowHandle = input.ThisWindowHandle,
                IsEnabled = mine is { IsMinimized: false },
                DisabledReason = mine is { IsMinimized: true } ? "该窗口已最小化" : null
            },
            // 标签取"当前做得到的那件事"：已最大化就问要不要还原，这正是系统标题栏按钮的语义
            new()
            {
                Id = "zoom", Action = DockContextAction.MaximizeOrRestore,
                Label = mine is { IsMaximized: true } ? "还原" : "最大化",
                WindowHandle = input.ThisWindowHandle
            },
            DockContextMenuEntry.Sep("sep0"),
            new() { Id = "close", Action = DockContextAction.CloseWindow, Label = "关闭窗口", WindowHandle = input.ThisWindowHandle },
            new()
            {
                Id = "closeallsiblings", Action = DockContextAction.CloseAllWindows, Label = "关闭所有窗口",
                IsEnabled = siblings.Count > 1,
                DisabledReason = siblings.Count > 1 ? null : "该应用只有一个窗口"
            },
            DockContextMenuEntry.Sep("sep1"),
        };

        items.Add(input.IsPinned
            ? new DockContextMenuEntry
            {
                Id = "unpin", Action = DockContextAction.Unpin, Label = "取消固定", TargetPath = input.LaunchPath,
                IsEnabled = hasPath, DisabledReason = hasPath ? null : NoExePath
            }
            : new DockContextMenuEntry
            {
                Id = "pin", Action = DockContextAction.Pin, Label = "固定到 Dock", TargetPath = input.LaunchPath,
                IsEnabled = hasPath, DisabledReason = hasPath ? null : NoExePath
            });

        items.Add(DockContextMenuEntry.Sep("sep2"));
        items.Add(ForceKillEntry((procId, procName)));

        return items;
    }

    /// <summary>回收站：打开 + 清空（清空必须确认，且回收站已空时置灰说清原因）。</summary>
    private static IReadOnlyList<DockContextMenuEntry> RecycleBinMenu(DockContextMenuInput input) => new List<DockContextMenuEntry>
    {
        new() { Id = "open", Action = DockContextAction.OpenRecycleBin, Label = "打开", TargetPath = "shell:RecycleBinFolder" },
        new()
        {
            Id = "empty",
            Action = DockContextAction.EmptyRecycleBin,
            Label = "清空回收站",
            RequiresConfirmation = true,
            ConfirmLabel = "确认清空回收站？项目将被永久删除。",
            IsEnabled = input.RecycleBinHasItems,
            DisabledReason = input.RecycleBinHasItems ? null : "回收站已是空的"
        },
    };

    /// <summary>窗口子菜单的格子：一个窗口一行，标题即入口，前台那格打勾并置灰（切到自己没有意义）。</summary>
    private static IReadOnlyList<DockContextMenuEntry> WindowListChildren(
        IReadOnlyList<DockWindowSnap> windows, IntPtr foreground)
    {
        var children = new List<DockContextMenuEntry>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var isFg = foreground != IntPtr.Zero && w.Handle == foreground;
            children.Add(new DockContextMenuEntry
            {
                Id = "win:" + i,
                Action = DockContextAction.SwitchToWindow,
                Label = w.Title.Length > 0 ? w.Title : "（无标题窗口）",
                WindowHandle = w.Handle,
                IsChecked = isFg,
                IsEnabled = !isFg,
                DisabledReason = isFg ? "已在前台" : null
            });
        }
        return children;
    }

    /// <summary>"打开文件位置 / 属性…"：都要求启动目标文件当前存在，否则给不出可信的动作。</summary>
    private static DockContextMenuEntry ShellPathEntry(
        string id, DockContextAction action, string label, DockContextMenuInput input) => new()
    {
        Id = id,
        Action = action,
        Label = label,
        TargetPath = input.LaunchPath,
        IsEnabled = input.LaunchPathExists,
        DisabledReason = input.LaunchPathExists ? null : MissingTarget
    };

    /// <summary>
    /// 结束任务格子：确认文案里点名进程（"结束"是不可逆的，必须让人看见自己按的是什么）。
    /// 三重守卫在 Core 一次定死：取不到进程 → 置灰；命中黑名单 → 置灰；否则 RequiresConfirmation。
    /// 取不到窗口的进程 id 时**不给这一格**（Build 只在有窗口时才追加），也不给"未知进程"挂一把刀。
    /// </summary>
    private static DockContextMenuEntry ForceKillEntry((uint Id, string Name) target)
    {
        const string label = "结束任务";
        if (target.Id == 0 || target.Name.Length == 0)
            return new DockContextMenuEntry
            {
                Id = "kill", Action = DockContextAction.ForceKill, Label = label,
                IsEnabled = false, DisabledReason = "无法确定所属进程"
            };
        if (IsKillProtected(target.Name))
            return new DockContextMenuEntry
            {
                Id = "kill", Action = DockContextAction.ForceKill, Label = label,
                ProcessId = target.Id, ProcessName = target.Name,
                IsEnabled = false, DisabledReason = $"系统关键进程 {target.Name}，禁止结束"
            };
        return new DockContextMenuEntry
        {
            Id = "kill", Action = DockContextAction.ForceKill, Label = label,
            ProcessId = target.Id, ProcessName = target.Name,
            RequiresConfirmation = true,
            ConfirmLabel = $"确认结束 {target.Name}？未保存的内容会丢失。"
        };
    }

    /// <summary>
    /// "结束任务"作用于哪个进程：优先前台那一格（用户此刻面对的就是它），否则第一个窗口。
    /// 一个固定应用可能有多进程（如 Chromium 系），这里刻意只挑**一个**并在确认文案里点名，
    /// 绝不"顺手把同名进程全杀了"——那是用户没要求的大范围破坏。
    /// </summary>
    private static (uint Id, string Name) KillTargetFor(IReadOnlyList<DockWindowSnap> windows, IntPtr foreground)
    {
        foreach (var w in windows)
            if (foreground != IntPtr.Zero && w.Handle == foreground) return (w.ProcessId, NormalizeProcessName(w.ProcessName));
        var first = windows[0];
        return (first.ProcessId, NormalizeProcessName(first.ProcessName));
    }

    private static DockWindowSnap? Find(IReadOnlyList<DockWindowSnap> windows, IntPtr handle)
    {
        foreach (var w in windows) if (w.Handle == handle) return w;
        return null;
    }
}
