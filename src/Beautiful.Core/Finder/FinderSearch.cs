using System;
using System.Collections.Generic;
using Beautiful.Core.Launcher;

namespace Beautiful.Core.Finder;

/// <summary>搜索范围（Phase 6-c）：当前文件夹，或"这台 Mac"（受控递归）。</summary>
public enum FinderSearchScope
{
    /// <summary>只筛当前目录已载入的条目（实时、无 IO）。</summary>
    CurrentFolder,

    /// <summary>从当前目录（或用户主目录）向下递归枚举——受深度/条数/超时三重限制。</summary>
    ThisMac
}

/// <summary>
/// 递归搜索的预算（Phase 6-c）。三个上限缺一不可：
/// 深度防目录环与超深树，条数防结果爆内存，超时防慢盘把 UI 卡在"搜索中"。
/// </summary>
/// <param name="MaxDepth">最大递归深度（含起点为 0）。</param>
/// <param name="MaxResults">结果条数上限。</param>
/// <param name="TimeoutMs">总超时（毫秒）。</param>
public readonly record struct FinderSearchLimits(int MaxDepth, int MaxResults, int TimeoutMs)
{
    /// <summary>默认预算：8 层 / 2000 条 / 8 秒。够覆盖日常找文件，又不会把机械盘拖死。</summary>
    public static FinderSearchLimits Default { get; } = new(8, 2000, 8000);

    /// <summary>把任意输入钳进合法区间（深度 1-32、条数 1-20000、超时 200-60000ms）。</summary>
    public static FinderSearchLimits Normalize(FinderSearchLimits limits)
        => new(
            Math.Clamp(limits.MaxDepth, 1, 32),
            Math.Clamp(limits.MaxResults, 1, 20000),
            Math.Clamp(limits.TimeoutMs, 200, 60000));

    /// <summary>超时换算成 <see cref="TimeSpan"/>。</summary>
    public TimeSpan Timeout => TimeSpan.FromMilliseconds(Normalize(this).TimeoutMs);
}

/// <summary>
/// 搜索匹配纯函数（Phase 6-c）：名称包含（忽略大小写）或拼音首字母匹配。
/// <para>
/// 与启动器共用 <see cref="PinyinInitials"/> 的表：输入 <c>wx</c> 能命中"微信"，
/// 输入 <c>weixin</c> 也能命中（首字母前缀）。空查询视为"全部命中"，由调用方决定是否走这条路径。
/// </para>
/// <para>
/// 匹配是**单向**的：只判断"这个名称是否满足查询"，不做排序打分——排序沿用目录/搜索结果的
/// <see cref="FinderSorting"/>，保持与列表视图一致的视觉预期。
/// </para>
/// </summary>
public static class FinderSearchMatcher
{
    /// <summary>名称是否命中查询。</summary>
    /// <param name="name">待匹配名称（不含路径）。</param>
    /// <param name="query">查询串；空白视为命中。</param>
    public static bool IsMatch(string? name, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = query.Trim();
        if (name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 拼音首字母：先取候选位置，再按"连续匹配"判定（与启动器同一套规则）
        var positions = PinyinInitials.Positions(name);
        return positions.Count > 0 && PinyinInitials.Matches(positions, trimmed, prefixOnly: false);
    }

    /// <summary>批量过滤名称集合（保持输入顺序）。</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string>? names, string? query)
    {
        var result = new List<string>();
        if (names is null)
        {
            return result;
        }

        foreach (var name in names)
        {
            if (IsMatch(name, query))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
