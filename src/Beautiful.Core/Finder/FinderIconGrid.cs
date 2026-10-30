using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// 图标视图的网格度量（Phase 6-b）。所有数值都是 DIP，纯算术，不依赖 WPF。
/// </summary>
/// <param name="Columns">每行列数（≥ 1）。</param>
/// <param name="Rows">总行数（0 项时为 0）。</param>
/// <param name="CellWidth">单元格宽（含横向间隙）。</param>
/// <param name="CellHeight">单元格高（含纵向间隙）。</param>
/// <param name="ContentWidth">内容总宽（至少等于可视宽，保证右对齐/背景铺满）。</param>
/// <param name="ContentHeight">内容总高（用于 ScrollViewer 的 ExtentHeight）。</param>
public readonly record struct FinderGridMetrics(
    int Columns,
    int Rows,
    double CellWidth,
    double CellHeight,
    double ContentWidth,
    double ContentHeight);

/// <summary>
/// 图标视图网格布局纯函数（Phase 6-b）。
/// <para>
/// macOS 图标视图是"从左到右、从上到下"的网格，单元格等宽等高，标签两行截断。
/// 这里只算位置：App 侧的自绘面板按同一套公式 Measure/Arrange，框选命中与键盘方向键
/// 也复用同一套公式，因此"看到的格子"与"点中的格子"永远一致——这是把布局锁进纯函数的收益。
/// </para>
/// <para>
/// 边界约定：<paramref name="viewportWidth"/> 小于一个单元格时列数钳为 1（宁可横向裁切也不返回 0 列，
/// 否则索引换算会出现除零）；空列表返回 0 行且内容高度为上下留白之和。
/// </para>
/// </summary>
public static class FinderIconGrid
{
    /// <summary>默认单元格宽（Big Sur 中图标约 104 DIP）。</summary>
    public const double DefaultCellWidth = 104;

    /// <summary>默认单元格高（图标 64 + 两行标签）。</summary>
    public const double DefaultCellHeight = 116;

    /// <summary>网格四周留白。</summary>
    public const double DefaultPadding = 16;

    /// <summary>图标尺寸滑杆的档位（⇧⌘[ / ]）：小/中/大/特大。</summary>
    public static readonly IReadOnlyList<double> IconSizeSteps = [48, 64, 96, 128];

    /// <summary>
    /// 计算网格度量。
    /// </summary>
    /// <param name="viewportWidth">可视宽度（DIP）。</param>
    /// <param name="itemCount">条目数（负数按 0）。</param>
    /// <param name="cellWidth">单元格宽，默认 <see cref="DefaultCellWidth"/>。</param>
    /// <param name="cellHeight">单元格高，默认 <see cref="DefaultCellHeight"/>。</param>
    /// <param name="padding">四周留白，默认 <see cref="DefaultPadding"/>。</param>
    public static FinderGridMetrics Measure(
        double viewportWidth,
        int itemCount,
        double cellWidth = DefaultCellWidth,
        double cellHeight = DefaultCellHeight,
        double padding = DefaultPadding)
    {
        var width = cellWidth > 0 ? cellWidth : DefaultCellWidth;
        var height = cellHeight > 0 ? cellHeight : DefaultCellHeight;
        var pad = Math.Max(0, padding);
        var count = Math.Max(0, itemCount);

        var usable = Math.Max(0, viewportWidth - (2 * pad));
        var columns = (int)Math.Floor(usable / width);
        if (columns < 1)
        {
            columns = 1;
        }

        var rows = count == 0 ? 0 : (int)Math.Ceiling(count / (double)columns);
        var contentWidth = Math.Max(viewportWidth, (2 * pad) + (columns * width));
        var contentHeight = (2 * pad) + (rows * height);
        return new FinderGridMetrics(columns, rows, width, height, contentWidth, contentHeight);
    }

    /// <summary>索引 → 行列（0 基）。索引越界返回 <c>(-1, -1)</c>。</summary>
    public static (int Column, int Row) CellOf(int index, int columns)
    {
        if (index < 0 || columns < 1)
        {
            return (-1, -1);
        }

        return (index % columns, index / columns);
    }

    /// <summary>索引 → 单元格左上角坐标（含留白）。</summary>
    public static (double X, double Y) OriginOf(
        int index,
        FinderGridMetrics metrics,
        double padding = DefaultPadding)
    {
        var (column, row) = CellOf(index, metrics.Columns);
        if (column < 0)
        {
            return (0, 0);
        }

        return (padding + (column * metrics.CellWidth), padding + (row * metrics.CellHeight));
    }

