namespace Beautiful.Core.Dock;

/// <summary>
/// 连续放大场一次计算的纯结果（均为 DIP 坐标）。
/// </summary>
/// <param name="Scales">每项缩放系数（&gt;= 1，上限 maxScale）。</param>
/// <param name="Offsets">每项左边缘相对原始排布的位移（centered=true 时整体向左平移）。</param>
/// <param name="TotalWidth">放大后的 dock 总宽度：Σ(scale_i·baseSize) + (n-1)·spacing。</param>
public readonly record struct MagnificationResult(double[] Scales, double[] Offsets, double TotalWidth);

/// <summary>
/// macOS Dock 风格连续高斯放大场（纯函数、无 UI 依赖，放 Core 以便单测）。
/// scale 由**连续光标坐标**决定而非离散悬停索引，从根本上消除逐索引跳变导致的卡顿：
/// <c>scale_i = 1 + (maxScale - 1) · exp(-(cursorX - center_i)² / (2σ²))</c>。
/// σ（sigmaDip）以 DIP 为单位，描述放大影响半径；lambda 动画收敛见 <see cref="AnimationMath"/>。
/// </summary>
public static class MagnificationField
{
    /// <summary>
    /// 计算某一光标位置下每项的 scale 与左偏移。
    /// </summary>
    /// <param name="cursorX">光标 X（DIP，dock 本地坐标，可为任意实数——连续值）。</param>
    /// <param name="baseCenters">未放大时每项中心的 X 坐标（DIP，升序）。</param>
    /// <param name="baseSize">未放大项宽（正方形图标边长，DIP）。</param>
    /// <param name="spacing">项间距（DIP）。</param>
    /// <param name="maxScale">被直接指向项的最大倍率（&gt;= 1；= 1 等价于关闭放大）。</param>
    /// <param name="sigmaDip">高斯衰减半径（DIP），必须为正。</param>
    /// <param name="centered">true 时整体左移额外宽度的一半，使视觉中心在放大过程中保持稳定。</param>
    /// <exception cref="ArgumentOutOfRangeException">sigmaDip &lt;= 0。</exception>
    public static MagnificationResult Compute(
        double cursorX,
        IReadOnlyList<double> baseCenters,
        double baseSize,
        double spacing,
        double maxScale,
        double sigmaDip,
        bool centered)
    {
        if (sigmaDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaDip), sigmaDip, "sigma 必须为正的 DIP 半径");

        var n = baseCenters.Count;
        var scales = new double[n];
        if (n == 0)
            return new MagnificationResult(scales, new double[0], 0.0);

        FillScales(cursorX, baseCenters, maxScale, sigmaDip, scales);

        var offsets = new double[n];
        // offset_i = Σ_{k<i} (scale_k - 1)·baseSize：单次前缀和，O(n)；额外总宽度即累加终值 acc。
        var acc = 0.0;
        for (var i = 0; i < n; i++)
        {
            offsets[i] = acc;
            acc += (scales[i] - 1.0) * baseSize;
        }

        var totalWidth = n * baseSize + (n - 1) * spacing + acc; // ≡ Σ scale_i·baseSize + (n-1)·spacing

        if (centered)
        {
            // 视觉中心稳定：整体减去 (TotalWidth - n·baseSize - (n-1)·spacing)/2 = acc/2。
            var shift = (totalWidth - n * baseSize - (n - 1) * spacing) / 2.0;
            for (var i = 0; i < n; i++)
                offsets[i] -= shift;
        }

