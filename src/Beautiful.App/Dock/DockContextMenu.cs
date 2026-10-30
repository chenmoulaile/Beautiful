using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Dock;

namespace Beautiful.App.Dock;

/// <summary>
/// 一次右键的成品：<b>Core 的决定</b>（<see cref="Entries"/>）+ <b>决策依据的快照</b>（<see cref="Input"/>）
/// + 快照里那批窗口的图标（<see cref="Icons"/>，句柄 → 位图）。
/// <para>
/// 快照必须与菜单一起留着："关闭窗口/结束任务"要动手在<b>用户看到的那几扇窗</b>上。事后重新枚举会牵连
/// 到用户在菜单打开期间新开或已关的窗口——那是"我点的是这一格，关掉的却是别一格"。
/// </para>
/// </summary>
internal sealed record DockContextMenu(
    DockItem Item,
    DockContextMenuInput Input,
    IReadOnlyList<DockContextMenuEntry> Entries,
    IReadOnlyDictionary<IntPtr, ImageSource?> Icons);

/// <summary>
/// App 侧的菜单装配：把 <see cref="DockItem"/> / <see cref="WindowTracker"/> / dock.json 现值折算成 Core
/// 的输入快照，再把 Core 的决定一对一映射成 <see cref="MenuItemView"/>。
/// <b>这里没有一条业务规则</b>——"该给哪些条目、谁该置灰、为什么置灰"全部在
/// <see cref="DockContextMenuPlan"/>（纯数据、可单测），本文件只做采集与渲染映射。
/// 分隔符与媒体胶囊返回 null：保持现状不弹菜单。
/// </summary>
internal static class DockContextMenuBuilder
{
    /// <summary>
    /// <paramref name="foreground"/> 必须是弹出菜单<b>之前</b>取的真实前台句柄：弹层一显示，前台就是菜单自己，
    /// 现场再读会把"当前前台窗"永远判成无（与顶栏"窗口"菜单同一口径，见 <c>TopBarMenus.WindowMenu</c>）。
    /// </summary>
    public static DockContextMenu? Build(DockItem item, DockViewModel vm, WindowTracker tracker, IntPtr foreground)
    {
        if (item.Kind is DockItemKind.Separator or DockItemKind.Media) return null;

        // 先挑出"这一格代表的那几扇窗"，再一次性采集图标：两者必须来自同一次遍历
        var related = item.Kind switch
        {
            DockItemKind.Pinned => WindowsOfApp(tracker, item.LaunchPath),
            DockItemKind.Window => WindowsOfProcess(tracker, item.WindowHandle),
            _ => new List<WindowItem>()
        };
        var snaps = related.Select(Snap).ToList();
        var icons = new Dictionary<IntPtr, ImageSource?>();
        foreach (var w in related) icons[w.Handle] = w.Icon;

        var executable = item.Kind == DockItemKind.RecycleBin
            ? string.Empty
            : DockViewModel.ResolveTarget(item.LaunchPath);

        var input = new DockContextMenuInput
        {
            Kind = item.Kind,
            LaunchPath = item.LaunchPath,
            LaunchPathExists = Exists(item.LaunchPath),
            // 只有解析出来真的是 .exe 才谈得上"以管理员身份运行"：.lnk 指向文档/文件夹时 runas 没有可靠语义
            HasExecutableTarget = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            // 固定项天然在固定块里；运行窗口项要查 dock.json 才知道该给"固定"还是"取消固定"
            IsPinned = item.Kind != DockItemKind.Pinned && vm.IsPinnedPath(item.LaunchPath),
            Windows = snaps,
            ThisWindowHandle = item.Kind == DockItemKind.Window ? item.WindowHandle ?? IntPtr.Zero : IntPtr.Zero,
            ForegroundHandle = foreground,
            RecentItems = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? RecentItemsService.FindForApp(executable, 6)
                : Array.Empty<DockRecentItem>(),
            RecycleBinHasItems = item.Kind == DockItemKind.RecycleBin && RecycleBinHasItems(),
            // 真机实测（Win11 24H2）：properties 动词对快捷方式可用、对裸 .exe 返回 1155，
            // 而 shell 的按序号属性页 API 在本进程内调用会抛 → 裸 exe 干脆不给这一格（见 Core 的字段说明）
            HasIconOverride = vm.IconOverrides.Has(item.LaunchPath),
            PropertiesPageAvailable = !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                      || item.LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
        };
        return new DockContextMenu(item, input, DockContextMenuPlan.Build(input), icons);
    }

    /// <summary>Core 条目 → 自绘菜单项。置灰项把原因拼进标签，让"为什么不能点"在菜单上就看得见。</summary>
    public static List<MenuItemView> ToViews(DockContextMenu menu, DockContextMenuExecutor exec) =>
        menu.Entries.Select(e => ToView(menu, e, exec)).ToList();

