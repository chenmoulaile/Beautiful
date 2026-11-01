using System.Windows.Media.Imaging;
using Beautiful.Core.Animation;

namespace Beautiful.App.Animation;

/// <summary>
/// WPF 侧的替身帧整理：把抓回来的帧（可能带 alpha 洞）合成到实心底，
/// 再交给 genie 引擎。<see cref="GenieFrames"/> 的纯数学在 Core 可单测，这里只做位图搬运。
/// <para>
/// 【为什么每一帧都要过这里】"最小化动画的窗口太过透明"（用户实测原话）的根因不是替身窗口的
/// Opacity，而是 PrintWindow 帧本身大片 alpha&lt;255——分层窗口的自绘内容、浏览器的 GPU 面、
/// UWP 的组合面都这样。在帧进引擎前统一补齐，三种引擎（mesh/strips/zoom）一次治好。
/// </para>
/// </summary>
internal static class GenieFrameSource
{
    /// <summary>合成耗时随帧尺寸线性；超过这个尺寸就跳过逐像素判定直接试合成（判定本身也是 O(n)）。</summary>
    public static BitmapSource? MakeOpaque(BitmapSource? source)
    {
        try
        {
            if (source is null) return null;
            if (source.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
            }

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);

            if (!GenieFrames.HasTranslucency(pixels)) return source;   // 本来就实心：零拷贝直接用

            GenieFrames.CompositeOverOpaque(pixels, width, height);
            var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            // 整理失败就按原帧放：透明是视觉问题，丢帧才是功能问题
            return source;
        }
    }
}