        return new MagnificationResult(scales, offsets, totalWidth);
    }

    /// <summary>
    /// 只算 scale、并写进**调用方提供的缓冲区**：动画循环每帧只要 Scales（ offsets/TotalWidth 由 UI 侧
    /// 按阻尼后的 scale 重新前缀和，见 <see cref="MagnificationGeometry"/>），
    /// 用这个重载可免掉每次调用两个数组分配 + 一整轮 centered 平移循环（评审发现：每帧分配）。
    /// </summary>
    /// <param name="cursorX">光标 X（DIP，未放大基准坐标系）。</param>
    /// <param name="baseCenters">未放大每项中心 X（升序），长度决定写入规模。</param>
    /// <param name="maxScale">正对光标项的最大倍率。</param>
    /// <param name="sigmaDip">高斯衰减半径（DIP），必须为正。</param>
    /// <param name="scales">输出缓冲区，长度须 &gt;= <paramref name="baseCenters"/>.Count；只写前 Count 项。</param>
    /// <exception cref="ArgumentOutOfRangeException">sigmaDip &lt;= 0。</exception>
    /// <exception cref="ArgumentException">缓冲区长度不足。</exception>
    /// <exception cref="ArgumentNullException">baseCenters 或 scales 为 null。</exception>
    public static void ComputeScalesInto(
        double cursorX,
        IReadOnlyList<double> baseCenters,
        double maxScale,
        double sigmaDip,
        double[] scales)
    {
        if (baseCenters is null) throw new ArgumentNullException(nameof(baseCenters));
        if (scales is null) throw new ArgumentNullException(nameof(scales));
        if (scales.Length < baseCenters.Count)
            throw new ArgumentException($"缓冲区长度 {scales.Length} < 项数 {baseCenters.Count}", nameof(scales));
        ComputeScalesInto(cursorX, baseCenters, baseCenters.Count, maxScale, sigmaDip, scales);
    }

    /// <summary>
    /// 同上，但**只算前 <paramref name="count"/> 个中心**：UI 侧的场缓冲区按条目数一次性预分配
    /// （<see cref="DockRowLayout"/> 只填前 count 项），长度不必每帧精确匹配，于是重排之外的逐帧路径
    /// 一个字节都不分配。
    /// </summary>
    /// <param name="cursorX">光标 X（DIP，未放大基准坐标系）。</param>
    /// <param name="baseCenters">未放大中心 X 缓冲区（升序），只看前 <paramref name="count"/> 项。</param>
    /// <param name="count">参与计算的项数（&gt; 0 且 ≤ baseCenters.Count 且 ≤ scales.Length）。</param>
    /// <param name="maxScale">正对光标项的最大倍率。</param>
    /// <param name="sigmaDip">高斯衰减半径（DIP），必须为正。</param>
    /// <param name="scales">输出缓冲区，长度须 &gt;= <paramref name="count"/>；只写前 count 项。</param>
    /// <exception cref="ArgumentOutOfRangeException">sigmaDip &lt;= 0 或 count 越出缓冲区。</exception>
    /// <exception cref="ArgumentNullException">baseCenters 或 scales 为 null。</exception>
    public static void ComputeScalesInto(
        double cursorX,
        IReadOnlyList<double> baseCenters,
        int count,
        double maxScale,
        double sigmaDip,
        double[] scales)
    {
        if (baseCenters is null) throw new ArgumentNullException(nameof(baseCenters));
        if (scales is null) throw new ArgumentNullException(nameof(scales));
        if (sigmaDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaDip), sigmaDip, "sigma 必须为正的 DIP 半径");
        if (count < 0 || count > baseCenters.Count)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"项数须落在 0..{baseCenters.Count}");
        if (scales.Length < count)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"缓冲区长度 {scales.Length} < 项数 {count}");

        FillScales(cursorX, baseCenters, count, maxScale, sigmaDip, scales);
    }

    /// <summary>高斯本体：<c>scale_i = 1 + (maxScale-1)·exp(-(cursorX-center_i)²/(2σ²))</c>（不做分配，可被两条 API 复用）。</summary>
    private static void FillScales(
        double cursorX,
        IReadOnlyList<double> baseCenters,
        double maxScale,
        double sigmaDip,
        double[] scales)
        => FillScales(cursorX, baseCenters, baseCenters.Count, maxScale, sigmaDip, scales);

    /// <summary>高斯本体（显式项数版本）：只写前 count 项，调用方缓冲区可以是容量而非精确长度。</summary>
    private static void FillScales(
        double cursorX,
        IReadOnlyList<double> baseCenters,
        int count,
        double maxScale,
        double sigmaDip,
        double[] scales)
    {
        var twoSigmaSq = 2.0 * sigmaDip * sigmaDip;
        for (var i = 0; i < count; i++)
        {
            var d = cursorX - baseCenters[i];
            scales[i] = 1.0 + (maxScale - 1.0) * Math.Exp(-(d * d) / twoSigmaSq);
        }
    }
}
