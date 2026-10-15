namespace Beautiful.Core.Dock;

/// <summary>
/// 行基准排布结果（未放大）。
/// </summary>
/// <param name="NaturalWidth">未放大总宽（末项尾随 spacing 已扣除）。</param>
/// <param name="FieldCount">写入放大场的项数（= 可见且参与放大的条目数）。</param>
/// <param name="VisibleCount">可见（宽度 &gt; 门槛）条目数，含分隔符。</param>
public readonly record struct DockBaseLayout(double NaturalWidth, int FieldCount, int VisibleCount);

/// <summary>
/// Dock 行的未放大基准排布（纯函数 + 调用方缓冲区：只在集合/模板尺寸变化时跑，逐帧路径不碰它）。
/// <para>
/// 三条规则一次算清，避免 App 侧再各写一份账：
/// <list type="bullet">
/// <item><description>可见项**依次占位**：<c>base_i = 前项右缘 + spacing</c>；</description></item>
/// <item><description><c>magnifiable=false</c> 的条目（分隔符）照常占位宽，但中心**不进放大场**、
/// 下标映射里也没有它 → 它的目标缩放恒 1（不放大、不悬停），邻居放大时它只随行整体平移，
/// 不会被当作可拉伸图标推挤；</description></item>
/// <item><description>收起的槽（宽度 ≤ <paramref name="minVisibleWidth"/>，如无会话的媒体胶囊 Width=0）
/// 既不占位也不进场，且左缘贴着下一个可见项 → 不留空隙、不产生"幽灵鼓包"。</description></item>
/// </list>
/// </para>
/// </summary>
public static class DockRowLayout
{
    /// <exception cref="ArgumentException">缓冲区长度不足（逐帧路径会因长度不足抛异常 → 整排图标冻结）。</exception>
    public static DockBaseLayout Compute(
        IReadOnlyList<double> widths,
        IReadOnlyList<bool> magnifiable,
        double spacing,
        double minVisibleWidth,
        double[] baseLefts,
        double[] fieldCenters,
        int[] fieldToItem)
    {
        var count = widths.Count;
        if (magnifiable.Count < count) throw new ArgumentException($"magnifiable 长度 {magnifiable.Count} < {count}", nameof(magnifiable));
        if (baseLefts.Length < count) throw new ArgumentException($"baseLefts 长度 {baseLefts.Length} < {count}", nameof(baseLefts));
        if (fieldCenters.Length < count) throw new ArgumentException($"fieldCenters 长度 {fieldCenters.Length} < {count}", nameof(fieldCenters));
        if (fieldToItem.Length < count) throw new ArgumentException($"fieldToItem 长度 {fieldToItem.Length} < {count}", nameof(fieldToItem));

        var acc = 0.0;
        var field = 0;
        var visible = 0;
        for (var i = 0; i < count; i++)
        {
            var w = widths[i];
            if (double.IsNaN(w) || w < 0) w = 0;
            if (w > minVisibleWidth)
            {
                baseLefts[i] = acc;
                if (magnifiable[i])
                {
                    fieldCenters[field] = acc + w / 2.0;
                    fieldToItem[field] = i;
                    field++;
                }
                visible++;
                acc += w + spacing;
            }
            else
            {
                baseLefts[i] = acc;   // 隐藏槽：不占位（下一个可见项紧接其后）
            }
        }

        // 末项的尾随 spacing 不算进行宽（visible 而非 field：分隔符也要占位宽）
        var natural = Math.Max(0.0, acc - (visible > 0 ? spacing : 0.0));
        return new DockBaseLayout(natural, field, visible);
    }
}
