namespace Beautiful.Core.Dock;

/// <summary>分隔符槽位的语义角色（角色稳定 ⇒ App 侧可复用同一个条目实例，位置变化才增删）。</summary>
public enum DockSeparatorRole
{
    /// <summary>固定应用块与运行窗口块之间。</summary>
    AppsBoundary,

    /// <summary>图标区与废纸篓/回收站之间。</summary>
    TrashBoundary
}

/// <summary>
/// 一个分隔符槽位。
/// </summary>
/// <param name="Index">在**不含分隔符**的语义序列中的插入下标（表示"插在该条目之前"）。</param>
/// <param name="Role">该槽位的语义角色。</param>
public readonly record struct DockSeparatorSlot(int Index, DockSeparatorRole Role);

/// <summary>
/// Dock 行分隔符的插位规则（纯函数，可单测）。
/// <para>
/// macOS 原生 Dock 的结构是「固定应用 | 正在运行的应用 | 最小化窗口/废纸篓」——分隔符只出现在
/// **语义块边界**上，而不是任意两项之间。Linux（GNOME dash）与鸿蒙的原生 dock 是**一条连续列表**，
/// 没有分隔符：主题把 <c>DockSeparatorsEnabled</c> 关掉即可，App 侧不需要任何 <c>if (theme == mac)</c> 分支。
/// </para>
/// </summary>
public static class DockSeparatorPlan
{
    /// <summary>
    /// 该条目是否参与放大场。分隔符固定宽、缩放恒 1，**不进场**：邻居图标长大时它既不会被拉伸，
    /// 也不会作为"可拉伸图标"参与前缀和的推挤（画出间距仍恒等于 Spacing）。
    /// </summary>
    public static bool IsMagnifiable(DockItemKind kind) => kind != DockItemKind.Separator;

    /// <summary>
    /// 该条目是否可交互（悬停高亮 / 左键激活 / 右键菜单 / 预览）。分隔符只是一道细线：
    /// 它占行宽却不占交互位，命中测试必须跳过它（否则右键会挂在细线上弹出无意义菜单）。
    /// 与 <see cref="IsMagnifiable"/> 目前同集合，但语义不同（放大场与命中是两件事），故分开命名。
    /// </summary>
    public static bool IsInteractive(DockItemKind kind) => kind != DockItemKind.Separator;

    /// <summary>
    /// 计算分隔符槽位（升序）。规则：
    /// <list type="number">
    /// <item><description>固定块 → 运行块边界（两侧都非空才插，否则会出现一道悬空细线）；</description></item>
    /// <item><description>回收站之前（下标 ≥1，即其前已有图标项才插）；</description></item>
    /// <item><description>序列首尾不插；媒体胶囊（后缀槽）与其邻居之间永不插——胶囊无会话时宽度归零，
    /// 若把细线留在它前面就会浮在面板末端。</description></item>
    /// </list>
    /// 传入的 <paramref name="kinds"/> 应为**不含分隔符**的语义序列；序列里已有的分隔符会被忽略（双保险）。
    /// </summary>
    public static IReadOnlyList<DockSeparatorSlot> Slots(IReadOnlyList<DockItemKind>? kinds, bool enabled)
    {
        if (!enabled || kinds is null || kinds.Count == 0) return Array.Empty<DockSeparatorSlot>();

        var slots = new List<DockSeparatorSlot>(2);
        var appsDone = false;
        var trashDone = false;
        for (var i = 1; i < kinds.Count; i++)
        {
            var prev = kinds[i - 1];
            var cur = kinds[i];
            if (prev == DockItemKind.Separator || cur == DockItemKind.Separator) continue;
            if (cur == DockItemKind.Media) continue;   // 规则 3：胶囊前不留细线

            // 同一角色只插一道：固定块可能因窗口增删出现多个"固定→运行"相邻对（防御性去重）
            if (!appsDone && cur == DockItemKind.Window && prev == DockItemKind.Pinned)
            {
                appsDone = true;
                slots.Add(new DockSeparatorSlot(i, DockSeparatorRole.AppsBoundary));
            }
            else if (!trashDone && cur == DockItemKind.RecycleBin)
            {
                trashDone = true;
                slots.Add(new DockSeparatorSlot(i, DockSeparatorRole.TrashBoundary));
            }
        }

        slots.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return slots;
    }
}
