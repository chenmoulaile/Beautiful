using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Dock;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 右键菜单的<b>唯一</b>落地处：所有碰 Win32 / 进程 / shell 的动作都只写在这一个类里
/// （菜单是快照，动作是副作用，两者分开放才审得动）。
/// <para>
/// 【为什么整个方法树包在一层 try 里】Beautiful 是常驻进程：AppBar 已经占了屏幕底边、键盘钩子挂在
/// 全机上。一次右键点击引发的任何异常如果外抛，代价是"整个桌面少一条 Dock、Alt+Tab 静默失效"，
/// 而这与用户点的那一格毫无关系。因此约定：<b>任何动作失败只记日志并 no-op</b>。
/// </para>
/// </summary>
internal sealed class DockContextMenuExecutor
{
    /// <summary>UAC 被用户点"否"时的 Win32 错误码（ERROR_CANCELLED）：这是正常结果，不是故障。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>再启动一次就能开出新窗口的浏览器族（只有它们有可靠的 <c>-new-window</c> 命令行）。</summary>
    private static readonly HashSet<string> NewWindowArgTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "edge", "brave", "vivaldi", "opera", "chromium"
    };

    private readonly WindowTracker _tracker;
    private readonly DockViewModel _vm;

    /// <summary>前台切换：委托给切换器的 <c>Commit</c>（AttachThreadInput + 合成 Alt 的完整链路）。
    /// 绝不在这里再写一份 SetForegroundWindow——两条激活路径迟早漂移，这是本仓库反复付过的学费。</summary>
    private readonly Func<IntPtr, bool> _commitForeground;

    /// <summary>启动成功后的视觉反馈（Dock 的启动弹跳），由 DockWindow 注入以免这里反向依赖渲染器。</summary>
    private readonly Action<DockItem> _onLaunched;

    public DockContextMenuExecutor(
        WindowTracker tracker, DockViewModel vm, Func<IntPtr, bool> commitForeground, Action<DockItem> onLaunched)
    {
        _tracker = tracker;
        _vm = vm;
        _commitForeground = commitForeground;
        _onLaunched = onLaunched;
    }

    /// <summary>执行一格菜单动作。置灰项在这里也直接返回（菜单已经拦住了，但危险动作要双层拦）。</summary>
    public void Execute(DockContextMenu menu, DockContextMenuEntry entry)
    {
        if (!entry.IsEnabled)
        {
            Logger.Info($"Dock 菜单动作被拒：{entry.Label}（{entry.DisabledReason}）");
            return;
        }
        try
        {
            switch (entry.Action)
            {
                case DockContextAction.Open: Launch(entry.TargetPath, menu.Item); break;
                case DockContextAction.OpenRecent: Launch(entry.TargetPath, menu.Item); break;
                case DockContextAction.OpenNewWindow: OpenNewWindow(entry.TargetPath, menu.Item); break;
                case DockContextAction.RunAsAdmin: RunAsAdmin(entry.TargetPath, menu.Item); break;
                case DockContextAction.OpenFileLocation: OpenFileLocation(entry.TargetPath); break;
                case DockContextAction.Properties: ShowProperties(entry.TargetPath); break;
                case DockContextAction.ChangeIcon: ChangeIcon(entry.TargetPath); break;
                case DockContextAction.ResetIcon: ResetIcon(entry.TargetPath); break;
                case DockContextAction.Pin: _vm.PinExecutable(entry.TargetPath ?? string.Empty); break;
                case DockContextAction.Unpin: _vm.Unpin(entry.TargetPath ?? string.Empty); break;
                case DockContextAction.SwitchToWindow: SwitchTo(entry.WindowHandle); break;
                case DockContextAction.ShowAllWindows: ShowAllWindows(menu.Input.Windows); break;
                case DockContextAction.CloseAllWindows:
                case DockContextAction.Quit: CloseAll(menu.Input.Windows, entry.Action); break;
                case DockContextAction.CloseWindow: CloseGraceful(entry.WindowHandle); break;
                case DockContextAction.Minimize: App.MinimizeWindow(entry.WindowHandle); break;
                case DockContextAction.MaximizeOrRestore: ToggleMaximize(entry.WindowHandle); break;
                case DockContextAction.OpenRecycleBin: RecycleBin.Open(); break;
                case DockContextAction.EmptyRecycleBin: RecycleBin.Empty(); break;
                case DockContextAction.ForceKill: ForceKill(entry); break;
                case DockContextAction.WindowList: break;   // 父项：只展开窗口列表，本身没有动作
            }
        }
        catch (Exception ex)
        {
            // 常驻进程绝不为一次菜单点击而死：兜住一切（含 Win32Exception、COMException、进程已退出等）
            Logger.Error($"Dock 菜单动作失败：{entry.Action} / {entry.Label} —— {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 更换图标：弹文件选择框 → Core 规则校验 → 写 dock-icons.json → 重载固定项。
    /// 取消/校验失败都只记日志，不弹模态错误框（用户按取消不是错误）。
    /// </summary>
    private void ChangeIcon(string? pinnedPath)
    {
        if (string.IsNullOrWhiteSpace(pinnedPath))
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图标图片",
            Filter = "图片 (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_vm.IconOverrides.Set(pinnedPath, dialog.FileName, out var reason))
        {
            Logger.Warn($"更换图标失败：{reason}");
            return;
        }

        _vm.ReloadPinned();
        Logger.Info("更换图标：已应用并重载固定项");
    }

    /// <summary>恢复默认图标：删记录并重载。</summary>
    private void ResetIcon(string? pinnedPath)
    {
        if (string.IsNullOrWhiteSpace(pinnedPath))
        {
            return;
        }

        _vm.IconOverrides.Reset(pinnedPath);
        _vm.ReloadPinned();
        Logger.Info("更换图标：已恢复默认");
    }

    // ---------- 启动 ----------

    private void Launch(string? target, DockItem item)
    {
        if (string.IsNullOrWhiteSpace(target)) { Logger.Warn("Dock 菜单：启动目标为空，已忽略"); return; }
        try
        {
            // UseShellExecute=true 才能让 .lnk 带回自己的参数/工作目录、让 shell: 与协议各归其位
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _onLaunched(item);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 菜单启动 {target} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 打开新窗口：浏览器族给 <c>-new-window</c>（这是它们唯一可靠的"再来一扇窗"入口）；
    /// 其余应用退化为"再启动一次"——多实例应用会真开一扇窗，单实例应用由系统把已有窗口带到前台。
    /// 这一格对任何目标都不是假入口：它做的始终是"按这一格的启动目标再发起一次启动"。
    /// </summary>
    private void OpenNewWindow(string? launchPath, DockItem item)
    {
        if (string.IsNullOrWhiteSpace(launchPath)) { Logger.Warn("Dock 菜单：新窗口目标为空，已忽略"); return; }
        var exe = DockViewModel.ResolveTarget(launchPath);
        var name = Path.GetFileNameWithoutExtension(exe);
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && NewWindowArgTargets.Contains(name))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, "-new-window") { UseShellExecute = true });
                _onLaunched(item);
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Dock 菜单：{exe} -new-window 失败，退回再启动一次 —— {ex.Message}");
            }
        }
        Launch(launchPath, item);
    }

    /// <summary>以管理员身份运行：runas 动词。用户在内核提示里点"否"是正常回答，吞掉并记一行，绝不冒异常。</summary>
    private void RunAsAdmin(string? target, DockItem item)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { Verb = "runas", UseShellExecute = true });
            _onLaunched(item);
            Logger.Info($"Dock 菜单：以管理员身份运行 {target}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Logger.Info($"Dock 菜单：UAC 被取消（{target}），不做任何补救");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 菜单：以管理员身份运行 {target} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 打开文件位置：explorer 的 <c>/select,"&lt;路径&gt;"</c>。
    /// 用 <see cref="ProcessStartInfo.Arguments"/> 而不是 <c>ArgumentList</c>：explorer 自己解析命令行，
    /// ArgumentList 的整体转义会把 <c>/select,</c> 与路径粘成一个被再次引用的参数，explorer 认不出来。
    /// 路径里的引号是 NTFS 非法字符，这里仍显式拒绝——绝不把来路不明的字符串拼进一条命令。
    /// </summary>
    private void OpenFileLocation(string? target)
    {
        var path = FullPath(target);
        if (path is null || path.Contains('"', StringComparison.Ordinal))
        {
            Logger.Warn("Dock 菜单：打开文件位置被拒（目标为空或含引号）");
            return;
        }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer)) explorer = "explorer.exe";
        Process.Start(new ProcessStartInfo
        {
            FileName = explorer,
            Arguments = "/select,\"" + path + "\"",
            UseShellExecute = false
        });
    }

    /// <summary>
    /// 属性…：只走 <c>properties</c> 动词（UseShellExecute=true，让 shell 决定弹哪个属性页）。
    /// 【真机边界】Win11 24H2 实测：该动词对快捷方式正常（"Everything 属性"对话框真的弹出来了），
    /// 对裸 .exe 返回 1155 ERROR_NO_ASSOCIATION；shell 的按序号属性页 API 在本进程内（含后台 STA 线程）
    /// 调用直接抛异常。所以 Core 只在目标不是裸 exe 时才给这一格，这里失败就只记日志——
    /// 用户永远看不到"点了没反应"的入口。
    /// </summary>
    private void ShowProperties(string? target)
    {
        var path = FullPath(target);
        if (path is null) return;
        Process.Start(new ProcessStartInfo(path) { Verb = "properties", UseShellExecute = true });
    }

    /// <summary>
    /// dock.json 里的路径是正斜杠写法（JSON 里不用转义反斜杠），而 shell 系列 API 按反斜杠解析。
    /// 统一在这里折算一次；折算不了（非法字符）就记一行并放弃，绝不把怪路径喂给 shell。
    /// </summary>
    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 菜单：路径无法解析，动作已放弃：{path} —— {ex.Message}");
            return null;
        }
    }

    // ---------- 窗口动作 ----------

    private void SwitchTo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            Logger.Info("Dock 菜单：切到该窗口被忽略（该窗口在菜单打开后已经关闭）");
            return;
        }
        if (_commitForeground(hwnd)) return;

        // 切换器不可用（启动降级、服务未装配）时退回跟踪器既有的激活路径；这不是第三条路，是仓库里已有的第二条
        var item = _tracker.Windows.FirstOrDefault(w => w.Handle == hwnd);
        if (item is not null) _tracker.ActivateOrMinimize(item);
        else Logger.Warn($"Dock 菜单：未能激活 0x{hwnd.ToInt64():X}（切换器不可用，且该窗口已不在跟踪列表）");
    }

    /// <summary>显示所有窗口：把该应用被最小化的窗口全部还原（一扇都不藏），再把第一扇带到前台。</summary>
    private void ShowAllWindows(IReadOnlyList<DockWindowSnap> windows)
    {
        var restored = 0;
        foreach (var w in windows)
        {
            if (!NativeMethods.IsWindow(w.Handle)) continue;
            if (NativeMethods.IsIconic(w.Handle)) { NativeMethods.ShowWindow(w.Handle, NativeMethods.SwRestore); restored++; }
        }
        if (windows.Count > 0) SwitchTo(windows[0].Handle);
        Logger.Info($"Dock 菜单：显示所有窗口（共 {windows.Count} 扇，其中 {restored} 扇由最小化还原）");
    }

    /// <summary>关闭窗口 / 退出：对快照里每一扇窗发 <b>WM_CLOSE</b>（正常关闭，应用自己走保存与确认流程）。</summary>
    private void CloseAll(IReadOnlyList<DockWindowSnap> windows, DockContextAction action)
    {
        foreach (var w in windows) CloseGraceful(w.Handle);
        Logger.Info($"Dock 菜单：{(action == DockContextAction.Quit ? "退出" : "关闭窗口")} —— 已向 {windows.Count} 扇窗口发 WM_CLOSE");
    }

    /// <summary>
    /// 正常关闭的唯一实现：<b>WM_CLOSE（PostMessage）</b>。绝不用 DestroyWindow（跳过应用的保存流程，
    /// 等于替用户丢数据），更不在正常路径上调 TerminateProcess。
    /// 沿用标题读取同一套挂死守卫：<c>IsHungAppWindow</c> 为真时 WM_CLOSE 只会排队，这里如实记一行并提示
    /// 用户走"结束任务"——而不是假装关掉了。
    /// </summary>
    private void CloseGraceful(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        if (NativeMethods.IsHungAppWindow(hwnd))
            Logger.Warn($"Dock 菜单：0x{hwnd.ToInt64():X} 所属线程无响应，WM_CLOSE 可能不生效（可用“结束任务”）");
        NativeMethods.PostMessage(hwnd, (uint)NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private void ShowWindow(IntPtr hwnd, int cmd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.ShowWindow(hwnd, cmd);
    }

    /// <summary>最大化 / 还原：按<b>执行时</b>的真实状态取反（菜单打开期间用户可能已经拖了窗口）。</summary>
    private void ToggleMaximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.ShowWindow(hwnd, NativeMethods.IsZoomed(hwnd) ? NativeMethods.SwRestore : NativeMethods.SwMaximize);
    }

    // ---------- 结束任务（破坏性：确认在菜单层，黑名单在这里再拦一次） ----------

    private void ForceKill(DockContextMenuEntry entry)
    {
        var pid = entry.ProcessId;
        if (pid == 0) { Logger.Warn("Dock 菜单：结束任务被拒（无法确定进程 id）"); return; }
        if (pid == (uint)Environment.ProcessId)
        {
            Logger.Error("Dock 菜单：结束任务被拒（目标就是 Beautiful 自身，绝不自我了断）");
            return;
        }

        // 名字以**执行时**读到的为准再核一次黑名单：菜单是快照，而名单是硬约束
        string liveName;
        try { liveName = Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception ex)
        {
            Logger.Info($"Dock 菜单：结束任务未执行（进程已退出或不可读）—— {ex.Message}");
            return;
        }
        if (DockContextMenuPlan.IsKillProtected(liveName) || DockContextMenuPlan.IsKillProtected(entry.ProcessName))
        {
            Logger.Error($"Dock 菜单：结束任务被拒——{liveName}（pid={pid}）是系统关键进程");
            return;
        }

        // 【pid 复用守卫】菜单是几秒前的快照，而 Windows 会回收并复用进程 id：若用户在菜单开着的时候
        // 结束掉那个应用、系统随即把同一个 pid 发给另一个进程，按快照 pid 下刀就是杀了**用户没确认过的那一个**。
        // 进程运行期间映像名不会变，所以"执行时的名字 ≠ 确认文案里点名的名字"只可能是 pid 已被复用。
        if (!string.Equals(liveName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error($"Dock 菜单：结束任务被拒——pid={pid} 现在是 {liveName}，" +
                         $"与确认时点名的 {entry.ProcessName} 不符（进程已退出、id 被系统复用）");
            return;
        }

        try
        {
            var proc = Process.GetProcessById((int)pid);
            proc.Kill(entireProcessTree: true);   // 整树：只杀主进程会留下无主的子进程与窗口
            proc.WaitForExit(2000);
            // 名字只用**杀掉之前**读到的那份：Process 对象在目标退出后连 ProcessName 都会抛
            // （真机第一版就在这里把一次成功的结束任务记成了"结束任务失败"——日志撒谎比日志缺失更糟）
            Logger.Info($"结束任务：{liveName}（pid={pid}），由 Dock 菜单二次确认后执行");
        }
        catch (Exception ex)
        {
            Logger.Error($"结束任务失败：pid={pid} {liveName} —— {ex.Message}", ex);
        }
    }
}

/// <summary>回收站的两条动作（打开 / 清空）。清空只走 shell API，绝不递归删除。</summary>
internal static class RecycleBin
{
    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"打开回收站失败: {ex.Message}"); }
    }

    /// <summary>
    /// 清空回收站：必须走 <c>SHEmptyRecycleBin</c>——它才懂每个卷的 <c>$Recycle.Bin</c> 元数据与安全描述符，
    /// 递归删除会把回收站本身弄坏。三个"不要弹窗"的标志只在<b>我们的确认格被点过之后</b>才使用。
    /// </summary>
    public static void Empty()
    {
        const uint flags = NativeMethods.SherbNoConfirmation | NativeMethods.SherbNoProgressUi | NativeMethods.SherbNoSound;
        var hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
        // S_OK=0 / RECYCLEBOX_EMPTY(0x80041021) 都是"做完了"，其余如实记 hr
        Logger.Info(hr == 0 || hr == 0x8004_1021
            ? $"清空回收站完成（hr=0x{hr:X8}，用户已在菜单确认）"
            : $"清空回收站返回 hr=0x{hr:X8}（未执行或需权限）");
    }
}
