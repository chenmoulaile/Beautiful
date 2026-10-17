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

    /// <summary>
    /// 抓到可用画面返回位图；窗口不可见/尺寸非法/两条路都纯色 → null。
    /// <para>
    /// 两条路：先 <c>PrintWindow(PW_RENDERFULLCONTENT)</c>（窗口内容本尊，连重叠窗口都不带），
    /// 纯色/失败再退到<b>屏幕 DC 抠窗口矩形</b>——DirectComposition 窗口（Chrome/Electron 全家）的
    /// PrintWindow 经常整幅纯色，而"窗口还画着"的场景它必然已在屏幕上合成，屏幕抠图必有像素。
    /// 这正是 <c>genie-minimize-animation</c> mod 的同一条退路；代价是重叠窗口会一起进画面，
    /// 但替身只在"该窗口即将消失"的一瞬显示，可接受。
    /// </para>
    /// </summary>
    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        // 最小化中的窗口矩形是 (-32000,-32000) 那套哨兵值；负/零尺寸直接判不可用
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

        var viaPrintWindow = CaptureViaPrintWindow(hwnd, width, height);
        if (viaPrintWindow is not null) return viaPrintWindow;
        return CaptureViaScreen(rect, width, height);
    }

    private static BitmapSource? CaptureViaPrintWindow(IntPtr hwnd, int width, int height)
    {
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

            if (TryFreeze(memDc, bitmap, width, height, out var source))
            {
                Logger.Info($"窗口抓取：层=PrintWindow 位图={width}×{height}");
                return source;
            }
            return null;   // 纯色：交给屏幕抠图这条退路，别在这里定生死
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException
                                      or InvalidOperationException or ArgumentException)
        {
            Logger.Warn($"窗口抓取失败(PrintWindow): {ex.GetType().Name}");
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

    private static BitmapSource? CaptureViaScreen(RECT rect, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = IntPtr.Zero;
        try
        {
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            previous = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, Srccopy)) return null;
            if (!TryFreeze(memDc, bitmap, width, height, out var source)) return null;
            Logger.Info($"窗口抓取：层=屏幕 位图={width}×{height}（PrintWindow 纯色退路）");
            return source;
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException
                                      or InvalidOperationException or ArgumentException)
        {
            Logger.Warn($"窗口抓取失败(屏幕): {ex.GetType().Name}");
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>取样验内容 → 非 WPF 位图冻结。纯色返回 false，由调用方决定走不走退路。</summary>
    private static bool TryFreeze(IntPtr memDc, IntPtr bitmap, int width, int height, out BitmapSource? source)
    {
        source = null;
        var samples = new List<uint>();
        foreach (var (x, y) in CaptureUsability.SampleGrid(width, height))
            samples.Add(GetPixel(memDc, x, y));
        if (!CaptureUsability.IsUsable(samples)) return false;

        var raw = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        raw.Freeze();   // 动画在渲染线程用，必须可跨线程
        source = raw;
        return true;
    }
}
