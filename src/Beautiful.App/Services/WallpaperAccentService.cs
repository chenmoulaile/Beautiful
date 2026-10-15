using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Themes;
using Beautiful.Core.Imaging;
using static Beautiful.App.Interop.NativeMethods;
using Color = System.Windows.Media.Color;   // UseWindowsForms 隐式 using 与 System.Drawing.Color 冲突

namespace Beautiful.App.Services;

/// <summary>
/// 壁纸取色（v3 T7）：读当前桌面壁纸 → <see cref="DominantColor"/> 取强调色 → 覆盖主题的 <c>ThemeAccent</c>。
/// <para>
/// 【为什么用 SystemParametersInfo 而不是 IDesktopWallpaper】两条路仓库都还没有现成互操作，
/// 但前者只需一个 user32 导入 + 一个 MAX_PATH 缓冲（<c>NativeMethods</c> 里就是这个形态），
/// 后者要新增 COM 接口、CLSID 与 shell 对象绑定 —— 而本仓库已经有一条在本机证伪过的 shell COM 路径
/// （IShellItemImageFactory，见 IconExtractor ④ 层：AV 抓不住）。能用纯 Win32 读到的东西，
/// 就不去新增一条 COM 崩溃面。代价是读不到"幻灯片"模式下的当前那张（该 API 只给 WPP 路径或空串），
/// 这一条被如实当成"不可用"处理：置灰 + 回落主题色。
/// </para>
/// <para>
/// 【回落而不是猜】任何一步失败（路径为空、WPP 幻灯片、文件不存在、解码失败、取色判为无彩度）
/// 都<b>撤掉覆盖</b>让强调色回到主题值，并把中文原因写进日志。绝不做"取不到就画黑/画白"这种降级：
/// 强调色要压在深色玻璃卡片上，黑白两个极端都会让选中行看不见。
/// </para>
/// <para>
/// 【线程】读路径（一次 Win32 调用）在 UI 线程；解码 + 取色在后台线程（4K 图解码是毫秒到几十毫秒级，
/// 不该出现在开关的一跳上）；结果回 UI 线程落资源键。面板开关路径与启动路径共用 <see cref="RefreshAsync"/>。
/// </para>
/// </summary>
public static class WallpaperAccentService
{
    /// <summary>解码上限（像素宽）：取色只需要 DominantColor 的几百个探针，256 宽已远超需要。</summary>
    private const int DecodeWidth = 256;

    /// <summary>
    /// 当前是否"读得到一张具体的壁纸文件"。控制中心的"壁纸取色"砖块据此置灰（本项目：没有能力支撑的
    /// 控件必须灰并写明原因，不留死开关）。只做存在性判定，不做解码——砖块投影在面板构造期就要拿到。
    /// </summary>
    public static bool IsWallpaperReadable() => TryGetWallpaperPath(out var path) && File.Exists(path);

    /// <summary>
    /// 按设置重放一次（启动、切主题之外的第三条路径：用户在设置页/控制中心刚打开这个开关时）。
    /// 关闭时不读磁盘、不解码，直接撤覆盖——"关掉以后画面立刻回到主题原色"就是这条路径的验收点。
    /// </summary>
    public static async Task RefreshAsync(bool enabled)
    {
        if (!enabled)
        {
            ThemeManager.SetWallpaperAccent(null);
            Logger.Info("壁纸取色：开关已关，强调色回到主题值");
            return;
        }
        if (!TryGetWallpaperPath(out var path))
        {
            ThemeManager.SetWallpaperAccent(null);
            Logger.Warn("壁纸取色：读不到当前壁纸路径（纯色桌面或壁纸幻灯片）→ 沿用主题强调色");
            return;
        }
        var color = await Task.Run(() => ExtractFrom(path));
        if (color is null)
        {
            ThemeManager.SetWallpaperAccent(null);
            return;   // 原因已由 ExtractFrom 落日志；这里只负责"不画"
        }
        ThemeManager.SetWallpaperAccent(color);
        Logger.Info($"壁纸取色：{path} → #{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}");
    }

    /// <summary>读系统当前的壁纸路径。空串/纯空白都算"没有可读的壁纸文件"（幻灯片与纯色桌面正是如此）。</summary>
    public static bool TryGetWallpaperPath(out string path)
    {
        path = string.Empty;
        try
        {
            // SPI_GETDESKWALLPAPER 要求缓冲区**先被填满**（长度同时经 uiParam 传出），
            // 否则返回 FALSE —— 这里按文档给 MaxPath 个空格，而不是赌 StringBuilder 的自动增长。
            var buffer = new StringBuilder(MaxPath, MaxPath);
            buffer.Length = MaxPath;
            if (!SystemParametersInfo(SpiGetDeskWallpaper, MaxPath, buffer, 0)) return false;
            path = buffer.ToString().Trim();
            return path.Length > 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"壁纸取色：SystemParametersInfo 读取失败: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 解码 + 取色（后台线程）。失败一律回 null 并留一行原因日志：调用方只需要"有没有颜色"，
    /// 不需要知道是哪一步没成，把原因留在日志里而不是界面上，正是为了界面永远只说事实。
    /// </summary>
    private static Color? ExtractFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Logger.Warn($"壁纸取色：壁纸文件不存在（可能被移动或权限受限）: {path}");
                return null;
            }
            // BitmapImage + DecodePixelWidth 让解码器直接给出小图（不是解完 4K 再缩），
            // 再统一转成 Bgra32 交给 Core 的纯函数——Core 侧只认字节，不认 WPF 类型。
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 句柄当场读完，不长期占用壁纸文件
            image.EndInit();
            image.Freeze();
            var frame = BitmapFrame.Create(image);   // image 已 OnLoad+Freeze，帧直接取它的第一帧
            var scaled = new TransformedBitmap(frame, new ScaleTransform(
                (double)DecodeWidth / Math.Max(1, frame.PixelWidth),
                (double)DecodeWidth / Math.Max(1, frame.PixelWidth)));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);

            if (!DominantColor.TryExtract(buffer, width, height, out var rgb))
            {
                Logger.Warn($"壁纸取色：{Path.GetFileName(path)} 无有效色相或数据不足（灰阶/纯色桌面）→ 沿用主题强调色");
                return null;
            }
            return Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }
        catch (Exception ex)
        {
            Logger.Warn($"壁纸取色失败（沿用主题强调色）: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }
}