    private static MenuItemView ToView(DockContextMenu menu, DockContextMenuEntry e, DockContextMenuExecutor exec)
    {
        if (e.IsSeparator) return new MenuItemView { Id = e.Id, IsSeparator = true };

        var label = e.IsEnabled || string.IsNullOrEmpty(e.DisabledReason)
            ? e.Label
            : $"{e.Label}（{e.DisabledReason}）";
        // 位图只给"某一扇窗"那一格（窗口子菜单）；其余格子用字形或留空保持对齐
        var icon = e.Action == DockContextAction.SwitchToWindow && e.Children is null
            ? menu.Icons.GetValueOrDefault(e.WindowHandle)
            : null;

        List<MenuItemView>? children = null;
        if (e.Children is { Count: > 0 })
        {
            children = e.Children.Select(c => ToView(menu, c, exec)).ToList();
        }
        else if (e.RequiresConfirmation && e.IsEnabled)
        {
            // 破坏性动作：父项只展开不执行（FluentMenu 对父项点击的语义正是"展开"），
            // 第二格的文案把"要结束谁/要删什么"写在脸上，点它才真正落地。
            children = new List<MenuItemView>
            {
                new()
                {
                    Id = e.Id + ":confirm",
                    Label = e.ConfirmLabel ?? "确认执行？",
                    Glyph = CloseGlyph,
                    Invoke = () => exec.Execute(menu, e)
                }
            };
        }

        // 叶子项才挂动作：父项（窗口列表 / 确认链）在 FluentMenu 里的点击语义是"展开"，绝不误执行
        var invoke = children is null && e.IsEnabled ? new Action(() => exec.Execute(menu, e)) : null;

        return new MenuItemView
        {
            Id = e.Id,
            Label = label,
            Glyph = GlyphOf(e.Action),
            Icon = icon,
            IsChecked = e.IsChecked,
            IsEnabled = e.IsEnabled,
            Children = children,
            Invoke = invoke
        };
    }

    // ---------- 快照采集（本文件唯一读 Win32 的地方：任何一步失败都只是降级，不抛） ----------

    private static DockWindowSnap Snap(WindowItem w)
    {
        uint pid = 0;
        try { NativeMethods.GetWindowThreadProcessId(w.Handle, out pid); }
        catch { /* 句柄刚失效：pid 留 0，Core 据此把"结束任务"置灰为"无法确定所属进程" */ }

        var name = Path.GetFileNameWithoutExtension(w.ExePath);
        if (name.Length == 0 && pid != 0)
        {
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { name = string.Empty; }   // 进程已退出 / 权限受限（UWP 主进程名取不到）
        }

        bool zoomed;
        try { zoomed = NativeMethods.IsWindow(w.Handle) && NativeMethods.IsZoomed(w.Handle); }
        catch { zoomed = false; }

        return new DockWindowSnap(w.Handle, w.Title, pid, name, w.IsMinimized, zoomed);
    }

    /// <summary>同一可执行文件的全部窗口：按<b>解析后的绝对路径</b>判同族（.lnk 与其目标 exe 是同一个应用）。</summary>
    private static List<WindowItem> WindowsOfApp(WindowTracker tracker, string launchPath)
    {
        var target = DockViewModel.ResolveTarget(launchPath);
        if (target.Length == 0) return new List<WindowItem>();
        return tracker.Windows.Where(w => w.ExePath.Length > 0 && SamePath(w.ExePath, target)).ToList();
    }

    /// <summary>同一进程的全部窗口（运行窗口条目的"兄弟窗"）；取不到 pid 时只有它自己那一扇。</summary>
    private static List<WindowItem> WindowsOfProcess(WindowTracker tracker, IntPtr? handle)
    {
        if (handle is not { } self || self == IntPtr.Zero) return new List<WindowItem>();
        uint pid = 0;
        try { NativeMethods.GetWindowThreadProcessId(self, out pid); } catch { }
        // 本条目自己一定算：枚举期间句柄状态可能已经变了，丢了它菜单就连自己都不认识
        return pid == 0
            ? tracker.Windows.Where(w => w.Handle == self).ToList()
            : tracker.Windows.Where(w => w.Handle == self || SamePid(w, pid)).ToList();
    }

    private static bool SamePid(WindowItem w, uint pid)
    {
        try { NativeMethods.GetWindowThreadProcessId(w.Handle, out var other); return other == pid; }
        catch { return false; }
    }

    /// <summary>回收站有没有内容（决定"清空回收站"是真能做还是已无可清）。读不到就当作有。</summary>
    private static bool RecycleBinHasItems()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
            };
            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            return hr != 0 || info.i64NumItems > 0;
        }
        catch { return true; }
    }

    private static bool Exists(string path)
    {
        if (path.Length == 0 || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        try { return File.Exists(path); }
        catch { return false; }   // 路径含非法字符时 File.Exists 会抛 ArgumentException
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // ---------- 字形：只取本仓库已在用的码点（未验证的码点会渲染成缺字方框，宁可留空保持对齐） ----------
    private const string CloseGlyph = "\uE711", MinGlyph = "\uE73F", QuitGlyph = "\uE7E8",
                         WindowGlyph = "\uE7C3", FolderGlyph = "\uE8B7", GearGlyph = "\uE713",
                         DesktopGlyph = "\uE7F4";

    private static string? GlyphOf(DockContextAction action) => action switch
    {
        DockContextAction.OpenNewWindow or DockContextAction.WindowList => WindowGlyph,
        DockContextAction.ShowAllWindows => DesktopGlyph,
        DockContextAction.CloseAllWindows or DockContextAction.CloseWindow => CloseGlyph,
        DockContextAction.Quit => QuitGlyph,
        DockContextAction.Minimize => MinGlyph,
        DockContextAction.OpenFileLocation => FolderGlyph,
        DockContextAction.Properties => GearGlyph,
        _ => null
    };
}
