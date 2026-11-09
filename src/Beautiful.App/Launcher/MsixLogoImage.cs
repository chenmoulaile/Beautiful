using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;

namespace Beautiful.App.Launcher;

/// <summary>
/// MSIX 包内 logo 资产（<see cref="AppxLogoResolver"/> 选出、MsixIndexSource 验存后的图片文件路径）
/// 的解码与缓存。
/// <para>
/// 【为什么不走 IconExtractor】它的降级链以 exe/ico 为对象（①②③⑤ 见其类型注释）：对一个 .png
/// 文件路径，①②只认 PE 图标资源、③只处理 .lnk、④默认关闭，最后落到 ⑤ SHGetFileInfo——那是
/// "PNG 文件类型"的系统图标，拿它当"微软 Family"的图标属于错图标，比色块更糟。这里直接
/// <see cref="BitmapDecoder"/> 解码资产字节（MacIconCatalog/TrayIconManager 的 PngBitmapDecoder
/// 同款先例，BitmapDecoder 是覆盖 jpg 声明值的通用入口），失败返回 null，调用方落回首字母色块。
/// </para>
/// <para>
/// 【缓存】按资产原尺寸解码（targetsize-256；更大的裸资产缩到 256，与 MacIconCatalog 同策略——
/// 一张 1024² BGRA ≈ 4MB 非托管内存，MSIX 条目一多就是白占）；产出一律 Freeze 后跨线程共享。
/// 键是资产全路径——启动台与 Spotlight 以不同尺寸要同一张图，位图只解一次，缩放交给 Image 渲染。
/// null 也进缓存（坏资产不反复重试，与 MacIconCatalog.FirstFor 同一策略）。
/// LruCache 非线程安全：启动台图标泵在后台线程（PumpIconsAsync → Task.Run）、Spotlight 在 UI 线程，
/// 两侧可能并发，锁覆盖"查缓存 + 解码 + 写缓存"整段（与 IconExtractor.Sync 同粒度）。
/// </para>
/// </summary>
internal static class MsixLogoImage
{
    private static readonly object Sync = new();

    /// <summary>256px BGRA ≈ 256KB/张；上限 96 张足够装下本机全部 MSIX 条目（当前约 40 条）并留余量。</summary>
    private static readonly LruCache<string, BitmapSource?> Cache = new(96);

    /// <summary>
    /// 解码一张包内 logo 资产；任何失败（读不到/不是合法位图）都返回 null，绝不抛上 UI。
    /// </summary>
    public static BitmapSource? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Sync)
        {
            if (Cache.TryGet(path, out var cached)) return cached;
            var bitmap = Decode(path);
            Cache.Set(path, bitmap);
            return bitmap;
        }
    }

    /// <summary>缓存整体作废（OOM 自愈用；frozen 位图的像素缓冲在非托管堆，要靠主动清缓存 + GC 收）。</summary>
    public static void ClearCache()
    {
        lock (Sync)
        {
            Cache.Clear();
        }
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            // 裸资产没有尺寸承诺（个别包只放一张大图）：超过 256 缩到 256，图案像素不变
            if (frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                var scale = 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or NotSupportedException or FileFormatException
                                     or InvalidOperationException or ArgumentException)
        {
            return null;   // 资产读不到或不是合法位图 → 交回首字母色块兜底，不猜
        }
    }
}
