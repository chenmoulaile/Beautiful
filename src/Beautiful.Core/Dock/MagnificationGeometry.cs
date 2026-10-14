namespace Beautiful.Core.Dock;

/// <summary>
/// 放大行的逐帧水平几何（纯函数、**调用方提供缓冲区**，一次调用零分配）。
/// <para>
/// 条目容器的 <c>RenderTransformOrigin = (0.5, 1)</c>：缩放是绕**自身中心**发生的，
/// 所以"布局矩形"（<c>Canvas.Left</c> 写进去的那个）比真实像素矩形宽出 <c>extra_i/2</c>
/// （<c>extra_i = (scale_i - 1)·width_i</c>）。若把布局左缘直接当成未放大槽位左缘 + 前缀和，
/// 画出来的相邻间距就变成 <c>spacing + (extra_i - extra_{i+1})/2</c>：上升沿挤压、下降沿撑开，
/// 首项还会溢出底板（评审发现：图标间距不匀、整行在自己的背板里偏心）。
/// </para>
/// <para>
/// 本函数把两个坐标一次算清，两者都必须由同一处给出，避免 App 侧再各写一份偏移账：
/// <list type="bullet">
/// <item><description><c>drawn_i = base_i + Σ_{k&lt;i} extra_k - shift</c>：**真实像素**左缘，
/// 相邻间距恒等于原 spacing；命中测试/媒体面板定位用它。</description></item>
/// <item><description><c>layout_i = drawn_i + extra_i / 2</c>：写给 <c>Canvas.Left</c> 的布局左缘，
/// 绕中心缩放 extra_i/2 后正好落回 drawn_i。</description></item>
/// </list>
/// </para>
/// <para>
/// 隐藏槽（width = 0）的 extra 恒为 0，因此不会在前缀和里留空隙，也不需要特判。
/// </para>
/// </summary>
public static class MagnificationGeometry
{
    /// <summary>
    /// 计算逐项的绘制左缘与布局左缘，写入调用方缓冲区。
    /// </summary>
    /// <param name="baseLefts">未放大左缘（升序，已含 spacing）。</param>
    /// <param name="widths">未放大宽（隐藏槽传 0）。</param>
    /// <param name="scales">**本帧（阻尼后）**缩放系数；传目标值会让几何与像素脱节。</param>
    /// <param name="centerRow">true = 面板宽固定，整行以自身中心为轴外扩（左移总增量的一半）；
    /// false = 面板宽随行生长（左对齐即整体居中，父级把面板居中摆放）。</param>
    /// <param name="drawnLefts">输出：真实像素左缘（长度须 &gt;= baseLefts.Count）。</param>
    /// <param name="layoutLefts">输出：Canvas.Left 写入值（长度须 &gt;= baseLefts.Count）。</param>
    /// <returns>本帧总额外宽度 Σ (scale_i - 1)·width_i（底板宽度跟随用）。</returns>
    /// <exception cref="ArgumentException">输入或输出缓冲区长度不足。</exception>
    public static double ComputeRowLefts(
        IReadOnlyList<double> baseLefts,
        IReadOnlyList<double> widths,
        IReadOnlyList<double> scales,
        bool centerRow,
        double[] drawnLefts,
        double[] layoutLefts)
    {
        var count = baseLefts.Count;
        if (widths.Count < count) throw new ArgumentException($"widths 长度 {widths.Count} < {count}", nameof(widths));
        if (scales.Count < count) throw new ArgumentException($"scales 长度 {scales.Count} < {count}", nameof(scales));
        if (drawnLefts.Length < count) throw new ArgumentException($"drawnLefts 长度 {drawnLefts.Length} < {count}", nameof(drawnLefts));
        if (layoutLefts.Length < count) throw new ArgumentException($"layoutLefts 长度 {layoutLefts.Length} < {count}", nameof(layoutLefts));

        var totalExtra = 0.0;
        for (var i = 0; i < count; i++) totalExtra += (scales[i] - 1.0) * widths[i];
        var shift = centerRow ? totalExtra / 2.0 : 0.0;

        var prefix = 0.0;
        for (var i = 0; i < count; i++)
        {
            var extra = (scales[i] - 1.0) * widths[i];
            var drawn = baseLefts[i] + prefix - shift;
            drawnLefts[i] = drawn;
            layoutLefts[i] = drawn + extra / 2.0;   // 缩放原点 0.5 → 布局要往外让出半个增量，像素才落在 drawn
            prefix += extra;
        }
        return totalExtra;
    }
}
