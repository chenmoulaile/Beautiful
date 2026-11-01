using System;
using System.Runtime.InteropServices;
using System.Text;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;

namespace Beautiful.App.Services;

/// <summary>
/// 顶层窗口资格判定与窗口探测的**唯一实现**（v1 WindowTracker 的规则原样抽出，改为共享而非复制）。
/// Dock（WindowTracker）与切换器（SwitcherService）必须使用同一套可见性规则，否则两处列表会各自漂移
/// （Dock 里存在的窗口在切换器里消失，或反之）。切换器那条"无边框全屏"直通判据同样集中在这里
/// （几何比较共用 <see cref="FullscreenAvoidance.CoversMonitor"/>；装饰层的让位判据是另一套，
/// 两者为什么不能合并见 <see cref="IsExclusiveFullscreen"/> 的注释）。
/// </summary>
internal static class WindowEligibility
{
    /// <summary>
    /// "被我们斗篷代管的窗口"豁免（alpha.21 过渡接管）：斗篷窗口在 DWM 查询里 cloaked=1，
    /// 不豁免就会从 Dock/切换器里凭空消失。装配方在动画器装配后注入（指到
    /// WindowTransitionGuard.IsCloakedByUs）；null = 未接管，按默认规则过滤。
    /// </summary>
    public static Func<IntPtr, bool>? ManagedCloakOverride;

    /// <summary>
    /// 是否作为"可切换窗口"呈现：可见 + 有标题 + 非本进程 + 未被 DWM cloak + 非工具窗口 +
    /// 非任务栏/桌面/ UWP 宿主类窗口。规则与 v1 WindowTracker 完全一致。
    /// </summary>
    /// <param name="h">待判定句柄。</param>
    /// <param name="ownProcessId">本进程 PID（自身 Dock/顶栏/弹层窗口一律排除）。</param>
    public static bool IsEligibleWindow(IntPtr h, uint ownProcessId)
    {
        if (!NativeMethods.IsWindowVisible(h)) return false;
        // 【终审 I-7 · 键盘钩子的承重墙，勿删】IsHungAppWindow 必须早于任何窗口文本查询：
        // GetWindowTextLength/GetWindowText 是 **SEND** 消息——同步发送到窗口所属线程，对方挂死
        // 时调用线程一并被挂住。本判定运行在两条 2s UI 线程定时器驱动的 EnumWindows 遍历中
        // （WindowTracker、SwitcherService），一次挂死的第三方窗口即可冻死我们的
        // UI 线程；而 WH_KEYBOARD_LL 回调同样在这条线程的消息泵上——超时后系统会给**全机每一次
        // 按键**都加上 LowLevelHooksTimeout 延迟并静默摘掉我们的钩子（v1 有遍历无钩子，未暴露）。
        // 挂死窗口本也不该出现在列表里：早退既是过滤，更是对键盘钩子的保护。
        if (NativeMethods.IsHungAppWindow(h)) return false;
        if (NativeMethods.GetWindowTextLength(h) == 0) return false;
        if (NativeMethods.GetWindowThreadProcessId(h, out var pid) == 0) return false;
        if (pid == ownProcessId) return false;
        // 过滤 UWP 幽灵与浏览器 cloak 窗口（被我们代管斗篷的窗口豁免：Dock/切换器里必须还能看到它）
        if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DwmwaCloaked, out var cloaked, 4) == 0
            && cloaked != 0 && ManagedCloakOverride?.Invoke(h) != true)
            return false;
        // 过滤工具窗口（WS_EX_TOOLWINDOW）
        var ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GwlpExStyle);
        if ((ex & NativeMethods.WsExToolwindow) != 0) return false;
        // 排除任务栏自身与桌面/UWP 宿主类窗口
        var cls = NativeMethods.GetWindowClassName(h);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "WorkerW" or "Progman" or "Windows.UI.Core.CoreWindow")
            return false;
        return true;
    }

    /// <summary>
    /// 窗口标题（无标题或窗口挂死返回空串）。
    /// 挂死早退是 <see cref="IsEligibleWindow"/> 同一道理的收口保护：本方法也会被
    /// 前台事件路径单独调用（不经 IsEligibleWindow），而 GetWindowText 是 SEND 消息。
    /// </summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (NativeMethods.IsHungAppWindow(hwnd)) return string.Empty;
        var len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 窗口所属进程路径（取不到返回空串——UWP/权限受限进程常见）。
    /// </summary>
    public static string GetWindowExePath(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 ? NativeMethods.GetProcessPath(pid) ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// 是否为"独占/无边框全屏"前台窗口（游戏、视频播放器）——切换器的直通判据。
    /// 判据：非最小化 + 非工具窗口 + **无 WS_CAPTION** + 窗口矩形完全覆盖所在显示器。
    /// 特意不用 DWM 的 extended frame bounds：常规最大化窗口虽然 IsZoomed，但带标题栏已被排除；
    /// 覆盖整屏且无标题栏的组合，实际只出现在 borderless/exclusive 全屏应用上。
    /// 误判代价是"这一次 Alt+Tab 交给系统"（保守方向），比在反作弊游戏中吞掉按键安全得多。
    /// <para>
    /// 【与 <see cref="FullscreenProbe"/> 的分工，别当成重复代码】两处都在判"全屏"，但保守方向相反，
    /// 所以判据不能合并：这里错判的代价是"Alt+Tab 还给系统一次"，全屏避让错判的代价是"美化层凭空消失"。
    /// 于是这里额外要"无标题栏"这一条硬条件、且不查 cloaked（切换器不在乎谁铺满屏，只在乎要不要抢键）；
    /// 那边用 DWM 可见像素 + cloak/进程/shell 三重资格。
    /// **唯一共用的是那条"四条边都盖住显示器"的几何**——收口到 <see cref="FullscreenAvoidance.CoversMonitor"/>，
    /// 免得两份 tolerance 各写各的。
    /// </para>
    /// </summary>
    public static bool IsExclusiveFullscreen(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
            if (NativeMethods.IsIconic(hwnd)) return false;

            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlpExStyle);
            if ((ex & NativeMethods.WsExToolwindow) != 0) return false;

            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlpStyle);
            if ((style & NativeMethods.WsCaption) != 0) return false; // 带标题栏 = 常规窗口/最大化窗口

            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 200) return false; // 退化数据保护

            var center = new NativeMethods.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
            var mon = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
            if (mon == IntPtr.Zero) return false;
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

            var m = mi.rcMonitor;
            return FullscreenAvoidance.CoversMonitor(
                new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom),
                new PhysicalRect(m.Left, m.Top, m.Right, m.Bottom));
        }
        catch
        {
            // 全屏判定发生在 Alt+Tab 按键路径上：任何异常都保守地视为"非全屏"（照常接管），
            // 由调用方的 try/catch 再兜一层；此处不外抛以免污染钩子回调。
            return false;
        }
    }
}
