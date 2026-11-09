namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 托盘收纳列 applet 的落位规则（纯函数，可单测）。
/// <para>
/// 【为什么下沉 Core】"开关关了还占不占位""挂点在小组件前还是行尾"是 Dock 行结构语义，
/// 与 <see cref="DockSeparatorPlan"/> 同一类问题——写在 UI 里只能靠肉眼验证，做成纯函数后
/// 单测能穷举开关 × 挂点 × 小组件开关的全部组合。
/// </para>
/// <para>
/// 【坐标系】Dock 行尾的语义后缀块固定为「回收站 → 媒体胶囊 →（小组件）」。本类的
/// <see cref="SuffixIndex"/> 返回"插在第 i 项之前"的下标（<c>List&lt;T&gt;.Insert</c> 语义）：
/// <c>2</c> = 媒体胶囊之后、小组件之前（默认——小组件保持"信息块永远在最右端"的 macOS 语义）；
/// <c>3</c> = 小组件之后（行尾）；<c>-1</c> = 不插入（开关关）。小组件关闭时两档挂点都收敛为
/// <c>2</c>（即行尾），不会出现"挂点悬空"。
/// </para>
/// </summary>
public static class DockTrayPlacement
{
    /// <summary>挂点：小组件之前（默认）。</summary>
    public const string PositionWidget = "widget";

    /// <summary>挂点：行尾（小组件之后）。</summary>
    public const string PositionEnd = "end";

    /// <summary>合法挂点标识白名单（小写；AppSettings.KnownDockTrayPositions 与它同源，避免两份名单）。</summary>
    public static IReadOnlyList<string> KnownPositions { get; } = [PositionWidget, PositionEnd];

    /// <summary>
    /// 挂点规范化：null/空白/大小写与首尾空白差异都归一；不在白名单内收敛为默认
    /// <see cref="PositionWidget"/>（配置文件被手改成任意值时自愈，Dock 行结构永不悬空）。
    /// </summary>
    public static string NormalizePosition(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() == PositionEnd ? PositionEnd : PositionWidget;

    /// <summary>
    /// 托盘格在后缀块「回收站(0) → 媒体(1) →（小组件(2)）」中的插入下标；
    /// <paramref name="enabled"/> 为 false 返回 <c>-1</c>（不插入）。
    /// </summary>
    public static int SuffixIndex(bool enabled, string? position, bool widgetEnabled)
        => !enabled ? -1
         : NormalizePosition(position) == PositionEnd && widgetEnabled ? 3
         : 2;
}
