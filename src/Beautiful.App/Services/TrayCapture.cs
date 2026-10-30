using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing.Imaging;
using System.IO;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 托盘真实像素（P4-T8 的"不注入"路线）：直接截取系统通知区的屏幕像素。
/// <para>
/// 【为什么走截屏】探针（<see cref="TrayProbe"/>）实测 Win11 24H2 的托盘在 UIA 里子树为 0，
/// 只读拿不到图标；注入 explorer 又要写别人的进程。截屏是第三条路：不碰 explorer、
/// 不改系统状态，拿到的就是用户眼睛看到的像素。
/// </para>
/// <para>
/// 【代价】截到的是"当时那一眼"：托盘图标自绘动画（如微信闪烁）只能按采样频率更新；
/// 高 DPI 下必须按物理像素截、再按目标 DPI 缩放，否则会糊。
/// </para>
/// </summary>
public static class TrayCapture
{
    /// <summary>通知区窗口类名（Win11 托盘图标容器）。</summary>
    private const string NotifyClass = "TrayNotifyWnd";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int max);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    /// <summary>
    /// 找 TrayNotifyWnd：它是 Shell_TrayWnd 的**子窗口**，FindWindow 只找顶层窗口所以拿不到
    /// （第一次实现就踩了这个坑，日志显示"拿不到 TrayNotifyWnd 矩形"）。
    /// </summary>
    private static IntPtr FindNotifyWindow()
    {
        var shell = NativeMethods.FindWindowClass("Shell_TrayWnd");
        if (shell == IntPtr.Zero) return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumChildWindows(shell, (child, _) =>
        {
            var buffer = new StringBuilder(128);
            if (GetClassNameW(child, buffer, buffer.Capacity) > 0
                && buffer.ToString() == NotifyClass)
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>托盘区当前的屏幕矩形（物理像素）；找不到返回 null。</summary>
    public static Core.Layout.PhysicalRect? NotifyRect()
    {
        var hwnd = FindNotifyWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return new Core.Layout.PhysicalRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>
    /// 截取托盘区为位图。失败返回 null（隐藏/被遮挡/远程桌面下都可能失败）。
    /// 调用方负责 Freeze 与线程（本方法必须在有桌面的会话里调用）。
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? Capture()
    {
        var area = NotifyRect();
        if (area is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            Logger.Warn("托盘截屏：拿不到 TrayNotifyWnd 矩形");
            return null;
        }

        try
        {
            using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                NativeMethods.DeleteObject(handle);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"托盘截屏失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>诊断入口：截一次托盘区并写 PNG，返回写出的路径（失败 null）。</summary>
    public static string? CaptureToFile(string path)
    {
        var source = Capture();
        if (source is null)
        {
            return null;
        }

        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = File.Create(path);
            encoder.Save(stream);
            Logger.Info($"托盘截屏：已保存 {path}（{source.PixelWidth}×{source.PixelHeight}）");
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn($"托盘截屏写文件失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }
}
