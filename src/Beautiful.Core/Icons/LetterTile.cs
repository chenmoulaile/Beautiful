using System.Buffers;   // OperationStatus（Rune.DecodeFromUtf16 的返回；不在 ImplicitUsings 集内）
using System.Text;

namespace Beautiful.Core.Icons;

/// <summary>
/// GNOME 式首字母色块的<b>参数</b>（纯数据）：accent 底 + 白色首字母 + 圆角 tile。
/// Core 只负责"该画什么字、用什么色、多大"，位图渲染在 App 侧（Core 无 WPF 依赖），
/// 两半由单测（本文件）与运行时目视验收分别钉住。
/// </summary>
/// <param name="Size">目标边长（物理 px，调用方按显示尺寸给，钳制见 <see cref="LetterTile.SpecFor"/>）。</param>
/// <param name="AccentHex">底色（恒为规范化后的 "#AARRGGBB"；非法输入回落 <see cref="LetterTile.DefaultAccentHex"/>）。</param>
/// <param name="Letter">要画的首字母（恒为单字符非空白；无名可取时是 <see cref="LetterTile.FallbackLetter"/>）。</param>
public sealed record LetterTileSpec(int Size, string AccentHex, string Letter);

/// <summary>
/// 启动台/启动器图标兜底链的<b>最后一级</b>（alpha.29 残项清零）：macOS 图标集与系统图标提取
/// 都拿不到图时，绝不留空白格——画一块"accent 底 + 白色首字母"的圆角色块（GNOME桌面图标
/// 缺图时的同款处理）。
/// <para>
/// 【为什么在 Core】"取哪个字母、颜色怎么收敛"是可测契约：同一个应用在任何面板、任何机器上
/// 必须是同一块色块。渲染（DrawingVisual → RenderTargetBitmap）依赖 WPF，留在 App 侧
/// （<c>Beautiful.App.Services.LetterTileRenderer</c>）；本文件全部是纯字符串/数值函数。
/// </para>
/// <para>
/// 【首字母规则】取显示名第一个非空白字符：拉丁字母转大写（GNOME 惯例），CJK 等无大小写的
/// 字符原样保留；代理对（emoji 等）按完整 Rune 取，不画半个替换字符；名字为空取
/// <see cref="FallbackLetter"/>（问号）——色块本身仍然上屏，格子不为空。
/// </para>
/// </summary>
public static class LetterTile
{
    /// <summary>取不到 accent 时的兜底底色（macOS 默认强调色蓝，不透明）。</summary>
    public const string DefaultAccentHex = "#FF0A84FF";

    /// <summary>名字为空/全空白时画的字符。</summary>
    public const char FallbackLetter = '?';

    /// <summary>圆角半径占边长比例：与 macOS 图标遮罩（IconMaskRadiusRatio=0.2）同档观感。</summary>
    public const double CornerRadiusRatio = 0.2;

    /// <summary>尺寸钳制下限（px）：再小画不出可辨的字形。</summary>
    public const int MinSizePx = 8;

    /// <summary>尺寸钳制上限（px）：启动台最大请求约 176，512 已远超任何面板需求。</summary>
    public const int MaxSizePx = 512;

    /// <summary>显示名 → 首字母（单字符，非空白；无名可取时为 <see cref="FallbackLetter"/>）。</summary>
    public static string LetterOf(string? name)
    {
        var text = name ?? string.Empty;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            // 代理对（emoji 等）：用 Rune 取完整码位再转字符串——直接 char.ToString 会画出半个替换符。
            // 孤立代理（高代理后不是低代理）解不出来 → 回落问号，绝不画替换字符。
            if (char.IsSurrogate(text[i]))
            {
                return Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out _) == OperationStatus.Done
                    ? rune.ToString()
                    : FallbackLetter.ToString();
            }
            return char.ToUpperInvariant(text[i]).ToString();
        }
        return FallbackLetter.ToString();
    }

    /// <summary>
    /// 组装一份渲染参数。<paramref name="accentHex"/> 合法（#RRGGBB/#AARRGGBB，'#' 可省）
    /// 时收敛为 "#AARRGGBB"，非法/空白回落 <see cref="DefaultAccentHex"/>；
    /// 尺寸钳到 [<see cref="MinSizePx"/>, <see cref="MaxSizePx"/>]。全部输入都可脏（手改配置/索引脏名）。
    /// </summary>
    public static LetterTileSpec SpecFor(string? name, int size, string? accentHex)
    {
        return new LetterTileSpec(
            Size: Math.Clamp(size, MinSizePx, MaxSizePx),
            AccentHex: CanonicalAccent(accentHex),
            Letter: LetterOf(name));
    }

    /// <summary>hex → "#AARRGGBB"（缺 alpha 补 FF）；非法/空白回落 <see cref="DefaultAccentHex"/>。</summary>
    public static string CanonicalAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultAccentHex;
        var text = hex.Trim();
        if (text[0] == '#') text = text[1..];
        if (text.Length is not (6 or 8)) return DefaultAccentHex;
        foreach (var ch in text)
            if (!Uri.IsHexDigit(ch)) return DefaultAccentHex;
        var value = Convert.ToUInt32(text, 16);
        return text.Length == 6 ? $"#FF{value:X6}" : $"#{value:X8}";
    }
}
