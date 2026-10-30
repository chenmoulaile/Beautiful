using System.Linq;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 图标视图网格布局测试（Phase 6-b）。
/// <para>
/// 布局、命中、框选、方向键四件事共用同一套度量：只要这里把"第几个格子在哪、点在哪算第几个"
/// 钉死，UI 侧的自绘面板就不会出现"看到的位置"和"点中的位置"错位——那是图标视图最常见的 bug。
/// </para>
/// </summary>
public class FinderIconGridTests
{
    // 视口 448 = 2*16 留白 + 4*104（恰好 4 列），便于手算
    private const double ViewportFourColumns = 448;

    private static FinderGridMetrics FourColumns(int items)
        => FinderIconGrid.Measure(ViewportFourColumns, items);

    [Fact]
    public void 四列视口下按单元格宽度算出列数与行数()
    {
        var metrics = FourColumns(9);

        Assert.Equal(4, metrics.Columns);
        Assert.Equal(3, metrics.Rows);   // ceil(9/4)
        Assert.Equal(FinderIconGrid.DefaultCellWidth, metrics.CellWidth);
        Assert.Equal((2 * FinderIconGrid.DefaultPadding) + (3 * FinderIconGrid.DefaultCellHeight),
            metrics.ContentHeight, precision: 3);
    }

    [Fact]
    public void 空列表行数为零且内容高度只含留白()
    {
        var metrics = FinderIconGrid.Measure(400, 0);

        Assert.Equal(0, metrics.Rows);
        Assert.Equal(2 * FinderIconGrid.DefaultPadding, metrics.ContentHeight, precision: 3);
    }

    [Fact]
    public void 视口窄于一个单元格时列数钳为一()
    {
        var metrics = FinderIconGrid.Measure(50, 3);

        Assert.Equal(1, metrics.Columns);
        Assert.Equal(3, metrics.Rows);
    }

    [Fact]
    public void 索引到行列与左上角坐标一致()
    {
        var metrics = FourColumns(9);

        Assert.Equal((0, 0), FinderIconGrid.CellOf(0, 4));
        Assert.Equal((3, 0), FinderIconGrid.CellOf(3, 4));
        Assert.Equal((0, 1), FinderIconGrid.CellOf(4, 4));
        Assert.Equal((1, 2), FinderIconGrid.CellOf(9, 4));

        var (x, y) = FinderIconGrid.OriginOf(5, metrics);
        Assert.Equal(FinderIconGrid.DefaultPadding + FinderIconGrid.DefaultCellWidth, x, precision: 3);
        Assert.Equal(FinderIconGrid.DefaultPadding + FinderIconGrid.DefaultCellHeight, y, precision: 3);
    }

    [Fact]
    public void 点击格子中心命中对应索引_留白与空行返回负一()
    {
        var metrics = FourColumns(9);

        var (x, y) = FinderIconGrid.OriginOf(6, metrics);
        var hit = FinderIconGrid.IndexAt(x + 10, y + 10, metrics, 9);
        Assert.Equal(6, hit);

        Assert.Equal(-1, FinderIconGrid.IndexAt(2, 2, metrics, 9));          // 左上留白
        var (lastX, lastY) = FinderIconGrid.OriginOf(9, metrics);            // 第 10 格（越界）
        Assert.Equal(-1, FinderIconGrid.IndexAt(lastX + 10, lastY + 10, metrics, 9));
    }

    [Fact]
    public void 框选返回相交的格子_反向拖动同样有效()
    {
        var metrics = FourColumns(9);
        var cellWidth = metrics.CellWidth;
        var cellHeight = metrics.CellHeight;
        var pad = FinderIconGrid.DefaultPadding;

        // 覆盖第 0 行前两格 + 第 1 行第一格的一部分
        var hits = FinderIconGrid.ItemsInRect(
            pad + 5, pad + 5,
            pad + (cellWidth * 2) - 5, pad + cellHeight + 5,
            metrics, 9);
        Assert.Equal(new[] { 0, 1, 4, 5 }, hits.OrderBy(i => i).ToArray());

        // 起点在右下、终点在左上（用户往上拖）
        var reversed = FinderIconGrid.ItemsInRect(
            pad + (cellWidth * 2) - 5, pad + cellHeight + 5,
            pad + 5, pad + 5,
            metrics, 9);
        Assert.Equal(hits.OrderBy(i => i).ToArray(), reversed.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void 框选不越出条目总数()
    {
        var metrics = FourColumns(5);   // 2 行，第 2 行只有 1 项
        var hits = FinderIconGrid.ItemsInRect(0, 0, 10_000, 10_000, metrics, 5);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, hits.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void 方向键左右不换行_上下越界返回负一()
    {
        var metrics = FourColumns(9);

        Assert.Equal(1, FinderIconGrid.Move(0, 1, 0, metrics, 9));
        Assert.Equal(-1, FinderIconGrid.Move(0, -1, 0, metrics, 9));      // 最左列再左
        Assert.Equal(-1, FinderIconGrid.Move(3, 1, 0, metrics, 9));       // 最右列再右（不换行）
        Assert.Equal(4, FinderIconGrid.Move(0, 0, 1, metrics, 9));
        Assert.Equal(-1, FinderIconGrid.Move(0, 0, -1, metrics, 9));
    }

    [Fact]
    public void 无选中时方向键落到首项或末项()
    {
        var metrics = FourColumns(9);

        Assert.Equal(0, FinderIconGrid.Move(-1, 0, 1, metrics, 9));
        Assert.Equal(8, FinderIconGrid.Move(-1, 0, -1, metrics, 9));
        Assert.Equal(-1, FinderIconGrid.Move(-1, 0, 0, metrics, 0));
    }

    [Fact]
    public void 图标尺寸吸附到最近的档位并推导单元格尺寸()
    {
        Assert.Equal(48, FinderIconGrid.SnapIconSize(40));
        Assert.Equal(64, FinderIconGrid.SnapIconSize(70));
        Assert.Equal(128, FinderIconGrid.SnapIconSize(200));

        var (width, height) = FinderIconGrid.CellFor(64);
        Assert.Equal(104, width);
        Assert.Equal(116, height);

        var (bigWidth, bigHeight) = FinderIconGrid.CellFor(128);
        Assert.Equal(152, bigWidth);
        Assert.Equal(180, bigHeight);
    }
}
