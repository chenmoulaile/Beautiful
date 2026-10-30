using System;
using System.Runtime.InteropServices;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 新窗口打开动画（P5-T3，尽力而为 + 诚实降级）。
/// <para>
/// 【能做到什么】Windows 没有"窗口出现时由 shell 统一播放入场动画"的公开接口。能用的只有
/// <c>AnimateWindow(AW_BLEND)</c>：对一个刚出现的顶层窗口做一次淡入。它由 USER32 内部驱动，
/// 不注入、不改窗口样式（结束后自动恢复），代价是**只对当前线程拥有的窗口可靠**——
/// 跨进程调用经常被目标窗口的 DWM 合成路径忽略，表现为"有的窗口淡入、有的直接出现"。
/// </para>
/// <para>
/// 【为什么默认关闭】这条路径在别人的窗口上不保证生效，且个别应用（自绘/分层窗口）会让动画看起来像闪烁。
/// 因此它是**实验功能**：设置里显式打开，打开后对每个新出现的窗口尝试一次，失败静默。
/// </para>
/// </summary>
public static class WindowOpenAnimator
{
    private const int AwBlend = 0x00080000;
    private const uint SwpNozorder = 0x0004, SwpNoactivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AnimateWindow(IntPtr hWnd, int dwTime, int dwFlags);

    /// <summary>本次进程里已尝试过的窗口（避免同一窗口每次刷新都重放动画）。</summary>
    private static readonly HashSet<IntPtr> Attempted = [];

    /// <summary>
    /// 对一个新出现的窗口尝试淡入。返回是否发出过动画请求（不代表系统真的播了）。
    /// <para>约束：只对可见、非最小化、非工具窗口、且不是我们自己的窗口尝试。</para>
    /// </summary>
    public static bool TryAnimate(IntPtr hwnd, int milliseconds = 120)
    {
        if (hwnd == IntPtr.Zero || !Attempted.Add(hwnd))
        {
            return false;
        }

        // 只处理顶层可见窗口：子窗口/隐藏窗口调用只会失败或抢焦点
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(hwnd, -16);   // GWL_STYLE
        const long wsChild = 0x40000000, wsDisabled = 0x08000000;
        if ((style & wsChild) != 0 || (style & wsDisabled) != 0)
        {
            return false;
        }

        try
        {
            // AW_BLEND + 不激活：动画期间不把焦点从用户手上抢走
            var ok = AnimateWindow(hwnd, milliseconds, AwBlend);
            if (!ok)
            {
                Logger.Info($"窗口动画：AnimateWindow 被目标窗口拒绝（cls 不兼容），已跳过");
            }

            return ok;
        }
        catch (Exception ex)
        {
            Logger.Warn($"窗口动画失败：{ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>测试/重置用：清掉"已尝试"记录（例如用户重新打开开关）。</summary>
    public static void Reset() => Attempted.Clear();
}
