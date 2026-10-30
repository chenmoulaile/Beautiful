using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Controls;
using MessageBox = System.Windows.MessageBox;

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏左区各菜单内容构建（Task 9）。macOS 风格条目集合：
/// <list type="bullet">
/// <item><description><see cref="BeautifulMenu"/>：logo 菜单（关于/设置…/主题▸/重载 Dock/分隔/退出）——全部真实。</description></item>
/// <item><description><see cref="WindowMenu"/>：窗口菜单——真实（列出跟踪窗口带图标、勾选前台项、点击激活、最小化/关闭当前）。</description></item>
/// <item><description>文件/编辑/视图/帮助：macOS 风格条目；无对应系统能力的项按控制器裁定渲染为置灰
/// （<see cref="MenuItemView.IsEnabled"/>=false），绝不假装有功能；能接系统能力的（打开资源管理器/显示桌面/关于）为真实项。</description></item>
/// </list>
/// 菜单树每次弹出前重建（勾选态/窗口列表 = 打开时快照，与 <see cref="FluentMenu"/> 的无状态语义一致）。
/// </summary>
public static class TopBarMenus
{
    // Segoe Fluent Icons 码点（与托盘菜单同一取值）
    private const string GearGlyph = "\uE713", ThemeGlyph = "\uE790", ReloadGlyph = "\uE72C",
                       QuitGlyph = "\uE7E8", InfoGlyph = "\uE946", MinGlyph = "\uE73F",
                       CloseGlyph = "\uE711", FolderGlyph = "\uE8B7", DesktopGlyph = "\uE7F4",
                       HelpGlyph = "\uE897";

    /// <summary>logo（"Beautiful" 应用菜单）：关于 / 设置… / 主题▸ / 重载 Dock / 分隔 / 退出。</summary>
    public static List<MenuItemView> BeautifulMenu(TopBarContext ctx) => new()
    {
        new() { Id = "about", Label = "关于 Beautiful", Glyph = InfoGlyph, Invoke = ShowAbout },
        new() { Id = "sep0", IsSeparator = true },
        new() { Id = "settings", Label = "设置…", Glyph = GearGlyph, Invoke = ctx.OpenSettings },
        new() { Id = "syssettings", Label = "系统设置…", Glyph = GearGlyph, Invoke = () => Launch("ms-settings:") },
        new() { Id = "store", Label = "Microsoft Store…", Invoke = () => Launch("ms-windows-store:") },
        new() { Id = "taskmgr", Label = "任务管理器…", Invoke = () => Launch("taskmgr.exe") },
        new() { Id = "sep1", IsSeparator = true },
        new() { Id = "sleep", Label = "睡眠", Invoke = Sleep },
        new() { Id = "restart", Label = "重新启动…", Invoke = () => PowerAction(ctx, "/r /t 0", "重新启动") },
        new() { Id = "shutdown", Label = "关机…", Invoke = () => PowerAction(ctx, "/s /t 0", "关机") },
        new() { Id = "sep2", IsSeparator = true },
        new() { Id = "lock", Label = "锁定屏幕", Invoke = Lock },
        new() { Id = "signout", Label = "注销…", Invoke = () => PowerAction(ctx, "/l", "注销") },
        new() { Id = "sep3", IsSeparator = true },
        new() { Id = "theme", Label = "主题", Glyph = ThemeGlyph, Children = ThemeSubmenu(ctx) },
        new() { Id = "reload", Label = "重载 Dock", Glyph = ReloadGlyph, Invoke = ctx.ReloadDock },
        new() { Id = "sep4", IsSeparator = true },
        new() { Id = "quit", Label = "退出 Beautiful", Glyph = QuitGlyph, Invoke = ctx.Exit },
    };

    private static List<MenuItemView> ThemeSubmenu(TopBarContext ctx)
    {
        var current = ctx.CurrentTheme();
        // 选项直接来自 QuickSettings.Themes 这一份白名单（与控制中心分段同源）：
        // v4 只剩 macOS；未来换外观模式时同样只改这份投影。
        return QuickSettings.Themes.Select(o => new MenuItemView
        {
            Id = o.Id, Label = o.Label, IsChecked = current == o.Id, Invoke = () => ctx.ApplyTheme(o.Id),
        }).ToList();
    }

    /// <summary>文件：新建窗口（无对应能力→置灰）、打开资源管理器…（真实）、分隔、关闭窗口（置灰）。</summary>
    public static List<MenuItemView> FileMenu() => new()
    {
        new() { Id = "new", Label = "新建窗口", Glyph = "\uE710", IsEnabled = false },
        new() { Id = "open", Label = "打开资源管理器…", Glyph = FolderGlyph, Invoke = () => Launch("explorer.exe") },
        new() { Id = "sep", IsSeparator = true },
        new() { Id = "close", Label = "关闭窗口", Glyph = CloseGlyph, IsEnabled = false },
    };

    /// <summary>编辑：撤销/重做/剪切/拷贝/粘贴/全选——跨应用文本命令无可靠注入手段（会抢焦点/破坏目标应用状态），
    /// 按裁定全部置灰而非假装有功能。</summary>
    public static List<MenuItemView> EditMenu() => new()
    {
        new() { Id = "undo", Label = "撤销", IsEnabled = false },
        new() { Id = "redo", Label = "重做", IsEnabled = false },
        new() { Id = "sep0", IsSeparator = true },
        new() { Id = "cut", Label = "剪切", IsEnabled = false },
        new() { Id = "copy", Label = "拷贝", IsEnabled = false },
        new() { Id = "paste", Label = "粘贴", IsEnabled = false },
        new() { Id = "select", Label = "全选", IsEnabled = false },
    };

