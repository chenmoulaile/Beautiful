namespace Beautiful.Core.Launchpad;

/// <summary>
/// Launchpad 分页纯函数：把扁平图标列表按网格容量切成页面。
/// <para>
/// 【为什么分页也下沉 Core】"第 i 个图标在哪一页、第几个槽"这句判定决定了图标怎么摆；
/// 一旦散在 WPF 里就和"显示第几页"的动画状态混在一起，没法钉住。这里只做最简单的
/// 整除 / 取余：图标数、容量 → 页数 / 页内位置，全称都是可枚举的纯数值关系。
/// </para>
/// <para>
/// 【约定】页号与槽位均从 0；总数为 0 时仍返回 1 页（空页占位，界面据此显示空状态而非"崩"）；
/// 容量 ≤0 的输入被钳到 1，避免除零并保证退化时"一行一列"。
/// </para>
/// </summary>
public static class LaunchpadPagination
{
    /// <summary>把 <paramref name="itemsPerPage"/> 钳到 ≥1：0 容量网格无法分页，宁可当 1。。</summary>
    private static int SafeCapacity(int itemsPerPage)
        => Math.Max(1, itemsPerPage);

    /// <summary>
    /// 总加起来多少页。空图标列表也返回 1 页（空页占位）。
    /// </summary>
    /// <param name="itemCount">图标总数（≤0 当作空处理）。</param>
    /// <param name="itemsPerPage">每页容量。</param>
    public static int PageCount(int itemCount, int itemsPerPage)
    {
        var c = SafeCapacity(itemsPerPage);
        if (itemCount <= 0) return 1;
        return (int)Math.Ceiling((double)itemCount / c);
    }

    /// <summary>
    /// 全局图标索引 → (页号, 页内槽位)。两者皆从 0。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">全局索引为负。</exception>
    public static (int Page, int SlotInPage) PageOf(int globalIndex, int itemsPerPage)
    {
        if (globalIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(globalIndex));
        var c = SafeCapacity(itemsPerPage);
        return (globalIndex / c, globalIndex % c);
    }

    /// <summary>
    /// 第 <paramref name="page"/> 页上的第一个全局索引与图标数。
    /// <para>
    /// 页码超出尾页时返回 <c>(first, 0)</c> —— 槽位已算出但图标数为 0，
    /// 由调用方据此不画任何图标而非抛异常。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">页码为负。</exception>
    public static (int FirstIndex, int Count) SpanOf(int page, int itemCount, int itemsPerPage)
    {
        if (page < 0)
            throw new ArgumentOutOfRangeException(nameof(page));
        var c = SafeCapacity(itemsPerPage);
        var first = page * c;
        if (first >= itemCount) return (first, 0);
        return (first, Math.Min(c, itemCount - first));
    }

    /// <summary>
    /// 图标总数、每页容量 → 所有图标的 (页号, 槽位) 映射。用于一次性生成分页后的坐标映射。
    /// <para>
    /// 返回长度恰好 = max(0, itemCount)；空列表返回空数组。
    /// 与 <see cref="PageOf"/> 逐条调用等价，但少一次越界判断，供需要批量落位的路径调用。
    /// </para>
    /// </summary>
    public static (int Page, int SlotInPage)[] Plan(int itemCount, int itemsPerPage)
    {
        if (itemCount <= 0) return [];
        var c = SafeCapacity(itemsPerPage);
        var result = new (int, int)[itemCount];
        for (var i = 0; i < itemCount; i++)
            result[i] = (i / c, i % c);
        return result;
    }
}
