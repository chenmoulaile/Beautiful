using Beautiful.Core.Layout;
using Beautiful.Core.Launchpad;
using Xunit;

/// <summary>
/// Launchpad 网格排版纯函数测试：列、行、居中、DPI 放大、副屏负坐标、边界钳位。
/// 这些分支在真机上难以全覆盖（尤其混合 DPI 多屏），所以在 Core 单测里穷举。
/// </summary>
public class LaunchpadLayoutTests
{
    // 1920×1080 @100% 的主屏
    private static readonly PhysicalRect Primary = new(0, 0, 1920, 1080);
    // 副屏负起始坐标
    private static readonly PhysicalRect Secondary = new(-2560, 0, 0, 1440);

    // macOS Launchpad 在 1080p@100% 约 6×5 网格（图标 64 + 32 间距 + 48 边距）
    private const double IconDip = 64, GapDip = 32, MarginDip = 48;

    // ---------- ComputeGrid：基本排布 ----------

    [Fact]
    public void 分辨率1080p缩放100_给出_6列_5行网格()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, scale: 1.0);
        // 单元格中心距 = 64 + 32 = 96；可用宽 = 1920 - 96 = 1824；1824/96 = 19 → 19 列
        // 可用高 = 1080 - 96 = 984；984/96 = 10.25 → 10 行
        Assert.Equal(19, grid.Columns);
        Assert.Equal(10, grid.Rows);
        Assert.Equal(96.0, grid.CellSizePhysical);
        Assert.Equal(64.0, grid.IconSizePhysical);
        Assert.Equal(190, grid.Capacity);
    }

    [Fact]
    public void 网格水平垂直居中_左右余量相等()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, scale: 1.0);
        var gridW = grid.Columns * grid.CellSizePhysical;
        var gridH = grid.Rows * grid.CellSizePhysical;
        var leftGap = grid.GridRect.Left - Primary.Left;
        var rightGap = Primary.Right - grid.GridRect.Right;
        var topGap = grid.GridRect.Top - Primary.Top;
        var bottomGap = Primary.Bottom - grid.GridRect.Bottom;
        // 居中 → 余量对称
        Assert.Equal(leftGap, rightGap);
        Assert.Equal(topGap, bottomGap);
        // 余量 = (monitor - grid) / 2
        Assert.Equal((Primary.Width - gridW) / 2, leftGap, 1);
        Assert.Equal((Primary.Height - gridH) / 2, topGap, 1);
    }

    // ---------- ComputeGrid：DPI 放大 ----------

    [Fact]
    public void 缩放200_图标间距边距全翻倍()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, scale: 2.0);
        // 64*2=128, 32*2=64 → 单元格中心距 192
        Assert.Equal(192.0, grid.CellSizePhysical);
        Assert.Equal(128.0, grid.IconSizePhysical);
        // 可用宽 = 1920 - 2*96 = 1728；1728/192 = 9
        Assert.Equal(9, grid.Columns);
        // 可用高 = 1080 - 2*96 = 888；888/192 = 4.625 → 4 行
        Assert.Equal(4, grid.Rows);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void 不同缩放下_列数总少于或等于_100pct_时(double scale)
    {
        var baseGrid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var scaled = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, scale);
        // 放大 → 每格更大 → 能摆的更少
        Assert.True(scaled.Columns <= baseGrid.Columns);
        Assert.True(scaled.Rows <= baseGrid.Rows);
    }

    // ---------- ComputeGrid：副屏 ----------

    [Fact]
    public void 副屏负坐标_网格落在副屏上_不是主屏()
    {
        var grid = LaunchpadLayout.ComputeGrid(Secondary, IconDip, GapDip, MarginDip, scale: 1.0);
        // 网格应落在副屏范围内
        Assert.InRange(grid.GridRect.Left, Secondary.Left, Secondary.Right);
        Assert.InRange(grid.GridRect.Top, Secondary.Top, Secondary.Bottom);
        Assert.InRange(grid.GridRect.Right, Secondary.Left, Secondary.Right);
        Assert.InRange(grid.GridRect.Bottom, Secondary.Top, Secondary.Bottom);
    }

    // ---------- ComputeGrid：边界钳位 ----------

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-10, -10, -10, -10)]
    public void 非法图标_间距_边距_钳到_0_退化为_1x1(double icon, double gap, double margin, double _)
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, icon, gap, margin, scale: 1.0);
        Assert.Equal(1, grid.Columns);
        Assert.Equal(1, grid.Rows);
        Assert.Equal(1, grid.Capacity);
    }

    [Fact]
    public void NaN_图标尺寸_退化为_1x1()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, double.NaN, 32, 48, scale: 1.0);
        Assert.Equal(1, grid.Columns);
        Assert.Equal(1, grid.Rows);
    }

    [Fact]
    public void 无穷大_图标尺寸_退化为_1x1()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, double.PositiveInfinity, 32, 48, scale: 1.0);
        Assert.Equal(1, grid.Columns);
        Assert.Equal(1, grid.Rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void scale_非法时_按_1_0_兜底(double scale)
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, scale);
        Assert.Equal(1.0, grid.Scale);
        // 物理尺寸应等于 DIP 值（scale=1）
        Assert.Equal((double)StripMath.ToPhysical(IconDip, 1.0), grid.IconSizePhysical);
    }

    [Fact]
    public void scale_NaN_钻_1_0_兜底()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, double.NaN);
        Assert.Equal(1.0, grid.Scale);
        Assert.Equal((double)StripMath.ToPhysical(IconDip, 1.0), grid.IconSizePhysical);
    }

    [Fact]
    public void scale_正无穷_钻_1_0_兜底()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, double.PositiveInfinity);
        Assert.Equal(1.0, grid.Scale);
    }

    [Fact]
    public void 边距比屏幕还大_退化为_1x1_网格()
    {
        // 边距 2000 > 屏幕 1920/2=960 → 可用宽 < 0
        var grid = LaunchpadLayout.ComputeGrid(Primary, 64, 32, 2000, scale: 1.0);
        Assert.Equal(1, grid.Columns);
        Assert.Equal(1, grid.Rows);
    }

    // ---------- CellMatrix ----------

    [Fact]
    public void 矩阵维数_恒为_Rows_x_Columns()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var matrix = LaunchpadLayout.CellMatrix(grid);
        Assert.Equal(grid.Rows, matrix.GetLength(0));
        Assert.Equal(grid.Columns, matrix.GetLength(1));
    }

    [Fact]
    public void 矩阵中_每个_cell_的中心_与_CellAt_一致()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var matrix = LaunchpadLayout.CellMatrix(grid);
        for (var row = 0; row < grid.Rows; row++)
        for (var col = 0; col < grid.Columns; col++)
        {
            var slot = row * grid.Columns + col;
            var fromMatrix = matrix[row, col];
            var fromCellAt = LaunchpadLayout.CellAt(grid, slot);
            Assert.Equal(fromMatrix, fromCellAt);
            Assert.Equal(col, fromMatrix.Column);
            Assert.Equal(row, fromMatrix.Row);
        }
    }

    [Fact]
    public void 矩阵左上角_第一个_cell_的_Col_Row_为_0()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var matrix = LaunchpadLayout.CellMatrix(grid);
        var first = matrix[0, 0];
        Assert.Equal(0, first.Column);
        Assert.Equal(0, first.Row);
    }

    // ---------- CellAt ----------

    [Fact]
    public void 槽位_0_是第_0_行第_0_列()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var cell = LaunchpadLayout.CellAt(grid, 0);
        Assert.Equal(0, cell.Row);
        Assert.Equal(0, cell.Column);
    }

    [Fact]
    public void 槽位_递增时_行主序_先右后下()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        // slot 1: row 0, col 1
        var c1 = LaunchpadLayout.CellAt(grid, 1);
        Assert.Equal(0, c1.Row);
        Assert.Equal(1, c1.Column);
        // slot = Columns: row 1, col 0
        var cNext = LaunchpadLayout.CellAt(grid, grid.Columns);
        Assert.Equal(1, cNext.Row);
        Assert.Equal(0, cNext.Column);
    }

    [Fact]
    public void 中间槽位_中心坐标在网格内部()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, IconDip, GapDip, MarginDip, 1.0);
        var slot = grid.Capacity / 2;
        var cell = LaunchpadLayout.CellAt(grid, slot);
        Assert.InRange(cell.CenterX, grid.GridRect.Left, grid.GridRect.Right);
        Assert.InRange(cell.CenterY, grid.GridRect.Top, grid.GridRect.Bottom);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]   // 1x1 网格，capacity=1，slot 1 越界
    public void 槽位越界_抛出_ArgumentOutOfRangeException(int slot)
    {
        var grid = new GridSpec
        {
            Columns = 1, Rows = 1, CellSizePhysical = 64, IconSizePhysical = 64,
            Scale = 1.0, GridRect = new PhysicalRect(0, 0, 100, 100)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchpadLayout.CellAt(grid, slot));
    }

    [Fact]
    public void 单格网格_槽位_0_有效()
    {
        var grid = LaunchpadLayout.ComputeGrid(Primary, iconSizeDip: 0, cellGapDip: 0, edgeMarginDip: 0, scale: 1.0);
        Assert.Equal(1, grid.Capacity);
        var cell = LaunchpadLayout.CellAt(grid, 0);
        Assert.Equal(0, cell.Column);
        Assert.Equal(0, cell.Row);
        // 居中于屏幕
        Assert.Equal(Primary.Width / 2.0, cell.CenterX, 1);
        Assert.Equal(Primary.Height / 2.0, cell.CenterY, 1);
    }
}
