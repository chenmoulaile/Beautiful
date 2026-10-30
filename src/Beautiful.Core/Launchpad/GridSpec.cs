using Beautiful.Core.Layout;

namespace Beautiful.Core.Launchpad;

/// <summary>
/// Launchpad 网格的排布方案：多少列 × 多少行、每个单元格多大、整个网格在屏幕上的包围矩形。
/// <para>
/// 【为什么整个结构都是纯推断】显示器矩形 + 图标尺寸 + 间距 + 边距 + DPI 缩放共 5 项，就能
/// 唯一确定一份网格 —— 这正是 macOS 启动台"图标尺寸随屏幕决定、图标总数从不回缩图标"的全部数学。
/// 把它下沉 Core，多屏/混合 DPI 的每一个分支（副屏负坐标、边距比屏还大、DPI 读取失败）
/// 都只产出一个纯数值，单测就能不借助真机穷举。
/// </para>
/// <para>
/// 【居中不溢出】网格横向居中放置在显示器上；如果图标+间距把可用宽度撑满不下，
/// <see cref="LaunchpadLayout.ComputeGrid"/> 会退化到 1 列 / 1 行（见下），
/// 而不会把坐标搞成 NaN —— 面对素描师式"参数乱传"，宁可出一个能用的 1×1，也不愿意崩。
/// </para>
/// </summary>
public readonly record struct GridSpec
{
    /// <summary>每页可容纳的图标列数（≥1）。。</summary>
    public required int Columns { get; init; }

    /// <summary>每页可容纳的图标行数（≥1）。。</summary>
    public required int Rows { get; init; }

    /// <summary>单元格中心之间的距离（物理 px） = 图标物理尺寸 + 单元格间距。</summary>
    public required double CellSizePhysical { get; init; }

    /// <summary>图标实际可绘制区域（物理 px），≤ <see cref="CellSizePhysical"/>。</summary>
    public required double IconSizePhysical { get; init; }

    /// <summary>归一化后的 DPI 缩放（scale≤0/NaN 时记为 1.0）。方便 App 层把其他 DPI 计量换算回物理。</summary>
    public required double Scale { get; init; }

    /// <summary>整个网格落在屏幕上的包围矩形（物理 px，居中于显示器）。</summary>
    public required PhysicalRect GridRect { get; init; }

    /// <summary>每页最多能放多少图标 = Columns × Rows。</summary>
    public int Capacity => Columns * Rows;
}
