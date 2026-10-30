using System;

namespace Beautiful.Core.Finder;

/// <summary>
/// Quick Look 的预览类型（Phase 6-b 起步）。枚举只表达“用哪块 UI 呈现”，不绑定任何 WPF 类型；
/// <see cref="InfoCard"/> 是**有效回退**（未知类型/PDF/读取失败），不是错误状态。
/// </summary>
public enum QuickLookKind
{
    /// <summary>纯文本：只读 TextBox（最多读 64KB）。</summary>
    Text,

    /// <summary>位图：BitmapImage 解码后等比缩放显示。</summary>
    Image,

    /// <summary>音视频：MediaElement 播放。</summary>
    Media,

    /// <summary>PDF：Phase 6-b 尚未接入渲染器，当前显示信息卡 + 明确说明。</summary>
    Pdf,

    /// <summary>信息卡：图标/名称/大小/种类/修改日期；也是所有未知类型与读取失败的兜底。</summary>
    InfoCard
}

/// <summary>
/// 扩展名 → <see cref="QuickLookKind"/> 的纯路由（Phase 6-b 起步）。
/// <para>
/// 只做字符串判定，不访问文件系统、不判断文件是否存在：Quick Look 的类型来自名字，
/// 内容是否可读由 App 侧的预览窗口在读取失败时回退信息卡。扩展名统一
/// <see cref="Path.GetExtension(string)"/> 取、<c>ToLowerInvariant</c> 归一，因此大小写不敏感。
/// </para>
/// <para>
/// 非法路径串（<see cref="ArgumentException"/>/<see cref="NotSupportedException"/>）与 null/空白
/// 一律回退 <see cref="QuickLookKind.InfoCard"/>，绝不把异常抛给键盘事件路径。
/// </para>
/// </summary>
public static class QuickLookRouter
{
    /// <summary>把文件名/路径路由到预览类型。</summary>
    /// <param name="path">文件名或完整路径；null/空白返回 <see cref="QuickLookKind.InfoCard"/>。</param>
    public static QuickLookKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return QuickLookKind.InfoCard;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return QuickLookKind.InfoCard;
        }

        return extension switch
        {
            ".txt" or ".md" or ".markdown" or ".log" or ".json" or ".xml" or ".yaml" or ".yml"
                or ".ini" or ".cfg" or ".cs" or ".cpp" or ".h" or ".hpp" or ".c" or ".js"
                or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".go" or ".rs" or ".sh"
                or ".ps1" or ".bat" or ".cmd" or ".sql" or ".html" or ".htm" or ".css" or ".csv"
                => QuickLookKind.Text,

            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico"
                => QuickLookKind.Image,

            ".mp4" or ".m4v" or ".mov" or ".webm" or ".mp3" or ".wav" or ".m4a"
                => QuickLookKind.Media,

            ".pdf" => QuickLookKind.Pdf,

            _ => QuickLookKind.InfoCard
        };
    }

    /// <summary>
    /// 是否为“真实内容预览”（Text/Image/Media/Pdf）。<see cref="QuickLookKind.InfoCard"/>
    /// 只是元数据卡，不算内容预览——UI 侧据此决定是走内容路径还是信息卡路径。
    /// </summary>
    public static bool IsContentPreview(QuickLookKind kind)
        => kind is QuickLookKind.Text or QuickLookKind.Image or QuickLookKind.Media or QuickLookKind.Pdf;
}
