using System;
using System.Collections.Generic;
using System.Globalization;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 文件名校验与唯一命名规则（Phase 6-b 纯函数）。
/// <para>
/// 规则对齐 Windows 资源管理器：非法字符与 0-31 控制字符、尾随空格/句点、CON/PRN/AUX/NUL/
/// COM1-9/LPT1-9 保留名、最长 255 个字符。冲突命名采用“主名 (n)扩展名”形式，
/// 点文件（如 <c>.gitignore</c>）没有扩展名，序号直接追加在末尾。
/// </para>
/// </summary>
public static class FinderNameRules
{
    private const int MaxLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 校验单个文件/文件夹名。
    /// </summary>
    /// <param name="name">待校验名称（不含路径分隔符之外的目录部分）。</param>
    /// <returns>合法返回 <c>null</c>；否则返回简短中文原因。</returns>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "名称不能为空";
        }

        if (name is "." or "..")
        {
            return "名称不能是 . 或 ..";
        }

        foreach (var c in name)
        {
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < 32)
            {
                return "名称包含非法字符";
            }
        }

        if (name[^1] is ' ' or '.')
        {
            return "名称不能以空格或句点结尾";
        }

        if (IsReservedName(name))
        {
            return "名称是 Windows 保留名";
        }

        if (name.Length > MaxLength)
        {
            return "名称过长";
        }

        return null;
    }

    /// <summary>
    /// 在目标目录已有名称中为 <paramref name="desired"/> 生成不冲突的名称。
    /// </summary>
    /// <param name="desired">期望名称，不能为 null、空串或纯空白。</param>
    /// <param name="existingNames">目标目录中已存在的名称（不区分大小写）。</param>
    /// <param name="suffixFormat">序号后缀格式，<c>{0}</c> 为主名、<c>{1}</c> 为序号。</param>
    /// <returns>不冲突的名称；无冲突时原样返回 <paramref name="desired"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="desired"/> 为空。</exception>
    /// <exception cref="InvalidOperationException">1 到 9999 的序号全部被占用。</exception>
    public static string Unique(
        string desired,
        IEnumerable<string> existingNames,
        string suffixFormat = "{0} ({1})")
    {
        // 不用 ArgumentNullException：调用方按 ArgumentException 统一处理名称错误。
        if (string.IsNullOrWhiteSpace(desired))
        {
            throw new ArgumentException("名称不能为空。", nameof(desired));
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingNames is not null)
        {
            foreach (var existing in existingNames)
            {
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    taken.Add(existing);
                }
            }
        }

        if (!taken.Contains(desired))
        {
            return desired;
        }

        var (stem, extension) = SplitExtension(desired);
        for (var n = 1; n <= 9999; n++)
        {
            var candidate = string.Concat(
                string.Format(CultureInfo.InvariantCulture, suffixFormat, stem, n),
                extension);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("无法生成唯一名称：序号已用尽。");
    }

    /// <summary>
    /// macOS 风格副本名：在最后一个扩展名前插入 <c> copy</c>。
    /// </summary>
    /// <param name="name">原名称，不能为 null、空串或纯空白。</param>
    /// <returns>如 <c>report.docx</c> → <c>report copy.docx</c>、<c>.gitignore</c> → <c>.gitignore copy</c>。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空。</exception>
    public static string CopyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名称不能为空。", nameof(name));
        }

        var (stem, extension) = SplitExtension(name);
        return string.Concat(stem, " copy", extension);
    }

    /// <summary>
    /// 是否为 Windows 保留名：取第一个句点前的基础名，不区分大小写。
    /// </summary>
    /// <param name="name">待判定名称；null、空串或纯空白返回 <c>false</c>。</param>
    public static bool IsReservedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var dot = name.IndexOf('.');
        var stem = dot >= 0 ? name[..dot] : name;
        return stem.Length > 0 && ReservedNames.Contains(stem);
    }

    /// <summary>
    /// 拆分“主名 + 扩展名”：最后一个句点位于首字符或末字符时视为无扩展名
    /// （点文件与尾随句点都不会被拆成空主名/空扩展名）。
    /// </summary>
    private static (string Stem, string Extension) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1
            ? (name[..dot], name[dot..])
            : (name, string.Empty);
    }
}
