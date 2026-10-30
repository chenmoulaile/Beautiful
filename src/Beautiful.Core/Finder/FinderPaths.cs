using System;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 的 Windows 路径纯字符串助手（Phase 6-a）。不访问文件系统，也不使用
/// <see cref="System.IO.Path"/> 的文化/平台相关分支：输入输出都是展示给用户的原始 path 形态。
/// <para>
/// 根只有两类：盘符根 <c>C:\</c> 与 UNC 共享根 <c>\\server\share</c>。共享根本身不能再向上，
/// 否则会退化成非法的 <c>\\server</c>。尾随分隔符在比较与取父级前统一剥掉（根除外）。
/// </para>
/// </summary>
public static class FinderPaths
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// 取父目录。
    /// </summary>
    /// <param name="path">目录路径。</param>
    /// <returns>
    /// 父目录；根（盘符根/UNC 共享根）、空路径、相对的单段路径没有父目录时返回 <c>null</c>。
    /// </returns>
    public static string? Parent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = TrimTrailingSeparators(path);
        if (trimmed.Length == 0
            || IsDriveRootCore(trimmed)
            || IsUncShareRootCore(trimmed))
        {
            return null;
        }

        var index = trimmed.LastIndexOfAny(Separators);
        if (index < 0)
        {
            return null;
        }

        var parent = TrimTrailingSeparators(trimmed[..index]);
        if (parent.Length == 0 || (parent.Length == 1 && IsSeparator(parent[0])))
        {
            return null;
        }

        // "C:\Users" 的父级是 "C:"，需补回根分隔符；UNC 父级已是完整共享路径。
        return IsDrivePrefix(parent) ? string.Concat(parent, "\\") : parent;
    }

    /// <summary>
    /// 是否为根：盘符根（<c>C:\</c>，允许重复尾随分隔符）或 UNC 共享根
    /// （<c>\\server\share</c>，允许尾随分隔符）。
    /// </summary>
    public static bool IsRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && IsSeparator(path[2]))
        {
            for (var i = 2; i < path.Length; i++)
            {
                if (!IsSeparator(path[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return IsUncShareRootCore(TrimTrailingSeparators(path));
    }

    /// <summary>
    /// 展示名：盘符根显示 <c>C:</c>，UNC 共享根显示共享名，普通路径显示最后一段。
    /// </summary>
    /// <returns>空路径或 null 返回空串。</returns>
    public static string DisplayName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = TrimTrailingSeparators(path);
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (IsDriveRootCore(trimmed))
        {
            return trimmed;
        }

        if (IsUncShareRootCore(trimmed))
        {
            var rest = trimmed[2..];
            var separatorIndex = rest.IndexOfAny(Separators);
            return rest[(separatorIndex + 1)..];
        }

        var index = trimmed.LastIndexOfAny(Separators);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>
    /// 把子项名拼到目录后：目录已有尾随分隔符时不产生重复分隔符，根也能正确拼接。
    /// </summary>
    /// <param name="dir">父目录；空路径按当前相对目录处理。</param>
    /// <param name="name">子项名，不能为 null、空串或纯空白。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null、空串或纯空白。</exception>
    public static string Combine(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("子项名不能为空。", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            return name;
        }

        var trimmed = TrimTrailingSeparators(dir);
        return trimmed.Length == 0 ? name : string.Concat(trimmed, "\\", name);
    }

    private static string TrimTrailingSeparators(string path)
    {
        var end = path.Length;
        while (end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return path[..end];
    }

    private static bool IsDriveRootCore(string trimmed)
        => trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';

    private static bool IsDrivePrefix(string parent)
        => parent.Length == 2 && char.IsAsciiLetter(parent[0]) && parent[1] == ':';

    private static bool IsUncShareRootCore(string path)
    {
        if (path.Length < 2 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
        {
            return false;
        }

        var rest = path[2..];
        var separatorIndex = rest.IndexOfAny(Separators);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var share = rest[(separatorIndex + 1)..];
        return share.Length > 0 && share.IndexOfAny(Separators) < 0;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
