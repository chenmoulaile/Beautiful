namespace Beautiful.Core.Launchpad;

/// <summary>
/// Launchpad 文件夹的纯逻辑：拖两个图标一起成文件夹、拖进已有文件夹、拖出 disslove。
/// <para>
/// 【为什么纯函数】文件夹的"创建/合并/裂解"完全由条目 ID 列表决定，
/// 与屏幕/像素无关 → 单测不用构造任何几何即可穷举边界。
/// </para>
/// <para>
/// 【拖放模型】操作对象是一页 (LaunchpadItem?[])：null 槽位 = 空位。
/// 把 sourceSlot 的条目放到 targetSlot，产出一个新数组（不就地修改）：
/// <list type="bullet">
/// <item><description>source == target / source 为空 → 原样返回（不拷贝）</description></item>
/// <item><description>target 为空 → 移到那儿，原位清空</description></item>
/// <item><description>App + App → target 变成含两者的 Folder，source 清空</description></item>
/// <item><description>App + Folder → App 加进目标文件夹，source 清空</description></item>
/// <item><description>Folder + Folder → 合并成员到一个 Folder，source 清空</description></item>
/// <item><description>Folder + App → 交换位置（macOS 对 Folder↔App 拖放的行为未特化，保守处理）</description></item>
/// </list>
/// </para>
/// </summary>
public static class LaunchpadFolders
{
    /// <summary>
    /// 把两个应用合并为一个新文件夹。
    /// <para>
    /// 文件夹名称默认取第一个成员的 ID（macOS 行为：以"被拖上去的那个"命名）。
    /// 成员顺序 = [target, source]，这样"打开文件夹后第一格就是原 target 图标"这一直观保持。
    /// </para>
    /// </summary>
    /// <param name="targetId">被拖上的图标 ID（作为文件夹名字与成员首位）。</param>
    /// <param name="sourceId">拖上去的图标 ID。</param>
    /// <param name="name">显式指定文件夹名；null 时用 <paramref name="targetId"/>。</param>
    public static LaunchpadItem.Folder Create(string targetId, string sourceId, string? name = null)
        => new(name ?? targetId, new[] { targetId, sourceId });

    /// <summary>
    /// 向已有文件夹添加成员。ID 已存在（忽略大小写）时原样返回 —— 不重复插入。
    /// </summary>
    public static LaunchpadItem.Folder AddMember(LaunchpadItem.Folder folder, string member)
    {
        if (folder.MemberIds.Contains(member, StringComparer.Ordinal)) return folder;
        var list = new List<string>(folder.MemberIds) { member };
        return folder with { MemberIds = list };
    }

    /// <summary>
    /// 合并两个文件夹：成员去重并保持顺序（target 的成员在前，source 的独有成员在后）。
    /// </summary>
    public static LaunchpadItem.Folder Merge(
        LaunchpadItem.Folder target, LaunchpadItem.Folder source, string? name = null)
    {
        var merged = new List<string>(target.MemberIds);
        foreach (var m in source.MemberIds)
            if (!merged.Contains(m, StringComparer.Ordinal))
                merged.Add(m);
        return new(name ?? target.Name, merged);
    }

    /// <summary>
    /// 从文件夹移除成员；<b>解散判据</b>：只剩 1 个成员 → 解散为普通图标，
    /// 0 个（不该发生）→ 解散为以文件夹名命名的空图标（由调用方丢弃）。
    /// </summary>
    /// <returns>
    /// Result：解散后为 <see cref="LaunchpadItem.App"/>（剩下的那个），否则为新的 Folder；
    /// Dissolved 为真表示文件夹已不再是文件夹。
    /// <para>
    /// 若成员不存在，原样返回 <paramref name="folder"/>（引用相同），以便调用方用 <c>Assert.Same</c> 断言。
    /// </para>
    /// </returns>
    public static (LaunchpadItem Result, bool Dissolved) RemoveMember(
        LaunchpadItem.Folder folder, string member)
    {
        var originalCount = folder.MemberIds.Count;
        var remaining = folder.MemberIds
            .Where(m => !m.Equals(member, StringComparison.Ordinal))
            .ToArray();

        // 成员不存在 → 原样返回（引用相同）
        if (remaining.Length == originalCount)
            return (folder, false);

        if (remaining.Length <= 1)
        {
            // 解散：剩 1 个就变成 App，0 个就拿文件夹名当 App
            var id = remaining.Length == 1 ? remaining[0] : folder.Name;
            return (new LaunchpadItem.App(id), true);
        }
        return (folder with { MemberIds = remaining }, false);
    }

    /// <summary>
    /// 在一页内应用一次拖放（纯函数，不就地修改）。
    /// <para>
    /// 详见类型注释的 6 条规则；唯一会返回与输入长度不同数组的情况是"没有" ——
    /// 长度恒等于页宽，空位以 null 表示。
    /// </para>
    /// </summary>
    /// <param name="page">当前页的槽位数组（null = 空位）。</param>
    /// <param name="sourceSlot">被拖走的槽位。</param>
    /// <param name="targetSlot">放下的槽位。</param>
    /// <returns>新页（长度不变）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任意槽位越界。</exception>
    public static LaunchpadItem?[] MergePageOnDrop(
        IReadOnlyList<LaunchpadItem?> page,
        int sourceSlot,
        int targetSlot)
    {
        var n = page.Count;
        if (n == 0) return [];
        if (sourceSlot < 0 || sourceSlot >= n)
            throw new ArgumentOutOfRangeException(nameof(sourceSlot));
        if (targetSlot < 0 || targetSlot >= n)
            throw new ArgumentOutOfRangeException(nameof(targetSlot));

        var source = page[sourceSlot];
        if (source is null || sourceSlot == targetSlot)
            return page.ToArray(); // no-op，无需拷贝

        var target = page[targetSlot];

        // 目标为空：直接搬过去、清空拉走处
        if (target is null)
        {
            var r = page.ToArray();
            r[targetSlot] = source;
            r[sourceSlot] = null;
            return r;
        }

        // App + App → 新文件夹（去重：同一个 ID 拖自己 = no-op）
        if (source is LaunchpadItem.App sApp && target is LaunchpadItem.App tApp)
        {
            if (string.Equals(sApp.Id, tApp.Id, StringComparison.Ordinal))
                return page.ToArray();
            var r = page.ToArray();
            r[targetSlot] = Create(tApp.Id, sApp.Id);
            r[sourceSlot] = null;
            return r;
        }

        // App + Folder → 把 App 加进文件夹
        if (source is LaunchpadItem.App sApp2 && target is LaunchpadItem.Folder tFolder)
        {
            var r = page.ToArray();
            r[targetSlot] = AddMember(tFolder, sApp2.Id);
            r[sourceSlot] = null;
            return r;
        }

        // Folder + Folder → 合并
        if (source is LaunchpadItem.Folder sFolder && target is LaunchpadItem.Folder tFolder2)
        {
            var r = page.ToArray();
            r[targetSlot] = Merge(tFolder2, sFolder);
            r[sourceSlot] = null;
            return r;
        }

        // Folder + App → 交换位置（保守处理）
        {
            var r = page.ToArray();
            r[targetSlot] = source;
            r[sourceSlot] = target;
            return r;
        }
    }
}
