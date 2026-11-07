namespace Beautiful.Core.Dock;

/// <summary>
/// 回收站槽位的满/空判定与图标键（纯函数，可单测）。
/// <para>
/// 【两条口径为什么分开】同一个 <c>SHQueryRecycleBin</c> 读数，图标与右键菜单的失败兜底方向相反：
/// <list type="bullet">
/// <item>**图标口径**（<see cref="IsFull"/>）：只在查询<b>成功</b>（hr == 0）时按条目数判满；
/// 失败时调用方保持上一次状态——垃圾桶图标不能因为一次读数失败来回抖，更不能凭空"变满"。</item>
/// <item>**菜单口径**（<see cref="MenuAssumesItems"/>，历史行为）：查询失败（hr ≠ 0）一律当作
/// "有可清"——宁可多给一格能点的"清空回收站"（点了由 shell 自己解释"本来就空"），
/// 也不把真能清空的菜单置灰。</item>
/// </list>
/// 两个口径都吃同一次查询的读数，所以 Dock 图标与右键菜单在查询成功时永远一致。
/// </para>
/// <para>
/// 【键序列绝不空白】满态的候选链先 <see cref="IconKeyFull"/> 后 <see cref="IconKeyEmpty"/>：
/// 图集缺 trash_full.png 时回退空桶，绝不留白（与 MacIconCatalog「查不到就回退」同一纪律）。
/// </para>
/// </summary>
public static class RecycleBinSlot
{
    /// <summary>图集键：空回收站（Assets/TahoeIcons/trash_empty.png，历史恒用键）。</summary>
    public const string IconKeyEmpty = "trash_empty";

    /// <summary>图集键：满回收站（Assets/TahoeIcons/trash_full.png，资产在库、此前无任何代码路径选它）。</summary>
    public const string IconKeyFull = "trash_full";

    /// <summary>
    /// 图标口径判满（查询成功时调用）：条目数 &gt; 0 即满。负数等异常读数按空处理
    /// （启动起步态就是"空"，与历史行为一致）。
    /// </summary>
    public static bool IsFull(long numItems) => numItems > 0;

    /// <summary>
    /// 菜单口径（DockContextMenuBuilder"清空回收站"可用性）：hr ≠ 0（查询失败）保守当作有可清，
    /// 与 alpha.28 之前的既有行为逐位一致——本函数只是把那把尺子收进 Core，让菜单与图标轮询
    /// 用同一份判定、不各写一份 switch。
    /// </summary>
    public static bool MenuAssumesItems(uint hr, long numItems) => hr != 0 || numItems > 0;

    /// <summary>满/空 → 图集键（单键，用于只需一个键的场合；候选链见 <see cref="IconKeyCandidates"/>）。</summary>
    public static string IconKeyFor(bool full) => full ? IconKeyFull : IconKeyEmpty;

    /// <summary>满/空 → 候选键序列（按命中优先级）：满态先 full 后 empty（图集缺 full 回退空桶），
    /// 空态只有 empty。调用方逐个试读，第一张真实存在的 PNG 胜出。</summary>
    public static IReadOnlyList<string> IconKeyCandidates(bool full) => full
        ? [IconKeyFull, IconKeyEmpty]
        : [IconKeyEmpty];
}
