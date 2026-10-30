namespace Beautiful.Core.Launchpad;

/// <summary>
/// 一页启动台的几何：多少列、多少行、图标多大、每格多大。
/// <para>
/// 【为什么要从屏幕反推列数，而不是写死 7×5】macOS 启动台的列数/行数随屏幕尺寸与图标尺寸变化，
/// 屏幕越宽一页放得越多；写死 7×5 在 4K 屏上会让中间空出一大片，在 1366 笔记本上又会挤到换行。
/// 这套规则从 MacDock（C++ 版）的 launchpad_layout 移植：列数从宽度算、行数从高度算，
/// 图标在原尺寸放不下时先缩图标、缩到下限才开始减列。
/// </para>
/// <para>
/// 全部是纯算术，不依赖任何窗口/DPI 类型：调用方把 DIP 尺寸传进来即可，真机与单测走同一条路径。
/// </para>
/// </summary>
public readonly record struct LaunchpadGridFit
{
    /// <summary>每页列数（≥ <see cref="LaunchpadGridRules.MinColumns"/>，≤ <see cref="LaunchpadGridRules.MaxColumns"/>）。</summary>
    public required int Columns { get; init; }

    /// <summary>每页行数（≥1）。</summary>
    public required int Rows { get; init; }

    /// <summary>图标实际边长（DIP），可能小于请求值（窄屏时收缩），不小于 <see cref="LaunchpadGridRules.MinIconDip"/>。</summary>
    public required double IconSize { get; init; }

    /// <summary>每格宽（DIP）= 图标 + 横向间距。</summary>
    public required double CellWidth { get; init; }

    /// <summary>每格高（DIP）= 图标 + 标签带 + 纵向间距。</summary>
    public required double CellHeight { get; init; }

    /// <summary>每页容量 = 列 × 行（分页用它切页）。</summary>
    public int Capacity => Columns * Rows;
}

/// <summary>
/// 启动台网格自适应规则（Phase 5 收尾，规则来自 MacDock 的 launchpad_layout）。
/// <para>
/// 【图标优先、列数其次】用户的直觉是"图标该有多大就多大"，所以宽度够时先保持请求的图标尺寸、
/// 用列数去填满屏幕；只有当一页连 <see cref="MinColumns"/> 列都放不下请求尺寸时，才缩图标。
/// 这与 macOS 的行为一致：图标不会因为应用变多而变小，但会因为屏幕变窄而变小。
/// </para>
/// </summary>
public static class LaunchpadGridRules
{
    /// <summary>最少列数：再窄也要能排成三列，否则一页只能放两三个，翻页会变得荒谬。</summary>
    public const int MinColumns = 3;

    /// <summary>
    /// 最多列数：9 列。MacDock 取 12 是因为它的图标基准 96 DIP；本仓库图标基准 76 DIP，
    /// 12 列在 1920 屏上每格只剩 148 DIP，应用名会被大量截断（真机截图实证）。
    /// 9 列与 macOS 在 1080p/1440p 下的观感一致，也让"图标该多大就多大"这条规则真正生效。
    /// </summary>
    public const int MaxColumns = 9;

    /// <summary>图标下限（DIP）：低于这个尺寸文字标签会盖住图标，宁可减列。</summary>
    public const double MinIconDip = 48;

    /// <summary>标签带高度（DIP）：图标下方留给应用名的垂直空间。</summary>
    public const double LabelBandDip = 22;

    /// <summary>
    /// 由内容区尺寸反推网格。
    /// </summary>
    /// <param name="widthDip">可用宽度（已扣掉两侧留白）。≤0 时退化为最小网格，不抛异常。</param>
    /// <param name="heightDip">可用高度（已扣掉搜索框/页点等固定带）。≤0 时按 1 行。</param>
    /// <param name="iconDip">期望图标边长（DIP）。</param>
    /// <param name="gapDip">格子间距（DIP）；横纵同值，与 macOS 的等距网格一致。</param>
    public static LaunchpadGridFit Fit(
        double widthDip,
        double heightDip,
        double iconDip = 76,
        double gapDip = 26)
    {
        var gap = Sanitize(gapDip, 0);
        var icon = Sanitize(iconDip, MinIconDip);
        var width = Sanitize(widthDip, 0);
        var height = Sanitize(heightDip, 0);

        // 列数：先按"请求图标 + 间距"能放几列算，再夹到 [MinColumns, MaxColumns]。
        var columns = (int)Math.Floor((width + gap) / (icon + gap));
        columns = Math.Clamp(columns, MinColumns, MaxColumns);

        // 每格宽 = 均分可用宽度。窄屏时这一值会小于"图标 + 间距"，于是图标要收缩。
        var cellWidth = width > 0 ? width / columns : icon + gap;

        // 图标能占的最大边长 = 格宽 - 间距；不足下限就按下限（此时列数已经是最小 3，不会再减）。
        var iconFit = Math.Max(MinIconDip, Math.Min(icon, cellWidth - gap));
        iconFit = Math.Min(iconFit, Math.Max(MinIconDip, icon));

        var cellHeight = iconFit + LabelBandDip + gap;
        var rows = (int)Math.Floor(height / cellHeight);
        rows = Math.Max(1, rows);

        return new LaunchpadGridFit
        {
            Columns = columns,
            Rows = rows,
            IconSize = iconFit,
            CellWidth = cellWidth,
            CellHeight = cellHeight
        };
    }

    /// <summary>NaN/无穷/负数一律当回落值处理：几何参数错了也不该把界面炸成空白。</summary>
    private static double Sanitize(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;
}
