using System;
using System.Runtime.InteropServices;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Interop;

/// <summary>
/// 只负责**取值**：把当前前台窗口的可见矩形与资格标志读成 Core 的
/// <see cref="ForegroundWindow"/> 值对象，"该不该让位"的判定全在
/// <see cref="FullscreenAvoidance"/>（那层才有单测）。
/// <para>
/// 【一次调用的成本】4~6 个 user32/dwmapi 调用，全部是查表式 API，不发消息、不跨进程读写内存，
/// 因此能在 1s 轮询里每轮跑一遍。唯一可能阻塞的是 <c>GetWindowText</c> 那类 SEND 消息，这里一个都不用。
/// </para>
/// </summary>
public static class FullscreenProbe
{
    /// <summary>本进程 pid：前台窗口是我们自己的（Dock/顶栏/设置）时绝不能当让位对象。</summary>
    private static readonly uint CurrentPid = (uint)Environment.ProcessId;

    /// <summary>
    /// shell 类窗口名单——**按类名排除，只有 Progman 这一条是别的判据替代不了的**（下面逐条注明本机取证）。
    /// <list type="bullet">
    /// <item><description>Progman —— 桌面。本机实测 <c>IsWindowVisible=true</c>、矩形 <c>0,0,2880,1920</c>、
    /// <c>DWMWA_CLOAKED=0</c>：可见性判据和 cloaked 判据都拦不住它，只能按类名。少了这一条，
    /// "桌面上没有别的窗口"这一常态会让装饰层永久消失。</description></item>
    /// <item><description>WorkerW —— 壁纸轮播/桌面小组件用的兄弟桌面窗口。本机这一批全是
    /// <c>vis=False</c>（已被可见性挡掉），留在名单里是给"哪天它显形"兜底。</description></item>
    /// <item><description>Shell_TrayWnd / Shell_SecondaryTrayWnd —— 任务栏。本机实测
    /// <c>0,1824,2880,1920</c>，本来就够不到整屏，判据本身能挡住；照抄上游名单是因为玻璃壳就盖在
    /// 它上面，一旦让位给它就是"装饰层压住它装饰的那条栏"这种自指故障。</description></item>
    /// <item><description>DV2ControlHost / XamlExplorerHostIslandWindow —— 旧搜索框宿主与 24H2 的 XAML 岛。
    /// 前者本机根本没有，后者的实测形态是 <c>vis=False cloak=1</c>（两个判据都能挡）。同样是保守名单。</description></item>
    /// <item><description>注意<b>不含</b> <c>Windows.UI.Core.CoreWindow</c>：那个类名同时属于输入法宿主
    /// （该排除）和 UWP 全屏游戏（该让位），按类名一刀切必然误伤一边。改由 <see cref="IsCloaked"/>
    /// 区分——见那条注释里的本机取证。</description></item>
    /// </list>
    /// </summary>
    private static readonly string[] ShellClassNames =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "DV2ControlHost", "XamlExplorerHostIslandWindow",
    ];

    /// <summary>读当前前台窗口；句柄为空、窗口已销毁或读不到矩形时返回 null（调用方据此判"不让位"）。</summary>
    public static ForegroundWindow? Read() => Read(GetForegroundWindow());

    /// <summary>
    /// 按任意句柄读同一套事实。无参重载是产品路径（只看前台），本重载存在是为了让
    /// <c>.tools/zorder-probe</c> 能用**生产代码**把所有可见顶层窗口扫一遍，看哪些会被误判成让位对象。
    /// </summary>
    public static ForegroundWindow? Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        if (!TryVisibleBounds(hwnd, out var bounds)) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        return new ForegroundWindow(
            hwnd,
            bounds,
            IsWindowVisible(hwnd),
            IsIconic(hwnd),
            IsCloaked(hwnd),
            pid == CurrentPid,
            IsShellOrDesktop(hwnd));
    }

    /// <summary>
    /// 可见矩形：<b>先问 DWM 要扩展边框矩形，只有它失败才退回 <c>GetWindowRect</c></b>。
    /// 顺序不能反：有边框窗口的 <c>GetWindowRect</c> 外扩一圈不可见缩放边，用它判"盖住整屏"
    /// 会把"只是最大化"读成全屏。本机实测（2880×1920@200%）最大化的 Chrome 报
    /// <c>-13,-13,2893,1837</c>，而可见像素其实是 <c>0,0,2880,1824</c>。
    /// 无框/工具窗口（真正的全屏游戏多是 WS_POPUP 无框）DWM 会返回失败或零矩形，那才用窗口矩形。
    /// </summary>
    private static bool TryVisibleBounds(IntPtr hwnd, out PhysicalRect bounds)
    {
        if (DwmGetRectAttribute(hwnd, DwmwaExtendedFrameBounds, out var ext, Marshal.SizeOf<RECT>()) == 0
            && ext.Right > ext.Left && ext.Bottom > ext.Top)
        {
            bounds = new PhysicalRect(ext.Left, ext.Top, ext.Right, ext.Bottom);
            return true;
        }

        if (GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            bounds = new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// 被 DWM 藏起来的窗口（cloaked）一个像素都没画，绝不能当"盖住屏幕"的对象。
    /// 这条判据来自本机取证：<c>TextInputHost.exe</c>（输入法/表情面板宿主）的
    /// <c>Windows.UI.Core.CoreWindow</c> 常驻且 <c>IsWindowVisible=true</c>、矩形正好铺满
    /// 2880×1920，cloaked=2。按类名拉黑它会连带误伤同一个类名的 UWP 全屏游戏；
    /// 按 cloaked 拉黑则两边都对——面板藏着一个像素没画，游戏没藏。
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>是不是桌面/外壳窗口：先比根句柄（含子窗口归根），再比类名。</summary>
    private static bool IsShellOrDesktop(IntPtr hwnd)
    {
        var desktop = GetDesktopWindow();
        var shell = GetShellWindow();
        if (hwnd == desktop || hwnd == shell) return true;

        var root = GetAncestor(hwnd, GaRoot);
        if (root == desktop || root == shell) return true;

        var cls = GetWindowClassName(hwnd);
        // 精确匹配、不做前缀或子串：任何第三方应用都可能注册一个以 "Shell" 开头的窗口类，
        // 前缀匹配会把人家的全屏游戏当成 shell 而永远不让位——那正是这个功能要服务的场景。
        foreach (var name in ShellClassNames)
            if (string.Equals(cls, name, StringComparison.Ordinal)) return true;

        return false;
    }
}
