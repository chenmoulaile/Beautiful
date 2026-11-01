using System.Collections.Generic;

namespace Beautiful.App.Animation;

/// <summary>
/// "自家内容窗口"登记表：开窗动画接管器（<see cref="MinimizeAnimator"/> 的 OBJECT_SHOW 路径）
/// 默认把<b>本进程</b>的所有窗口都排除在 mac 开窗动画之外——因为 Dock/顶栏/启动台/控制中心
/// 这类自家表面绝不能被斗篷+替身接管（它们有自己的动效，接管会自我打架）。
/// <para>
/// 但访达这类"内容窗口"同样是自家进程，被一刀切排除的后果是它永远播系统原生开窗动画
/// （真机反馈原话："访达的滑动动画退回 windows 的了"）。登记表就是那把白名单钥匙：
/// 内容窗口在 SourceInitialized 时注册、Closed 时注销，只有在册 hwnd 才走自家窗口的
/// mac 开窗链路。
/// </para>
/// <para>
/// 线程安全：注册/查询发生在 WinEvent 回调线程与 UI 线程两侧，统一加锁。
/// hwnd 复用防护：窗口 Closed 必注销——面板类窗口（控制中心等）由 WPF 反复创建销毁，
/// 若留下死 hwnd，系统复用句柄后会把接管器引到错误的窗口上。
/// </para>
/// </summary>
internal static class SelfContentWindows
{
    private static readonly object Gate = new();
    private static readonly HashSet<System.IntPtr> Registered = new();

    public static void Register(System.IntPtr hwnd)
    {
        if (hwnd == System.IntPtr.Zero) return;
        lock (Gate) Registered.Add(hwnd);
    }

    public static void Unregister(System.IntPtr hwnd)
    {
        if (hwnd == System.IntPtr.Zero) return;
        lock (Gate) Registered.Remove(hwnd);
    }

    public static bool Contains(System.IntPtr hwnd)
    {
        if (hwnd == System.IntPtr.Zero) return false;
        lock (Gate) return Registered.Contains(hwnd);
    }
}
