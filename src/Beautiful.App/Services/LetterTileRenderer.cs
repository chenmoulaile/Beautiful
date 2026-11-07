using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.Core.Icons;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 StageBarWindow.xaml.cs 同法）：
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace Beautiful.App.Services;

/// <summary>
/// GNOME 式首字母色块的<b>位图渲染</b>（Core <see cref="LetterTile"/> 只出参数，本类画像素）：
/// accent 底圆角方块 + 白色首字母。图标兜底链（macOS 图标集 → 系统图标提取 → 本类）的最后一级，
/// 任何应用都不允许空白格（alpha.29 残项清零：启动台 Microsoft「Family」格空白的收口）。
/// <para>
/// 【线程模型】启动台图标泵在后台线程逐格调用（<c>LauncherService.IconOf</c> → 本类）：
/// DrawingVisual / RenderTargetBitmap 全部在<b>调用线程</b>上创建、使用并 Freeze 后才跨线程——
/// 与 <see cref="MacIconCatalog"/> 后台解 PNG 的既有线程纪律同一条（Freezable 创建时绑当前线程的
/// dispatcher，Freeze 后解除亲和，线程池每线程自建 dispatcher 与现状一致）。
/// </para>
/// <para>
/// 【缓存】键 = 字母|底色|尺寸：同一字母同一主题色同一尺寸只画一次（整个启动台页通常个位数键）。
/// null 也进缓存——画不出来的键不反复重试（与 MacIconCatalog.FirstFor 同一策略）。
/// </para>
/// </summary>
internal static class LetterTileRenderer
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清空缓存（主题/强调色变更后旧色块作废；ThemeManager 换 token 时调用，幂等）。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 渲染一块首字母色块。<paramref name="accentHex"/> 传 null/空白时用 Core 的默认蓝；
    /// 任何异常都只记一行日志并返回 null（调用方退回原"留空格"行为，绝不因装饰性兜底崩掉图标泵）。
    /// </summary>
    public static BitmapSource? Render(string? name, int size, string? accentHex)
    {
        var spec = LetterTile.SpecFor(name, size, accentHex);
        var key = $"{spec.Letter}|{spec.AccentHex}|{spec.Size}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        BitmapSource? bitmap = null;
        try
        {
            bitmap = Draw(spec);
        }
        catch (Exception ex)
        {
            Logger.Warn($"首字母色块渲染失败（留空格）: {ex.GetType().Name} {ex.Message}");
        }
        Cache[key] = bitmap;
        return bitmap;
    }

    private static BitmapSource? Draw(LetterTileSpec spec)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var radius = LetterTile.CornerRadiusRatio * spec.Size;
            dc.DrawRoundedRectangle(AccentBrush(spec.AccentHex), null,
                new Rect(0, 0, spec.Size, spec.Size), radius, radius);
            var text = new FormattedText(
                spec.Letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                spec.Size * 0.46, Brushes.White, 1.0)
            {
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(text, new Point(spec.Size / 2.0, spec.Size / 2.0 - text.Height / 2.0));
        }
        var rtb = new RenderTargetBitmap(spec.Size, spec.Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Core 已把 hex 收敛为 "#AARRGGBB"，这里只做解析；防御性再兜一层默认蓝。</summary>
    private static Brush AccentBrush(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            var fallback = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            fallback.Freeze();
            return fallback;
        }
    }
}
