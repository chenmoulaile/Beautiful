using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 列表中的一行。只含排序/展示所需的快照字段，不持有文件系统句柄。
/// </summary>
/// <param name="Name">显示名（不含路径）。</param>
/// <param name="IsDirectory">是否为文件夹。</param>
/// <param name="Size">字节数（文件夹通常为 0）。</param>
/// <param name="ModifiedUtc">修改时间（UTC）。</param>
/// <param name="Kind">类型描述，例如 Folder/Document/Image。</param>
/// <param name="IsHidden">是否隐藏/系统项（⇧⌘. 控制是否显示，显示时半透明——macOS 语义）。</param>
public readonly record struct FinderEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc,
    string Kind,
    bool IsHidden = false);

/// <summary>Finder 列表可排序的列。</summary>
public enum FinderSortKey
{
    Name,

    Modified,

    Size,

    Kind
}

/// <summary>
/// Finder 列表排序纯函数（Phase 6-a）。
/// <para>
/// 与 macOS Finder 一致：文件夹恒在文件之前，降序只翻转组内次序；组内按选中列比较，
/// 名称列使用 <see cref="NaturalStringComparer"/>，其余列并列时回落到名称自然序，
/// 完全并列则按原始位置保持稳定。输入列表不会被修改。
/// </para>
/// </summary>
public static class FinderSorting
{
    /// <summary>排序列表。</summary>
    /// <param name="entries">输入快照列表；null 或空返回空列表。</param>
    /// <param name="key">排序列，默认名称。</param>
    /// <param name="descending">是否在"文件夹组/文件组"内部降序。</param>
    /// <returns>新的有序只读列表，不修改 <paramref name="entries"/>。</returns>
    public static IReadOnlyList<FinderEntry> Sort(
        IReadOnlyList<FinderEntry>? entries,
        FinderSortKey key = FinderSortKey.Name,
        bool descending = false)
    {
        if (entries is null || entries.Count == 0)
        {
            return Array.Empty<FinderEntry>();
        }

        var indexed = new (FinderEntry Entry, int Index)[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            indexed[i] = (entries[i], i);
        }

        Array.Sort(indexed, (a, b) => Compare(a, b, key, descending));

        var result = new FinderEntry[entries.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = indexed[i].Entry;
        }

        return result;
    }

    private static int Compare(
        (FinderEntry Entry, int Index) a,
        (FinderEntry Entry, int Index) b,
        FinderSortKey key,
        bool descending)
    {
        // 文件夹恒在前：descending 不翻转这个分组。
        if (a.Entry.IsDirectory != b.Entry.IsDirectory)
        {
            return a.Entry.IsDirectory ? -1 : 1;
        }

        var cmp = key switch
        {
            FinderSortKey.Name => NaturalStringComparer.Instance.Compare(a.Entry.Name, b.Entry.Name),
            FinderSortKey.Modified => a.Entry.ModifiedUtc.CompareTo(b.Entry.ModifiedUtc),
            FinderSortKey.Size => a.Entry.Size.CompareTo(b.Entry.Size),
            FinderSortKey.Kind => string.Compare(a.Entry.Kind, b.Entry.Kind, StringComparison.OrdinalIgnoreCase),
            _ => 0
        };

        if (descending)
        {
            cmp = -cmp;
        }

        if (cmp != 0)
        {
            return cmp;
        }

        // 主键并列：名称自然序；名称也相同则用原始索引保持稳定。
        cmp = NaturalStringComparer.Instance.Compare(a.Entry.Name, b.Entry.Name);
        return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
    }
}
