using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;
using Beautiful.Core.Imaging;
using Beautiful.Core.Layout;
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

        var viaNative = CaptureViaNative(hwnd, width, height);
        if (viaNative is not null) return viaNative;
        var viaPrintWindow = CaptureViaPrintWindow(hwnd, width, height);
        if (viaPrintWindow is not null) return viaPrintWindow;
        return CaptureViaScreen(rect, width, height);
    }

    /// <summary>
    /// native 捕获服务（Phase 2）：开启时作为 PrintWindow 之外的又一条路；返回 null 继续走既有 C# 退路。
    /// 当前 native 实现为 PrintWindow→BitBlt（WGC/DXGI 是后续增强），判废逻辑在 native 内完成。
    /// </summary>
    private static BitmapSource? CaptureViaNative(IntPtr hwnd, int width, int height)
    {
        var settings = SettingsService.Current;
        if (!settings.NativeModules || !settings.NativeCapture || !NativeLoader.Available) return null;
        var handle = NativeLoader.CaptureCreate();
        if (handle == IntPtr.Zero) return null;
        try
        {
            var pixels = new byte[width * height * 4];
            var status = NativeLoader.CaptureWindow(handle, hwnd, pixels, out var result);
            if (status == 0 && result.Rejected == 0 && result.Width > 0 && result.Height > 0)
            {
                var source = BitmapSource.Create(result.Width, result.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, result.Stride);
                source.Freeze();
                Logger.Info($"窗口抓取：层=native(mode={result.Mode}) 位图={result.Width}×{result.Height}");
                return source;
            }
            Logger.Info($"窗口抓取：native 未命中（code={status}, rejected={result.Rejected}），继续 C# 退路");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"窗口抓取失败(native): {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            NativeLoader.CaptureDestroy(handle);
        }
    }

    /// <summary>
    /// 抓整个窗口，再按"屏幕坐标下的这一块矩形"抠出来（矩形必须属于那个窗口）。
    /// <para>
    /// 给"窗口在屏幕外但仍在渲染"的场合用：24H2 的任务栏被本程序移出屏幕后，屏幕上没有那一格像素，
    /// 裁屏这条路必然失败。注意 **XAML 岛不吃 PrintWindow**（本机实测：对 Shell_TrayWnd 与岛子窗口
    /// 做 PrintWindow(PW_RENDERFULLCONTENT) 整幅全黑），所以这条只对普通 Win32 表面有效；
    /// 岛里的图标改走"取该应用主程序的图标"（见 TrayIconSource.OwnerIcon）。
    /// </para>
    /// </summary>
    public static BitmapSource? CaptureRegionOfWindow(IntPtr hwnd, PhysicalRect screenRect)
    {
        var full = Capture(hwnd);
        if (full is null) return null;
        return TryCrop(full, hwnd, screenRect, out var cell) ? cell : null;
    }

    /// <summary>
    /// 在<b>已经抓好</b>的整窗位图上抠一格（同一批格子只抓一次窗口，别每格付一次 PrintWindow）。
    /// 几何对不上（越界/尺寸非法）返回 false——宁可画空框，也不错画别人窗口的像素。
    /// </summary>
    public static bool TryCrop(BitmapSource surface, IntPtr originHwnd, PhysicalRect screenRect,
        out BitmapSource? cell)
    {
        cell = null;
        if (!GetWindowRect(originHwnd, out var w)) return false;
        var x = (int)screenRect.Left - w.Left;
        var y = (int)screenRect.Top - w.Top;
        var cw = (int)(screenRect.Right - screenRect.Left);
        var ch = (int)(screenRect.Bottom - screenRect.Top);
        if (cw <= 0 || ch <= 0 || x < 0 || y < 0 || x + cw > surface.PixelWidth || y + ch > surface.PixelHeight)
            return false;
        var cropped = new CroppedBitmap(surface, new System.Windows.Int32Rect(x, y, cw, ch));
        cropped.Freeze();
        cell = cropped;
        return true;
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
