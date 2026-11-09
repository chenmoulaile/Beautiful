using System.Globalization;

namespace Beautiful.Core.Dock;

/// <summary>
/// 单台显示器的 Dock 覆盖配置（对标 MyDockFinder 的每显示器独立设置）。
/// <para>
/// 【null = 跟随全局】可覆盖项：停靠边（<see cref="Edge"/>）、自动隐藏开关
/// （<see cref="AutoHide"/>）、图标尺寸（<see cref="IconSize"/>）、要不要完整 Dock
/// （<see cref="ShowDock"/>）、要不要精简条（<see cref="Slim"/>）。前三个 null 字段回落
/// <c>AppSettings</c> 的全局值，所以单屏机器（列表为空）行为与现状完全一致。
/// </para>
/// <para>
/// 【Dock 在哪块屏不在这里】单实例时代条带落位由全局 <c>DockMonitor</c> 选择器决定；
/// 多实例（每屏一个 DockWindow/SlimDockWindow）后 App 组合根按屏序号逐一钉住，
/// 本列表回答的是"落在这块屏时用哪套边/隐藏/尺寸，以及这块屏放什么表面"。
/// <see cref="Monitor"/> 是这块覆盖配置的**键**（primary 或十进制序号），不是落位选择器——
/// "auto"对单块屏没有定义，规范化收敛为 primary。
/// </para>
/// </summary>
public sealed record DockMonitorProfile
{
    /// <summary>覆盖配置的键：primary（主屏）或 1..16 的十进制序号（枚举序，与设置下拉同源）。</summary>
    public string Monitor { get; init; } = "primary";

    /// <summary>此屏停靠边：null = 跟随全局 DockEdge；否则 bottom/left/right（top 无竖排实现，非法值收敛为 null）。</summary>
    public string? Edge { get; init; }

    /// <summary>此屏自动隐藏开关：null = 跟随全局；true/false 只在 off↔auto 之间扳动全局隐藏模式，其他模式不动。</summary>
    public bool? AutoHide { get; init; }

    /// <summary>此屏图标尺寸：null = 跟随全局 DockIconSize；否则钳制到 32..96。</summary>
    public int? IconSize { get; init; }

    /// <summary>
    /// 此屏要不要**完整 Dock**（悬浮面板 + 放大场 + 媒体胶囊那条）：null = 显示（全局没有
    /// showDock 设置键，"未配置的屏显示完整 Dock"就是多屏化前的现状，零回归）。
    /// false 时该屏改由 <see cref="Slim"/> 决定放精简条还是什么都不放。
    /// </summary>
    public bool? ShowDock { get; init; }

    /// <summary>
    /// 此屏在无完整 Dock（<see cref="ShowDock"/>=false）时是否放一条**精简条**
    /// （轻量图标行 + 点击切窗，无媒体面板/无角标管线）。showDock=true 时本项无效。
    /// </summary>
    public bool? Slim { get; init; }
}

/// <summary>
/// 一块屏上解析出的**生效** Dock 计划（全局回落已完成，App 侧拿来即用，无需再判空）。
/// </summary>
/// <param name="HideMode">生效隐藏模式（DockHideModes 白名单内的串）。</param>
/// <param name="Edge">生效停靠边（bottom/left/right）。</param>
/// <param name="IconSize">生效图标尺寸（32..96）。</param>
/// <param name="ShowDock">生效的"完整 Dock"开关（默认 true = 多屏化前的现状）。</param>
/// <param name="Slim">生效的精简条开关（默认 false；仅 ShowDock=false 时有意义）。</param>
public readonly record struct DockScreenPlan(
    string HideMode, string Edge, int IconSize, bool ShowDock = true, bool Slim = false);

/// <summary>一块屏最终放什么 Dock 表面（App 组合根按它决定建完整 Dock、精简条还是什么都不建）。</summary>
public enum DockSurfaceKind
{
    /// <summary>什么都不放（showDock=false 且 slim=false）。</summary>
    None,
    /// <summary>完整 Dock（现状形态）。</summary>
    Full,
    /// <summary>副屏精简条（showDock=false 且 slim=true）。</summary>
    Slim,
}

/// <summary>
/// 每显示器覆盖配置的规范化与解析（纯函数）。读（AppSettings.Load 后 Normalized）与写
/// （设置窗口 upsert）都走这里，内存态、落盘态与运行态永远同一套规则。
/// </summary>
public static class DockMonitorProfiles
{
    /// <summary>此屏停靠边的合法值（与 DockEdge 实验功能一致：bottom/left/right；top 无竖排实现）。</summary>
    public static readonly string[] KnownEdges = ["bottom", "left", "right"];

    /// <summary>图标尺寸钳制界（与 AppSettings 的全局滑杆同区间）。</summary>
    public const int MinIconSize = 32;
    public const int MaxIconSize = 96;

    /// <summary>序号上限（与 AppSettings.MaxMonitorIndex 同源的一层护栏：手改 JSON 写不出荒谬台数）。</summary>
    public const int MaxMonitorIndex = 16;

