using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Beautiful.App.Services;

/// <summary>
/// 截图（诊断/证据采集用，<c>--shot &lt;file&gt;</c>）。不是产品功能：
/// 只在命令行显式要求时调用一次，成功后立即关停进程。
/// <para>
/// 两种手法，按可靠性排序：
/// ① <see cref="PrintWindow"/> + <c>PW_RENDERFULLCONTENT</c> 直接让窗口把自己画到我们的 DC——
///    **与窗口是否在前台无关**，所以即使编辑器抢走焦点也能拿到真实内容（本机实测 WPF 窗口可用）；
/// ② GDI <see cref="Graphics.CopyFromScreen(Point, Point, Size)"/> 截屏幕区域——PrintWindow 失败
///    （返回 false 或整图纯黑）时的兜底，要求目标窗口确实在最上层。
/// 失败只返回原因，绝不抛出（诊断工具不该连坐启动链路）。
/// </para>
/// </summary>
internal static class ScreenCapture
{
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>截主屏到 <paramref name="path"/>（PNG，目录不存在会创建）。</summary>
    public static bool TryCapturePrimary(string path, out string error)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return TryCaptureRegion(bounds.Left, bounds.Top, bounds.Width, bounds.Height, path, out error);
    }

    /// <summary>截屏幕上一块物理像素区域到 PNG（要求目标内容确实在屏幕上）。</summary>
    public static bool TryCaptureRegion(int left, int top, int width, int height, string path, out string error)
    {
        try
        {
            if (width <= 0 || height <= 0)
            {
                error = $"区域非法：{width}x{height}";
                return false;
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(new Point(left, top), Point.Empty, new Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }

            return Save(bitmap, path, out error);
        }
        catch (Exception ex) when (IsCaptureFailure(ex))
        {
            error = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 截某个窗口（PrintWindow，不依赖前台状态）；失败或拿到纯黑图时自动回退到屏幕区域。
    /// </summary>
    public static bool TryCaptureWindow(IntPtr hwnd, string path, out string error)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            error = "窗口句柄无效";
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            error = $"窗口尺寸非法：{width}x{height}";
            return false;
        }

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(hwnd, hdc, PwRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }

                if (!ok || IsBlank(bitmap))
                {
                    graphics.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, new Size(width, height),
                        CopyPixelOperation.SourceCopy);
                }
            }

            return Save(bitmap, path, out error);
        }
        catch (Exception ex) when (IsCaptureFailure(ex))
        {
            error = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    private static bool Save(Bitmap bitmap, string path, out string error)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(full, ImageFormat.Png);
        error = string.Empty;
        return true;
    }

    /// <summary>抽样判定"纯黑/全透明"：PrintWindow 在某些合成场景下会成功返回但画布是空的。</summary>
    private static bool IsBlank(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 16);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 8 && (pixel.R > 8 || pixel.G > 8 || pixel.B > 8))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCaptureFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or ExternalException or OutOfMemoryException or InvalidOperationException;
}
