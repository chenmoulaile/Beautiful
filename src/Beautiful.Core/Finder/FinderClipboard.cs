using System;
using System.Collections.Generic;
using System.IO;

namespace Beautiful.Core.Finder;

/// <summary>剪贴板语义：复制（保留源）或剪切（成功后删除源）。</summary>
public enum FinderClipboardMode
{
    Copy,
    Cut
}

/// <summary>
/// 访达剪贴板快照（Phase 6-c）。只存路径与意图，不碰文件系统、不持有 COM 对象。
/// <para>
/// 去重与空项过滤在 <see cref="Of"/> 里一次做完：同一路径被重复放入（多选 + 拖拽叠加）
/// 只保留首次出现，顺序保持用户选择的顺序——粘贴时的命名避让顺序依赖它。
/// </para>
/// </summary>
/// <param name="Paths">源路径（已去重、已去空）。</param>
/// <param name="Mode">复制或剪切。</param>
public sealed record FinderClipboard(IReadOnlyList<string> Paths, FinderClipboardMode Mode)
{
    /// <summary>空剪贴板（粘贴时无操作）。</summary>
    public static FinderClipboard Empty { get; } = new(Array.Empty<string>(), FinderClipboardMode.Copy);

    /// <summary>是否没有可粘贴的内容。</summary>
    public bool IsEmpty => Paths.Count == 0;

    /// <summary>构造剪贴板：过滤空白项、按路径去重（不区分大小写、忽略尾随分隔符）、保持顺序。</summary>
    public static FinderClipboard Of(IEnumerable<string>? paths, FinderClipboardMode mode)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var key = path.TrimEnd('\\', '/');
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            result.Add(path);
        }

        return new FinderClipboard(result, mode);
    }
}

/// <summary>单个粘贴项的处理结果分类。</summary>
public enum FinderPasteStatus
{
    /// <summary>可以执行。</summary>
    Ready,

    /// <summary>剪切后粘回原目录：macOS 语义是"什么都不做"。</summary>
    SameDirectory,

    /// <summary>把文件夹粘进它自己/它的子孙目录。</summary>
    IntoItself,

    /// <summary>源已不存在（枚举后被删除/移动）。</summary>
    Missing
}

/// <summary>粘贴计划中的一项。</summary>
/// <param name="SourcePath">源路径（调用方原始输入）。</param>
/// <param name="TargetPath">目标完整路径（目标目录 + <paramref name="FinalName"/>）。</param>
/// <param name="FinalName">避让冲突后的最终名称。</param>
/// <param name="Renamed">最终名称是否与源名称不同。</param>
/// <param name="Status">是否可执行；非 <see cref="FinderPasteStatus.Ready"/> 的项必须被跳过。</param>
public readonly record struct FinderPasteItem(
    string SourcePath,
    string TargetPath,
    string FinalName,
    bool Renamed,
    FinderPasteStatus Status)
{
    /// <summary>是否应当真正执行。</summary>
    public bool WillExecute => Status == FinderPasteStatus.Ready;
}

/// <summary>
/// 粘贴计划纯函数（Phase 6-c）。
/// <para>
/// 与 <see cref="FinderDropPlanner"/> 的区别是"同目录"语义：拖放到同一目录等于什么都没做，
/// 而**复制后粘到同一目录**在 macOS 上会生成 <c>name copy.ext</c>——这正是本类存在的理由，
/// 也是它必须复用 <see cref="FinderNameRules.CopyName"/> 而不是自己拼字符串的原因。
/// </para>
/// <para>
/// 三类必须跳过的输入在这里一次判掉，执行层不必再猜：剪切回原目录（<see cref="FinderPasteStatus.SameDirectory"/>）、
/// 文件夹粘进自身子孙（<see cref="FinderPasteStatus.IntoItself"/>）、源已消失（<see cref="FinderPasteStatus.Missing"/>）。
/// 判定所需的文件系统事实全部通过委托注入，本类自身不做 IO，因此可完整单测。
/// </para>
/// </summary>
public static class FinderPastePlanner
{
    /// <summary>
    /// 生成粘贴计划。
    /// </summary>
    /// <param name="clipboard">剪贴板快照；null 或空返回空计划。</param>
    /// <param name="targetDirectory">目标目录，不能为 null、空串或纯空白。</param>
    /// <param name="existingNames">目标目录已存在的名称（不区分大小写）；null 视为空目录。</param>
    /// <param name="exists">源是否仍存在；null 表示不检查（视为存在）。</param>
    /// <param name="isDirectory">源是否为文件夹；null 表示一律按文件处理（不做"粘进自身"判定）。</param>
    /// <returns>按剪贴板顺序排列的计划项，含不可执行的项（调用方据 <see cref="FinderPasteItem.WillExecute"/> 过滤）。</returns>
    /// <exception cref="ArgumentException"><paramref name="targetDirectory"/> 为空。</exception>
    public static IReadOnlyList<FinderPasteItem> Plan(
        FinderClipboard? clipboard,
        string targetDirectory,
        IEnumerable<string>? existingNames = null,
        Func<string, bool>? exists = null,
        Func<string, bool>? isDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("目标目录不能为空。", nameof(targetDirectory));
        }

        if (clipboard is null || clipboard.IsEmpty)
        {
            return Array.Empty<FinderPasteItem>();
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingNames is not null)
        {
            foreach (var name in existingNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    taken.Add(name);
                }
            }
        }

        var targetKey = targetDirectory.TrimEnd('\\', '/');
        var result = new List<FinderPasteItem>(clipboard.Paths.Count);

        foreach (var source in clipboard.Paths)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var trimmed = source.TrimEnd('\\', '/');
            var original = SafeFileName(trimmed);
            if (original.Length == 0)
            {
                continue;   // 盘符根等没有名字的项：不可粘贴
            }

            if (exists is not null && !exists(source))
            {
                result.Add(new FinderPasteItem(source, string.Empty, original, false, FinderPasteStatus.Missing));
                continue;
            }

            var sourceParent = SafeParent(trimmed);
            var sameDirectory = sourceParent is not null
                && string.Equals(sourceParent.TrimEnd('\\', '/'), targetKey, StringComparison.OrdinalIgnoreCase);

            if (sameDirectory && clipboard.Mode == FinderClipboardMode.Cut)
            {
                result.Add(new FinderPasteItem(source, string.Empty, original, false, FinderPasteStatus.SameDirectory));
                continue;
            }

            if (isDirectory is not null && isDirectory(source)
                && FinderPathSafety.IsSameOrDescendant(targetDirectory, source))
            {
                result.Add(new FinderPasteItem(source, string.Empty, original, false, FinderPasteStatus.IntoItself));
                continue;
            }

            // 复制到同一目录：macOS 生成 "name copy.ext"；其他情况保留原名，冲突时再走 (n) 避让。
            var desired = sameDirectory ? FinderNameRules.CopyName(original) : original;
            var finalName = FinderNameRules.Unique(desired, taken);
            taken.Add(finalName);

            result.Add(new FinderPasteItem(
                source,
                FinderPaths.Combine(targetDirectory, finalName),
                finalName,
                !string.Equals(finalName, original, StringComparison.Ordinal),
                FinderPasteStatus.Ready));
        }

        return result;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string? SafeParent(string path)
    {
        try
        {
            return FinderPaths.Parent(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
