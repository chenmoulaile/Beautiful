using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Beautiful.App.Services;

/// <summary>
/// 托盘条目的兜底图标（alpha.16，用户反馈"顶栏托盘只有字不是图标"）：
/// 经典路读到的条目带 owner 窗口 → 按进程映像提取它 exe 里的小图标（16px），
/// 这是"这个应用长什么样"的最可靠事实，不再画应用名首字母充当图标。
/// <para>
/// 【边界】UIA 路条目没有 owner 句柄（拿不到 exe）→ 调用方退回中性图形；
/// Store 应用（WindowsApps）路径可能读不到 → 返回 null，同样退回中性图形。
/// 每个映像路径只提取一次并缓存（托盘条目在两次刷新间高度稳定）。
/// </para>
/// </summary>
internal static class TrayIconFallback
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
        System.Text.StringBuilder path, ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string path, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>owner 窗口 → 进程映像小图标。失败返回 null（调用方退中性图形，不画文字）。</summary>
    public static BitmapSource? GetOwnerIcon(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(ownerHwnd, out var pid) == 0 || pid == 0) return null;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        var path = new System.Text.StringBuilder(1024);
        var size = (uint)path.Capacity;
        string image;
        try
        {
            if (!QueryFullProcessImageNameW(process, 0, path, ref size) || size == 0) return null;
            image = path.ToString();
        }
        finally
        {
            CloseHandle(process);
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(image, out var cached)) return cached;
            var icon = Extract(image);
            if (Cache.Count > 128) Cache.Clear();
            Cache[image] = icon;
            return icon;
        }
    }

    private static BitmapSource? Extract(string image)
    {
        try
        {
            if (!File.Exists(image)) return null;
            var small = new IntPtr[1];
            if (ExtractIconExW(image, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(small[0]);
            }
        }
        catch (Exception)
        {
            return null;   // 权限/损坏 exe 等一切异常：如实退中性图形
        }
    }
}
