namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 图标 macOS 式放大计算（纯函数，无 UI 依赖，放 Core 以便单测）。
/// <list type="bullet">
/// <item><description><see cref="ComputeScales"/>：高斯衰减 scale_i = 1 + (max-1)·exp(-(d/σ)²)，d 为与悬停项的序差。</description></item>
/// <item><description><see cref="ComputeOffsets"/>：累计半宽补偿 offsets[i]，表示第 i 个图标相对未放大布局右移的量；
/// 放大时左移对称处理在 View 层通过整体居中完成。</description></item>
/// </list>
/// </summary>
public static class Magnifier
{
    public static double[] ComputeScales(int count, int hovered, double maxScale, double sigma)
    {
        var scales = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (hovered < 0) { scales[i] = 1.0; continue; }
            var d = i - hovered;
            scales[i] = 1.0 + (maxScale - 1.0) * Math.Exp(-(d * d) / (sigma * sigma));
        }
        return scales;
    }

    public static double[] ComputeOffsets(double[] scales, double iconSize, double spacing)
    {
        var offsets = new double[scales.Length];
        var acc = 0.0;
        for (var i = 1; i < scales.Length; i++)
        {
            acc += (scales[i - 1] - 1.0) * iconSize / 2.0 + (scales[i] - 1.0) * iconSize / 2.0;
            offsets[i] = acc;
        }
        return offsets;
    }
}
