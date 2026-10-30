using Beautiful.Core.Layout;

namespace Beautiful.Core.Launchpad;

/// <summary>
/// Launchpad 网格排版的纯函数：把"显示器矩形 + 图标/间距/边距 + DPI 缩放"还原成
/// 屏幕上居中的固定列 × 行网格。
/// <para>
/// 【为什么排版算术独立于分页】网格本身只是"这块屏幕上能摆多少列多少行、每个格子在哪"；
/// 怎么把多页图标填进去是 <see cref="LaunchpadPagination"/> 的事。解耦两个理由：
/// （1）分页是整除/取余，排版是几何 —— 纯函数粒度越细越容易钉住；
/// （2）App 侧在动画/拖拽期望拿到"完整的格子矩阵"却不用重新算分页，二者自由组合。
/// </para>
/// <para>
/// 【图标不回缩】像素总数超出容量时，多出来的图标分页到下一页（<see cref="LaunchpadPagination"/>），
/// 图标尺寸始终维持 <c>iconSizeDip × scale</c> —— 这与 macOS 启动台一致，图标大小随屏幕而非数量变化。
/// </para>
/// </summary>
public static class LaunchpadLayout
{
    /// <summary>
    /// 在给定显示器与 DPI 下计算网格排布。
    /// <para>
    /// <paramref name="monitor"/> 可为副屏（负起始坐标）；<paramref name="scale"/> 取 DPI 失败
    /// 时传 ≤0/NaN 视为 1.0 —— 拿不到缩放不应该把整张网格全炸（<see cref="StripMath.ToPhysical"/>
    /// 内部就已兜底 <see cref="StripMath"/> 的 SafeScale，这里把归一化后的值也带给调用方）。
    /// </para>
    /// <para>
    /// 负的图标/间距/边距会被钳为 0：它们不是合法几何参数，只会把可用空间撑成负数。
    /// 若钳完边距后可用空间 ≤0，或者单元格尺寸 ≤0，则退化为 1×1 网格 —— 至少能放下一个图标。
    /// </para>
    /// </summary>
    /// <param name="monitor">显示器矩形（物理 px）。副屏 <c>Left</c> 可为负。</param>
    /// <param name="iconSizeDip">图标可绘制尺寸（DIP），e.g. 64。</param>
    /// <param name="cellGapDip">单元格间距 = 图标尺寸以外、中心到中心之余量（DIP），e.g. 32。</param>
    /// <param name="edgeMarginDip">网格距屏幕四边的留白（DIP），e.g. 48。</param>
    /// <param name="scale">该显示器的 DPI 缩放（= 有效 DPI / 96）。</param>
    /// <returns>网格方案 <see cref="GridSpec"/>，其中 <see cref="GridSpec.Capacity"/> 为每页图标数。</returns>
    public static GridSpec ComputeGrid(
        in PhysicalRect monitor,
        double iconSizeDip,
        double cellGapDip,
        double edgeMarginDip,
        double scale)
    {
        // 负的几何参数钳到 0 —— 只会把可用空间撑成负数，没有合法语义
        var iconDip = Math.Max(0, iconSizeDip);
        var gapDip = Math.Max(0, cellGapDip);
        var marginDip = Math.Max(0, edgeMarginDip);
        var safeScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;

        var iconPhysical = StripMath.ToPhysical(iconDip, safeScale);
        var cellPhysical = StripMath.ToPhysical(iconDip + gapDip, safeScale);
        var marginPhysical = StripMath.ToPhysical(marginDip, safeScale);

        // 可用宽度 = 显示器宽 - 2 倍边距；存负说明边距过大，此时仍留至少 1 列而不是 0
        var availW = monitor.Width - 2.0 * marginPhysical;
        var availH = monitor.Height - 2.0 * marginPhysical;

        int columns;
        if (cellPhysical > 0 && availW > 0)
            columns = Math.Max(1, (int)(availW / cellPhysical));
        else
            columns = 1;

        int rows;
        if (cellPhysical > 0 && availH > 0)
            rows = Math.Max(1, (int)(availH / cellPhysical));
        else
            rows = 1;

        var gridW = columns * cellPhysical;
        var gridH = rows * cellPhysical;
        // 居中：用 (monitor.Width - gridW) / 2 抵消不满整行的余量
        var gridLeft = monitor.Left + (monitor.Width - gridW) / 2.0;
        var gridTop = monitor.Top + (monitor.Height - gridH) / 2.0;

        var gridRect = new PhysicalRect(
            (int)Math.Round(gridLeft),
            (int)Math.Round(gridTop),
            (int)Math.Round(gridLeft + gridW),
            (int)Math.Round(gridTop + gridH));

        return new GridSpec
        {
            Columns = columns,
            Rows = rows,
            CellSizePhysical = cellPhysical,
            IconSizePhysical = iconPhysical,
            Scale = safeScale,
            GridRect = gridRect,
        };
    }

    /// <summary>
    /// 生成整页的网格坐标矩阵：<c>[row, col]</c> → <see cref="GridCell"/>。
    /// <para>
    /// 矩阵尺寸恒为 <c>Rows × Columns</c>，空位也有坐标 —— 拖拽落位与分页切换都靠这个矩阵
    /// 复用槽位，App 侧不用在"空槽"上特殊处理。
    /// </para>
    /// <returns>逐行填充的 2D 数组：matrix[row, col]。</returns>
    public static GridCell[,] CellMatrix(GridSpec grid)
    {
        var matrix = new GridCell[grid.Rows, grid.Columns];
        for (var row = 0; row < grid.Rows; row++)
        {
            // 每行顶边 = GridRect.Top + row * CellSizePhysical，中心再 + 0.5 * CellSizePhysical
            var cy = grid.GridRect.Top + (row + 0.5) * grid.CellSizePhysical;
            for (var col = 0; col < grid.Columns; col++)
            {
                var cx = grid.GridRect.Left + (col + 0.5) * grid.CellSizePhysical;
                matrix[row, col] = new GridCell(col, row, cx, cy);
            }
        }
        return matrix;
    }

    /// <summary>
    /// 行主序槽位 → 单元格（slot 0 = 左上 → 先右后下）。
    /// <para>
    /// 把分页后的全局索引直接映射到坐标：先 <see cref="LaunchpadPagination.PageOf"/> 得页内槽位，
    /// 再这儿拿单元格中心。两个函数一起用即可把"图标第 i 个"映射到屏幕上的一个点。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">槽位越界（< 0 或 ≥ Capacity）。</exception>
    public static GridCell CellAt(GridSpec grid, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= grid.Capacity)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        var row = slotIndex / grid.Columns;
        var col = slotIndex % grid.Columns;
        var cx = grid.GridRect.Left + (col + 0.5) * grid.CellSizePhysical;
        var cy = grid.GridRect.Top + (row + 0.5) * grid.CellSizePhysical;
        return new GridCell(col, row, cx, cy);
    }
}