    /// <summary>
    /// 单条覆盖配置规范化：<see cref="Monitor"/> 归一为 primary/序号串（auto、空白、非法都收敛 primary）；
    /// <see cref="Edge"/> 非法收敛 null（= 跟随全局）；<see cref="IconSize"/> 钳到 32..96。布尔无需处理。
    /// </summary>
    public static DockMonitorProfile Normalize(DockMonitorProfile? profile)
    {
        if (profile is null) return new DockMonitorProfile();
        return profile with
        {
            Monitor = CanonicalMonitor(profile.Monitor),
            Edge = profile.Edge is null ? null
                : KnownEdges.Contains(profile.Edge.Trim().ToLowerInvariant())
                    ? profile.Edge.Trim().ToLowerInvariant()
                    : null,
            IconSize = profile.IconSize is { } size
                ? Math.Clamp(size, MinIconSize, MaxIconSize)
                : null
        };
    }

    /// <summary>
    /// 解析一块屏的生效计划。<paramref name="monitorIndex"/> 是运行时枚举序的 1 基序号，
    /// <paramref name="isPrimary"/> 标记该屏是否主屏；匹配顺序：**序号精确匹配优先**，其次 primary 键
    /// （仅当该屏真是主屏）。没有匹配 → 全局值原样返回。
    /// <para>
    /// showDock/slim 的"全局值"固定为 true/false（AppSettings 没有对应全局键）：
    /// 未配置覆盖的屏显示完整 Dock，与多屏化前的单实例行为逐字段一致（零回归）。
    /// </para>
    /// </summary>
    public static DockScreenPlan Resolve(
        IReadOnlyList<DockMonitorProfile>? profiles, int monitorIndex, bool isPrimary,
        string globalHideMode, string globalEdge, int globalIconSize,
        bool globalShowDock = true, bool globalSlim = false)
    {
        var match = MatchOrDefault(profiles, monitorIndex, isPrimary);
        return new DockScreenPlan(
            EffectiveHideMode(globalHideMode, match?.AutoHide),
            match?.Edge ?? globalEdge,
            match?.IconSize ?? globalIconSize,
            match?.ShowDock ?? globalShowDock,
            match?.Slim ?? globalSlim);
    }

    /// <summary>
    /// 这块屏最终放什么表面：showDock=true → 完整 Dock（slim 无效）；
    /// showDock=false 且 slim=true → 精简条；否则什么都不放。纯函数，单测覆盖。
    /// </summary>
    public static DockSurfaceKind SurfaceKindOf(bool showDock, bool slim)
        => showDock ? DockSurfaceKind.Full
           : slim ? DockSurfaceKind.Slim
           : DockSurfaceKind.None;

    /// <summary>序号键优先、主屏键其次（仅 isPrimary 时）的匹配；没有返回 null。</summary>
    private static DockMonitorProfile? MatchOrDefault(
        IReadOnlyList<DockMonitorProfile>? profiles, int monitorIndex, bool isPrimary)
    {
        if (profiles is null || profiles.Count == 0) return null;
        var indexKey = Math.Clamp(monitorIndex, 1, MaxMonitorIndex).ToString(CultureInfo.InvariantCulture);
        foreach (var p in profiles)
            if (p is not null && string.Equals(p.Monitor, indexKey, StringComparison.Ordinal))
                return p;
        if (isPrimary)
            foreach (var p in profiles)
                if (p is not null && p.Monitor == "primary")
                    return p;
        return null;
    }

    /// <summary>
    /// 自动隐藏开关如何扳动全局隐藏模式：只对 off↔auto 这对"常显 vs 自动隐藏"有定义——
    /// 全局 off 时此屏开 true → auto；全局 auto 时此屏关 false → off；其余组合（dim/条件档）
    /// 不受这个布尔影响，原样返回全局模式。
    /// </summary>
    public static string EffectiveHideMode(string globalHideMode, bool? autoHideOverride)
        => (autoHideOverride, globalHideMode) switch
        {
            (true, DockHideModes.Off) => DockHideModes.Auto,
            (false, DockHideModes.Auto) => DockHideModes.Off,
            _ => globalHideMode
        };

    /// <summary>监视器键规范化：primary 保留；auto/空白/非法 → primary；序号 1..16 归一为裸十进制串。</summary>
    private static string CanonicalMonitor(string? raw)
    {
        var value = (raw ?? "primary").Trim().ToLowerInvariant();
        if (value == "primary") return "primary";
        if (value == "auto" || value.Length == 0) return "primary";
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 1 && parsed <= MaxMonitorIndex &&
            Math.Abs(parsed - Math.Truncate(parsed)) < double.Epsilon)
            return ((int)parsed).ToString(CultureInfo.InvariantCulture);
        return "primary";
    }
}

/// <summary>
/// 覆盖配置列表的批量规范化（AppSettings.Normalized 的入口）：逐条 <see cref="DockMonitorProfiles.Normalize"/>，
/// 丢弃 null 条目；空列表原样返回空。System.Text.Json 反序列化出的列表可能带 null 字面量，这里一并滤掉。
/// </summary>
public static class DockMonitorProfileList
{
    public static IReadOnlyList<DockMonitorProfile> Normalized(IReadOnlyList<DockMonitorProfile>? profiles)
    {
        if (profiles is null || profiles.Count == 0) return [];
        var list = new List<DockMonitorProfile>(profiles.Count);
        foreach (var p in profiles)
        {
            if (p is null) continue;
            list.Add(DockMonitorProfiles.Normalize(p));
        }
        return list;
    }
}
