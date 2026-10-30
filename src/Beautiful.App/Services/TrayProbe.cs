using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Beautiful.App.Services;

/// <summary>
/// 托盘真实像素的**只读探针**（P4-T8 第一步，不注入）。
/// <para>
/// 【为什么先探针】托盘图标在 explorer 的 XAML 岛里，读它只有两条路：注入（写 explorer 的进程内存）
/// 或 UIA（跨进程读无障碍树）。后者是只读的，最坏情况也只是"读不到"，不会影响任务栏。
/// 因此先用探针确认 Win11 24H2 的托盘在 UIA 里到底暴露成什么样：有几个按钮、名字是什么、
/// 有没有独立窗口（Win11 的托盘是独立的 XamlExplorerHostIslandWindow）。
/// </para>
/// <para>
/// 【探针只写位置与名字】日志里不写图标像素，也不写用户的通知内容——托盘按钮的 Name 通常是应用名，
/// 这与任务栏本身可见的信息一致。
/// </para>
/// </summary>
public static class TrayProbe
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int max);

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "?";
    }

    /// <summary>
    /// 枚举任务栏与托盘区的 UIA 子树并写日志。返回找到的候选按钮数（0 = UIA 读不到）。
    /// </summary>
    public static int Run(int maxLog = 40)
    {
        var total = 0;
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TopLevelWindowForOverflowXamlIsland" })
        {
            var hwnd = Interop.NativeMethods.FindWindowClass(className);
            Logger.Info($"托盘探针：{className} hwnd=0x{hwnd.ToInt64():X}");
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Logger.Info($"托盘探针：{className} 子树元素 {all.Count} 个");

                var logged = 0;
                for (var i = 0; i < all.Count && logged < maxLog; i++)
                {
                    var element = all[i];
                    var name = element.Current.Name;
                    var type = element.Current.ControlType?.ProgrammaticName ?? "?";
                    var cls = element.Current.ClassName;
                    var rect = element.Current.BoundingRectangle;

                    // 只记有名字或像按钮的元素：整棵树 dump 会把日志刷爆，也看不出结构
                    if (string.IsNullOrEmpty(name) && type is not ("ControlType.Button" or "ControlType.Pane"))
                    {
                        continue;
                    }

                    Logger.Info($"托盘探针：  [{logged}] {type} name=[{name}] cls=[{cls}] " +
                                $"rect=({rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0})");
                    logged++;
                    if (type is "ControlType.Button") total++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"托盘探针：{className} 读取失败 {ex.GetType().Name} {ex.Message}");
            }
        }

        // UIA 在 Shell_TrayWnd 上读不到（Win11 的托盘在子窗口/XAML 岛里）：把子窗口逐个列出来，
        // 每个都试一次 UIA —— 这一步是"注入 vs UIA"裁定的关键证据。
        var tray = Interop.NativeMethods.FindWindowClass("Shell_TrayWnd");
        if (tray != IntPtr.Zero)
        {
            EnumChildWindows(tray, (child, _) =>
            {
                var cls = ClassOf(child);
                Logger.Info($"托盘探针：Shell_TrayWnd 子窗口 cls=[{cls}] hwnd=0x{child.ToInt64():X}");
                try
                {
                    var root = AutomationElement.FromHandle(child);
                    var count = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Count;
                    Logger.Info($"托盘探针：  子窗口 {cls} UIA 子树 {count} 个");
                    total += root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Count;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"托盘探针：  子窗口 {cls} 读取失败 {ex.GetType().Name}");
                }

                return true;
            }, IntPtr.Zero);
        }

        Logger.Info($"托盘探针：完成，按钮候选 {total} 个");
        return total;
    }
}
