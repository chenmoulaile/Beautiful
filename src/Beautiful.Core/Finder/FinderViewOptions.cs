using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>访达的四种视图（Phase 6-b / 6-c）：列表 / 分栏 / 图标 / 画廊。</summary>
public enum FinderViewMode
{
    /// <summary>列表视图（可排序的列，Phase 6-a）。</summary>
    List,

    /// <summary>分栏视图（Miller columns，Phase 6-b）。</summary>
    Columns,

    /// <summary>图标视图（网格 + 框选，Phase 6-b）。</summary>
    Icons,

    /// <summary>画廊视图（大预览 + 底部胶片条，Phase 6-c）。</summary>
    Gallery
}

/// <summary>
/// 单个文件夹的视图选项（Phase 6-b，macOS "每个文件夹记住自己的显示方式"语义）。
/// </summary>
/// <param name="Mode">视图模式。</param>
/// <param name="SortKey">排序列。</param>
/// <param name="Descending">组内是否降序（文件夹恒在前，由 <see cref="FinderSorting"/> 保证）。</param>
/// <param name="ShowHidden">是否显示隐藏/系统文件（⇧⌘.）。</param>
/// <param name="IconSize">图标视图的图标边长（DIP，会钳到合法档位）。</param>
public readonly record struct FinderViewOptions(
    FinderViewMode Mode,
    FinderSortKey SortKey,
    bool Descending,
    bool ShowHidden,
    double IconSize)
{
    /// <summary>出厂默认：列表视图、名称升序、不显示隐藏项、64 DIP 图标。</summary>
    public static FinderViewOptions Default { get; } =
        new(FinderViewMode.List, FinderSortKey.Name, Descending: false, ShowHidden: false, IconSize: 64);
}

/// <summary>
/// 视图选项的归一化与持久化键规则（Phase 6-b 纯函数）。
/// <para>
/// 存储形态是"路径 → 选项"的字典（App 侧落 <c>config/finder-view-options.json</c>，
/// 不往用户目录写 .DS_Store 之类的东西）。本类只负责三件事，全部可单测：
/// 把任意输入钳到合法值域、把路径折成字典键、按容量上限裁剪字典。
/// </para>
/// <para>
/// 键规则：<see cref="System.IO.Path.GetFullPath(string)"/> 之后 <c>TrimEnd('\\','/')</c> 再
/// <c>ToLowerInvariant()</c>。Windows 文件系统大小写不敏感，同一个文件夹被不同大小写访问时必须命中同一条记录；
/// 非法路径退化为原串小写（不抛异常，UI 路径不接受崩溃）。
/// </para>
/// </summary>
public static class FinderViewOptionRules
{
    /// <summary>字典容量上限：超出后丢最早写入的条目（够覆盖常用目录，且不让 json 无限膨胀）。</summary>
    public const int MaxFolders = 200;

    /// <summary>把选项钳到合法值域：未知枚举回落默认，图标尺寸吸附到档位。</summary>
    public static FinderViewOptions Normalize(FinderViewOptions options)
    {
        var mode = Enum.IsDefined(options.Mode) ? options.Mode : FinderViewOptions.Default.Mode;
        var sortKey = Enum.IsDefined(options.SortKey) ? options.SortKey : FinderViewOptions.Default.SortKey;
        var iconSize = FinderIconGrid.SnapIconSize(
            options.IconSize > 0 ? options.IconSize : FinderViewOptions.Default.IconSize);
        return new FinderViewOptions(mode, sortKey, options.Descending, options.ShowHidden, iconSize);
    }

    /// <summary>路径 → 持久化键；null/空白返回空串（调用方据此跳过读写）。</summary>
    public static string KeyOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(path);
            return full.TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or System.IO.PathTooLongException or System.Security.SecurityException)
        {
            return path.TrimEnd('\\', '/').ToLowerInvariant();
        }
    }

    /// <summary>视图模式 → 持久化用的稳定字符串（枚举改名不会污染旧文件）。</summary>
    public static string ModeName(FinderViewMode mode) => mode switch
    {
        FinderViewMode.Columns => "columns",
        FinderViewMode.Icons => "icons",
        FinderViewMode.Gallery => "gallery",
        _ => "list"
    };

    /// <summary>稳定字符串 → 视图模式；无法识别一律回落列表视图。</summary>
    public static FinderViewMode ParseMode(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "columns" or "column" => FinderViewMode.Columns,
        "icons" or "icon" or "grid" => FinderViewMode.Icons,
        "gallery" => FinderViewMode.Gallery,
        _ => FinderViewMode.List
    };

    /// <summary>排序列 → 稳定字符串。</summary>
    public static string SortKeyName(FinderSortKey key) => key switch
    {
        FinderSortKey.Modified => "modified",
        FinderSortKey.Size => "size",
        FinderSortKey.Kind => "kind",
        _ => "name"
    };

    /// <summary>稳定字符串 → 排序列；无法识别回落名称。</summary>
    public static FinderSortKey ParseSortKey(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "modified" or "date" => FinderSortKey.Modified,
        "size" => FinderSortKey.Size,
        "kind" or "type" => FinderSortKey.Kind,
        _ => FinderSortKey.Name
    };

    /// <summary>
    /// 写入一条记录：键已存在则覆盖（保持其在字典中的"年龄"），否则追加；超过
    /// <see cref="MaxFolders"/> 时按插入顺序丢弃最早的键。返回新字典，不修改入参。
    /// </summary>
    public static Dictionary<string, FinderViewOptions> With(
        IReadOnlyDictionary<string, FinderViewOptions>? source,
        string? path,
        FinderViewOptions options)
    {
        var result = new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
        var key = KeyOf(path);
        if (key.Length == 0)
        {
            if (source is not null)
            {
                foreach (var pair in source)
                {
                    result[pair.Key] = Normalize(pair.Value);
                }
            }

            return result;
        }

        if (source is not null)
        {
            foreach (var pair in source)
            {
                if (!string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    result[pair.Key] = Normalize(pair.Value);
                }
            }
        }

        result[key] = Normalize(options);
        if (result.Count <= MaxFolders)
        {
            return result;
        }

        // Dictionary 的枚举顺序≈插入顺序（.NET 实现细节，但同进程内足够稳定）：
        // 丢掉最早的若干条，保留最近写入的 MaxFolders 条。
        var drop = result.Count - MaxFolders;
        var keep = new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
        foreach (var pair in result)
        {
            if (drop > 0)
            {
                drop--;
                continue;
            }

            keep[pair.Key] = pair.Value;
        }

        return keep;
    }

    /// <summary>查一条记录；没有则返回默认选项。</summary>
    public static FinderViewOptions For(
        IReadOnlyDictionary<string, FinderViewOptions>? source,
        string? path)
    {
        var key = KeyOf(path);
        if (source is not null && key.Length > 0 && source.TryGetValue(key, out var options))
        {
            return Normalize(options);
        }

        return FinderViewOptions.Default;
    }
}