    /// <summary>视图：显示桌面（真实：最小化所有跟踪窗口）、分隔、进入全屏模式（无对应能力→置灰）。</summary>
    public static List<MenuItemView> ViewMenu(WindowTracker? tracker) => new()
    {
        new() { Id = "desktop", Label = "显示桌面", Glyph = DesktopGlyph, Invoke = () => ShowDesktop(tracker) },
        new() { Id = "sep", IsSeparator = true },
        new() { Id = "fullscreen", Label = "进入全屏模式", IsEnabled = false },
    };

    /// <summary>帮助：关于 Beautiful（真实）、键盘快捷键…（无对应界面→置灰）、分隔、反馈（置灰）。</summary>
    public static List<MenuItemView> HelpMenu() => new()
    {
        new() { Id = "about", Label = "关于 Beautiful", Glyph = InfoGlyph, Invoke = ShowAbout },
        new() { Id = "keys", Label = "键盘快捷键…", Glyph = "\uE765", IsEnabled = false },
        new() { Id = "sep", IsSeparator = true },
        new() { Id = "feedback", Label = "发送反馈…", IsEnabled = false },
    };

    /// <summary>
    /// 窗口：真实列出所有跟踪窗口（位图图标 + 标题），点击 = 激活（前台项则最小化，复用
    /// <see cref="WindowTracker.ActivateOrMinimize"/>），当前前台项打勾；末尾对前台窗口追加
    /// 最小化/关闭（ShowWindow / WM_CLOSE）。无跟踪窗口时显示置灰"（无窗口）"。
    /// <paramref name="foreground"/> 为弹出前顶栏记录的真实前台句柄（弹层显示后前台即弹层本身，故须外部传入）。
    /// </summary>
    public static List<MenuItemView> WindowMenu(WindowTracker? tracker, IntPtr foreground)
    {
        var items = new List<MenuItemView>();
        var windows = tracker?.Windows;
        if (windows is null || windows.Count == 0)
        {
            items.Add(new MenuItemView { Id = "nowin", Label = "（无窗口）", IsEnabled = false });
            return items;
        }

        WindowItem? active = null;
        foreach (var w in windows)
        {
            bool isActive = w.Handle == foreground;
            if (isActive) active = w;
            var captured = w; // 闭包捕获当前项（foreach 变量在 lambda 中需固定）
            items.Add(new MenuItemView
            {
                Id = "win:" + captured.Handle,
                // 标题压短：菜单宽度由最长的一条决定，而 Windows 的窗口标题常是"完整路径 • 工程 - 编辑器"。
                // 原文进 ToolTip，压窄不丢信息。
                Label = Beautiful.Core.TopBar.WindowMenuLabel.Compact(captured.Title),
                ToolTip = captured.Title,
                Icon = captured.Icon,       // 真实窗口位图图标（无则 HasIcon=false → 回退空列）
                Glyph = "\uE7C3",           // 无图标时的占位字形（窗口）
                IsChecked = isActive,
                Invoke = () => tracker!.ActivateOrMinimize(captured),
            });
        }

        if (active is not null)
        {
            var h = active.Handle;
            items.Add(new MenuItemView { Id = "sep", IsSeparator = true });
            items.Add(new MenuItemView { Id = "min", Label = "最小化", Glyph = MinGlyph,
                Invoke = () => NativeMethods.ShowWindow(h, NativeMethods.SwMinimize) });
            items.Add(new MenuItemView { Id = "close", Label = "关闭", Glyph = CloseGlyph,
                Invoke = () => NativeMethods.PostMessage(h, (uint)NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero) });
        }
        return items;
    }

    // ---------- 落地动作 ----------

    private static void ShowDesktop(WindowTracker? tracker)
    {
        if (tracker is null) return;
        foreach (var w in tracker.Windows)
            NativeMethods.ShowWindow(w.Handle, NativeMethods.SwMinimize);
    }

    private static void ShowAbout()
        => MessageBox.Show(
            "Beautiful\n\nmacOS 风格悬浮 Dock + 顶部菜单栏（Finder 栏）。\nWindows 11 · WPF · .NET 8。",
            "关于 Beautiful", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>睡眠：powrprof SetSuspendState（不强制、不关唤醒）。失败只记日志，不弹假成功。</summary>
    private static void Sleep()
    {
        try
        {
            if (!SetSuspendState(false, false, false))
                MessageBox.Show("睡眠请求失败（可能被策略禁用）。", "Beautiful", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"睡眠请求失败：{ex.Message}", "Beautiful", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>锁定屏幕：LockWorkStation，失败无副作用。</summary>
    private static void Lock()
    {
        try { LockWorkStation(); }
        catch { /* 顶栏菜单动作失败不拖垮 UI */ }
    }

    /// <summary>重启/关机/注销：先二次确认，再走 Beautiful 完整退出链路，最后交给系统 shutdown。</summary>
    private static void PowerAction(TopBarContext ctx, string shutdownArgs, string title)
    {
        var answer = MessageBox.Show(
            $"{title}会先退出 Beautiful，再执行系统操作。\n\n确认{title}？",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try { ctx.Exit(); } catch { /* 退出链路自身已有兜底 */ }
        Launch("shutdown.exe", shutdownArgs);
    }

    /// <summary>Shell 启动（explorer.exe / ms-settings: URI / sndvol.exe）：UseShellExecute 让系统解析协议。</summary>
    public static void Launch(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = arguments ?? string.Empty
            });
        }
        catch
        {
            // 状态项/菜单动作的落地失败不应崩溃顶栏（目标不存在或被策略禁用）
        }
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}
