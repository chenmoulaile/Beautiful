using System;
using System.Collections.Generic;
using System.IO;

namespace Beautiful.Core.Finder;

/// <summary>拖放动作：复制或移动。</summary>
public enum FinderDropAction
{
    Copy,
    Move,
}

/// <summary>
/// 单个拖放项的纯计划结果。
/// </summary>
/// <param name="SourcePath">源路径（调用方原始输入）。</param>
/// <param name="TargetPath">目标完整路径（目标目录 + 最终名称）。</param>
/// <param name="FinalName">避让冲突后的最终名称。</param>
/// <param name="NameChanged">最终名称是否与源名称不同（序数比较）。</param>
public readonly record struct FinderDropItem(
    string SourcePath,
    string TargetPath,
    string FinalName,
    bool NameChanged);

/// <summary>
/// Finder 拖放计划（Phase 6-b 纯函数）。
/// <para>
/// 只计算每个源落到目标目录后的名称，不复制/移动/创建任何文件。同名冲突按来源顺序
/// 分配 <c>(1)</c>、<c>(2)</c>…；源按完整路径去重（不区分大小写），保留首次出现顺序。
/// 复制与移动共用同一套命名规则，<see cref="FinderDropAction"/> 仅表达调用方意图。
/// </para>
/// </summary>
public static class FinderDropPlanner
{
    /// <summary>
    /// 生成拖放计划。
    /// </summary>
    /// <param name="sources">源路径集合；null 视为空集合，空白项与盘符根会被跳过。</param>
    /// <param name="targetDirectory">目标目录，不能为 null、空串或纯空白。</param>
    /// <param name="existingNames">目标目录已存在的名称（不区分大小写），用于避让冲突；null 视为无。</param>
    /// <param name="action">复制或移动；不影响计划内容。</param>
    /// <returns>按来源首次出现顺序排列的计划项。</returns>
    /// <exception cref="ArgumentException"><paramref name="targetDirectory"/> 为空。</exception>
    public static IReadOnlyList<FinderDropItem> Plan(
        IEnumerable<string> sources,
        string targetDirectory,
        IEnumerable<string> existingNames,
        FinderDropAction action)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("目标目录不能为空。", nameof(targetDirectory));
        }

        if (sources is null)
        {
            return Array.Empty<FinderDropItem>();
        }

        // action 只影响执行阶段：复制与移动的目标路径、最终名称完全一致。
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

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FinderDropItem>();

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var key = TryGetDedupeKey(source);
            if (key is null || !seen.Add(key))
            {
                continue;
            }

            var trimmed = source.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || IsDriveRoot(trimmed))
            {
                continue;
            }

            var original = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var finalName = FinderNameRules.Unique(original, taken);
            taken.Add(finalName);

            result.Add(new FinderDropItem(
                source,
                FinderPaths.Combine(targetDirectory, finalName),
                finalName,
                !string.Equals(finalName, original, StringComparison.Ordinal)));
        }

        return result;
    }

    /// <summary>
    /// 去重键：完整路径并去掉尾随分隔符；无效路径返回 <c>null</c>（跳过该项）。
    /// </summary>
    private static string? TryGetDedupeKey(string source)
    {
        try
        {
            var full = Path.GetFullPath(source);
            var trimmed = full.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return full;
            }

            return IsDriveRoot(trimmed) ? string.Concat(trimmed, "\\") : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string trimmed)
        => trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';
}
