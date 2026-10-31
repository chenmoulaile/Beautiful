namespace Beautiful.Core.Animation;

/// <summary>
/// 帧时间序列的统计摘要（AnimLab 帧时间图顶部条：均值 / P95 / 最大 / 超预算占比）。
/// <para>
/// 纯函数：输入逐帧毫秒数（CompositionTarget.Rendering 的差分），输出可直接写入 UI 的
/// 不可变结果。非法样本（非正数 / NaN）直接丢弃——首帧差分、窗口挂起恢复都会产生 0 或
/// 异常值，混入会把均值拉到不可信的程度。
/// </para>
/// </summary>
public static class FrameTimeStats
{
    /// <summary>预算线（16.7ms = 60fps）；超线帧计入 <see cref="Summary.BadRatio"/>。</summary>
    public const double DefaultBudgetMs = 16.7;

    /// <param name="Count">有效样本数（丢弃非法值之后）。</param>
    /// <param name="AverageMs">均值。</param>
    /// <param name="P95Ms">P95（最近秩法 ceil(0.95*n)-1，与常规 profiler 口径一致）。</param>
    /// <param name="MaxMs">最大帧。</param>
    /// <param name="BadRatio">超出预算线的帧占比（0~1；样本为 0 时为 0）。</param>
    public sealed record Summary(int Count, double AverageMs, double P95Ms, double MaxMs, double BadRatio);

    public static Summary Compute(IReadOnlyList<double> frameMs, double budgetMs = DefaultBudgetMs)
    {
        var valid = new List<double>(frameMs?.Count ?? 0);
        if (frameMs is not null)
        {
            foreach (var v in frameMs)
            {
                // 非正数与 NaN 都不是"一帧的耗时"：0 是同 vsync 双回调，负数/NaN 是时钟异常。
                if (double.IsNaN(v) || v <= 0) continue;
                valid.Add(v);
            }
        }

        if (valid.Count == 0)
            return new Summary(0, 0, 0, 0, 0);

        valid.Sort();
        var count = valid.Count;
        var sum = 0.0;
        foreach (var v in valid) sum += v;

        var p95Index = (int)Math.Ceiling(0.95 * count) - 1;
        if (p95Index < 0) p95Index = 0;
        if (p95Index >= count) p95Index = count - 1;

        var budget = budgetMs <= 0 ? DefaultBudgetMs : budgetMs;
        var bad = 0;
        foreach (var v in valid)
            if (v > budget) bad++;

        return new Summary(
            count,
            sum / count,
            valid[p95Index],
            valid[count - 1],
            (double)bad / count);
    }
}
