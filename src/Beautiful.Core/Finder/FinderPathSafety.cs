using System;
using System.IO;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 路径安全判定（Phase 6-b 纯函数）。
/// <para>
/// 路径只做 <see cref="Path.GetFullPath(string)"/> 规范化，不检查是否存在、不访问文件系统。
/// 比较不区分大小写（Windows 语义），并先剥掉尾随分隔符；盘符根 <c>C:\</c> 与 UNC 共享根
/// <c>\\server\share</c> 都按“祖先 + 分隔符”判后代，避免 <c>C:\A</c> 误判 <c>C:\AB</c>。
/// </para>
/// </summary>
public static class FinderPathSafety
{
    /// <summary>
    /// <paramref name="candidate"/> 是否与 <paramref name="ancestor"/> 相同或是其后代。
    /// </summary>
    /// <returns>路径无效（null、空白或 <see cref="Path.GetFullPath(string)"/> 抛错）时返回 <c>false</c>。</returns>
    public static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        var normalizedCandidate = TryNormalize(candidate);
        var normalizedAncestor = TryNormalize(ancestor);
        return normalizedCandidate is not null
            && normalizedAncestor is not null
            && IsSameOrDescendantCore(normalizedCandidate, normalizedAncestor);
    }

    /// <summary>
    /// 是否可以把源（文件或目录）移入目标目录。
    /// </summary>
    /// <param name="sourcePath">源路径。</param>
    /// <param name="sourceIsDirectory">源是否为目录。</param>
    /// <param name="targetDirectory">目标目录。</param>
    /// <returns>
    /// 文件恒为 <c>true</c>；目录在目标是自身或其后代时为 <c>false</c>；任一无效路径为 <c>false</c>。
    /// </returns>
    public static bool CanMoveInto(string sourcePath, bool sourceIsDirectory, string targetDirectory)
    {
        var normalizedSource = TryNormalize(sourcePath);
        var normalizedTarget = TryNormalize(targetDirectory);
        if (normalizedSource is null || normalizedTarget is null)
        {
            return false;
        }

        return !sourceIsDirectory || !IsSameOrDescendantCore(normalizedTarget, normalizedSource);
    }

    private static bool IsSameOrDescendantCore(string candidate, string ancestor)
    {
        if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = IsSeparator(ancestor[^1])
            ? ancestor
            : string.Concat(ancestor, Path.DirectorySeparatorChar);
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return full;
            }

            // 盘符根 "C:\" 去掉分隔符后是 "C:"，补回分隔符才能做前缀比较。
            return trimmed.Length == 2
                && char.IsAsciiLetter(trimmed[0])
                && trimmed[1] == ':'
                ? string.Concat(trimmed, Path.DirectorySeparatorChar)
                : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
