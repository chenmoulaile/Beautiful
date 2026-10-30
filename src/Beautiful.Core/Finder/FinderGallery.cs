using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// 画廊视图胶片条的度量（Phase 6-c）。全部 DIP，纯算术，不依赖 WPF。
/// </summary>
/// <param name="Capacity">一屏能完整放下的缩略图个数（≥ 1）。</param>
/// <param name="ContentWidth">内容总宽（含首尾留白；空列表时为两倍留白）。</param>
public readonly record struct FinderGalleryMetrics(
    int Capacity,
    double ContentWidth);

/// <summary>
/// 画廊视图（Big Sur 第四种视图）的布局纯函数（Phase 6-c）。
/// <para>
/// 画廊 = 上半屏大预览 + 下半屏一条横向胶片条。这里只算"胶片条怎么排、点在哪算第几个、
/// 选中项要滚到哪、大图缩到多大"四件事；App 侧的缩略图容器、预览控件与键位都复用同一套公式，
/// 因此"看到的选中框"和"实际选中的条目"不会错位（与图标视图同一哲学）。
/// </para>
/// <para>
/// 边界约定：视口窄于一个缩略图时容量钳为 1（宁可横向裁切也不返回 0，否则索引换算会出现除零）；
/// 条目数、索引、源尺寸的负数一律按非法输入处理，调用方拿到 -1 / 0 后自行降级，不抛异常。
/// </para>
/// </summary>
public static class FinderGallery
{
    /// <summary>缩略图格宽（Big Sur 胶片条约 84 DIP）。</summary>
    public const double ThumbWidth = 84;

    /// <summary>缩略图格高（图片按等比缩进这个框，标签不上胶片条）。</summary>
    public const double ThumbHeight = 62;

    /// <summary>缩略图之间的横向间隙。</summary>
    public const double Gap = 6;

    /// <summary>胶片条左右留白。</summary>
    public const double Padding = 10;

    /// <summary>
    /// 计算胶片条度量。
    /// </summary>
    /// <param name="viewportWidth">可视宽度（DIP）。</param>
    /// <param name="itemCount">条目数（负数按 0）。</param>
    public static FinderGalleryMetrics Measure(double viewportWidth, int itemCount)
    {
        var count = Math.Max(0, itemCount);
        var step = ThumbWidth + Gap;
        var usable = viewportWidth - (2 * Padding);

        // 容量 = 能完整放下的格子数；最后一个格子不需要 trailing gap，所以先补一个 gap 再除
        var capacity = (int)Math.Floor((usable + Gap) / step);
        if (capacity < 1)
        {
            capacity = 1;
        }

        var contentWidth = count == 0
            ? 2 * Padding
            : (2 * Padding) + (count * ThumbWidth) + ((count - 1) * Gap);

        return new FinderGalleryMetrics(capacity, contentWidth);
    }

    /// <summary>索引 → 缩略图左边在内容坐标系里的 X（含留白）；越界返回 -1。</summary>
    public static double OriginOf(int index, int itemCount)
    {
        if (index < 0 || index >= itemCount)
        {
            return -1;
        }

        return Padding + (index * (ThumbWidth + Gap));
    }

    /// <summary>
    /// 命中测试：内容坐标系里的 <paramref name="x"/> 落在第几个缩略图上。
    /// 落在间隙/留白/越界返回 -1（调用方据此不改变选中）。
    /// </summary>
    public static int IndexAt(double x, int itemCount)
    {
        if (itemCount <= 0 || double.IsNaN(x))
        {
            return -1;
        }

        var local = x - Padding;
        if (local < 0)
        {
            return -1;
        }

        var step = ThumbWidth + Gap;
        var index = (int)Math.Floor(local / step);
        if (index < 0 || index >= itemCount)
        {
            return -1;
        }

        // 格子内部才算命中：右边的 6px 间隙不选中任何东西（与 macOS 一致，点缝隙不跳选）
        var within = local - (index * step);
        return within <= ThumbWidth ? index : -1;
    }

    /// <summary>
    /// 让选中项尽量居中的滚动偏移，夹在 <c>[0, max(0, contentWidth - viewportWidth)]</c>。
    /// 选中项非法或列表为空时返回 0（列表开头）。
    /// </summary>
    public static double ScrollOffsetFor(double viewportWidth, int itemCount, int selectedIndex)
    {
        if (itemCount <= 0 || selectedIndex < 0 || selectedIndex >= itemCount || double.IsNaN(viewportWidth))
        {
            return 0;
        }

        var metrics = Measure(viewportWidth, itemCount);
        var max = Math.Max(0, metrics.ContentWidth - viewportWidth);
        var center = OriginOf(selectedIndex, itemCount) + (ThumbWidth / 2) - (viewportWidth / 2);
        return Math.Clamp(center, 0, max);
    }

    /// <summary>
    /// 方向键移动选中项：左/上 −1，右/下 +1，两端夹住不环绕。
    /// 当前无选中（<c>-1</c>）时按方向落到首项或末项；空列表返回 -1。
    /// </summary>
    public static int MoveSelection(int current, int delta, int itemCount)
    {
        if (itemCount <= 0)
        {
            return -1;
        }

        if (current < 0 || current >= itemCount)
        {
            return delta < 0 ? itemCount - 1 : 0;
        }

        return Math.Clamp(current + delta, 0, itemCount - 1);
    }

    /// <summary>
    /// 大预览的等比缩放：缩到能完整放进可用区域，**不放大小图**（macOS 画廊里 16px 图标
    /// 不会被拉成一整屏马赛克）。源尺寸非法返回 <c>(0, 0)</c>，调用方据此显示图标/文字回退。
    /// </summary>
    public static (double Width, double Height) FitPreview(
        double availableWidth,
        double availableHeight,
        double sourceWidth,
        double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || double.IsNaN(sourceWidth) || double.IsNaN(sourceHeight))
        {
            return (0, 0);
        }

        var maxWidth = Math.Max(1, availableWidth);
        var maxHeight = Math.Max(1, availableHeight);
        var scale = Math.Min(1.0, Math.Min(maxWidth / sourceWidth, maxHeight / sourceHeight));
        return (sourceWidth * scale, sourceHeight * scale);
    }
}