    /// <summary>
    /// 命中测试：点 <c>(x, y)</c> 落在哪个索引的单元格内（越界/落在留白/空行返回 -1）。
    /// </summary>
    public static int IndexAt(
        double x,
        double y,
        FinderGridMetrics metrics,
        int itemCount,
        double padding = DefaultPadding)
    {
        if (itemCount <= 0 || metrics.Columns < 1 || metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            return -1;
        }

        var localX = x - padding;
        var localY = y - padding;
        if (localX < 0 || localY < 0)
        {
            return -1;
        }

        var column = (int)Math.Floor(localX / metrics.CellWidth);
        var row = (int)Math.Floor(localY / metrics.CellHeight);
        if (column < 0 || column >= metrics.Columns || row < 0 || row >= metrics.Rows)
        {
            return -1;
        }

        var index = (row * metrics.Columns) + column;
        return index < itemCount ? index : -1;
    }

    /// <summary>
    /// 框选命中：返回与矩形相交的所有索引（升序）。矩形按左上/右下归一化，容忍反向拖动。
    /// </summary>
    public static IReadOnlyList<int> ItemsInRect(
        double x1,
        double y1,
        double x2,
        double y2,
        FinderGridMetrics metrics,
        int itemCount,
        double padding = DefaultPadding)
    {
        if (itemCount <= 0 || metrics.Columns < 1 || metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            return Array.Empty<int>();
        }

        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var right = Math.Max(x1, x2);
        var bottom = Math.Max(y1, y2);

        var firstColumn = (int)Math.Floor((left - padding) / metrics.CellWidth);
        var lastColumn = (int)Math.Floor((right - padding) / metrics.CellWidth);
        var firstRow = (int)Math.Floor((top - padding) / metrics.CellHeight);
        var lastRow = (int)Math.Floor((bottom - padding) / metrics.CellHeight);

        firstColumn = Math.Max(0, firstColumn);
        lastColumn = Math.Min(metrics.Columns - 1, lastColumn);
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Min(metrics.Rows - 1, lastRow);

        var hits = new List<int>();
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = (row * metrics.Columns) + column;
                if (index >= itemCount)
                {
                    break;
                }

                // 单元格矩形与框选矩形相交（半开区间：零面积框仍能选中一个格子）。
                var cellLeft = padding + (column * metrics.CellWidth);
                var cellTop = padding + (row * metrics.CellHeight);
                if (cellLeft < right && cellLeft + metrics.CellWidth > left
                    && cellTop < bottom && cellTop + metrics.CellHeight > top)
                {
                    hits.Add(index);
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// 方向键移动：在当前网格里从 <paramref name="index"/> 走一格（macOS 图标视图语义）。
    /// 左右越界不换行（保持在原列），上下越界返回 -1；没有选中项时按首个方向落到 0 或末项。
    /// </summary>
    /// <param name="index">当前索引（-1 表示无选中）。</param>
    /// <param name="deltaColumn">-1 左 / +1 右。</param>
    /// <param name="deltaRow">-1 上 / +1 下。</param>
    /// <param name="metrics">网格度量。</param>
    /// <param name="itemCount">条目数。</param>
    /// <returns>新索引；无法移动返回 -1。</returns>
    public static int Move(int index, int deltaColumn, int deltaRow, FinderGridMetrics metrics, int itemCount)
    {
        if (itemCount <= 0 || metrics.Columns < 1)
        {
            return -1;
        }

        if (index < 0 || index >= itemCount)
        {
            return deltaRow < 0 ? itemCount - 1 : 0;
        }

        var (column, row) = CellOf(index, metrics.Columns);
        var targetColumn = column + deltaColumn;
        var targetRow = row + deltaRow;
        if (targetColumn < 0 || targetColumn >= metrics.Columns || targetRow < 0 || targetRow >= metrics.Rows)
        {
            return -1;
        }

        var target = (targetRow * metrics.Columns) + targetColumn;
        return target < itemCount ? target : -1;
    }

    /// <summary>把任意图标尺寸钳到 <see cref="IconSizeSteps"/> 的合法档位（最近的档）。</summary>
    public static double SnapIconSize(double size)
    {
        var best = IconSizeSteps[0];
        var bestDistance = Math.Abs(size - best);
        foreach (var step in IconSizeSteps)
        {
            var distance = Math.Abs(size - step);
            if (distance < bestDistance)
            {
                best = step;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>图标尺寸 → 单元格尺寸（图标下方留 2 行标签空间）。</summary>
    public static (double CellWidth, double CellHeight) CellFor(double iconSize)
    {
        var icon = SnapIconSize(iconSize);
        var width = Math.Max(DefaultCellWidth, icon + 24);
        var height = icon + 52;
        return (width, height);
    }
}
