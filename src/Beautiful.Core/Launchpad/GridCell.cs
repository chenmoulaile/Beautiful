namespace Beautiful.Core.Launchpad;

/// <summary>
/// 网格中的一个单元格：行列索引 + 图标中心的物理像素坐标。
/// <para>
/// 【为什么是记录结构体】它只是 4 个数的打包（col/row 不过是 CenterX/Y 的冗余索引，
/// 方便调试与落位互查），在 <see cref="LaunchpadLayout.CellMatrix"/> 里被填入二维数组。
/// 作为值类型穿过路径不会分配到堆上，整个矩阵一次 Allocate 一个数组而已。
/// </para>
/// <para>
/// 坐标系与 <see cref="Layout.PhysicalRect"/> 一致：Left/Top 含、单位物理像素，副屏可为负。
/// </para>
/// </summary>
/// <param name="Column">列索引（0-based，左→右递增）。</param>
/// <param name="Row">行索引（0-based，上→下递增）。</param>
/// <param name="CenterX">图标中心 X（物理 px）。</param>
/// <param name="CenterY">图标中心 Y（物理 px）。</param>
public readonly record struct GridCell(int Column, int Row, double CenterX, double CenterY);
