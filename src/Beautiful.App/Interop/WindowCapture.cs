using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;
using Beautiful.Core.Imaging;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Interop;

/// <summary>
/// 把一个普通窗口当前的画面抓成 <see cref="BitmapSource"/>，供最小化/最大化动画当"替身"用。
/// <para>
/// 用 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 而不是 DWM 缩略图：缩略图只能被 DWM 合成到
/// <b>另一个窗口的玻璃表面</b>上（见 <c>Preview/ThumbnailPreview.cs</c> 那条豁免），拿不到像素，
/// 而动画需要逐条带地画自己的位图。代价是 GPU/DirectComposition 窗口可能整幅抓成纯色，
/// 所以出口一律过 <see cref="CaptureUsability"/>——纯色就当没抓到，宁可不动画。
/// </para>
/// </summary>
internal static class WindowCapture
{
    /// <summary>PW_RENDERFULLCONTENT（Win 8.1+）：让 DirectComposition 内容也参与重绘。</summary>
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>抓到可用画面返回位图；窗口不可见/尺寸非法/PrintWindow 失败/整幅纯色 → null。</summary>
    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        // 最小化中的窗口矩形是 (-32000,-32000) 那套哨兵值；负/零尺寸直接判不可用
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        var previous = IntPtr.Zero;
        try
        {
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            previous = SelectObject(memDc, bitmap);
            if (!PrintWindow(hwnd, memDc, PwRenderFullContent)) return null;

            // 先验内容再拷像素：纯色抓取没必要走一遍 WPF 位图转换
            var samples = new List<uint>();
            foreach (var (x, y) in CaptureUsability.SampleGrid(width, height))
                samples.Add(GetPixel(memDc, x, y));
            if (!CaptureUsability.IsUsable(samples)) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();   // 动画在渲染线程用，必须可跨线程
            return source;
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException
                                      or InvalidOperationException or ArgumentException)
        {
            Logger.Warn($"窗口抓取失败: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(hwnd, windowDc);
        }
    }
}
