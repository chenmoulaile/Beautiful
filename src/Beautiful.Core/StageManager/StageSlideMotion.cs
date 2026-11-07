using System;
using System.Globalization;

namespace Beautiful.Core.StageManager;

/// <summary>
/// 台前调度滑动的缓动曲线（CSS cubic-bezier 的纯数学求值，零 Win32/WPF 依赖）。
/// <para>
/// 【token 的映射】主题动效 token 以 CSS 语义给曲线名（<c>MacTokens.EaseOut =
/// "cubic-bezier(0,0,0.58,1)"</c>，Core 不引 WPF，App 侧把该串解析成本结构后按帧求值）。
/// 解析失败一律回落 <see cref="Default"/>——它就是 token 的定稿值，等于"token 缺席时行为不变"。
/// </para>
/// <para>
/// 求值算法与 WebKit UnitBezier 同一族：给定参数 (x1,y1,x2,y2)，先对 x(u)=t 解出参数 u
/// （牛顿迭代 8 次，导数过小退回二分 16 次——竖直段 x1≈x2 时牛顿不收敛），再算 y(u)。
/// 全程无堆分配，动画每帧调用没有 GC 压力。
/// </para>
/// </summary>
public readonly record struct StageEase(double X1, double Y1, double X2, double Y2)
{
    /// <summary>定稿缓动 = 主题 token EaseOut 的值（cubic-bezier(0,0,0.58,1)，即 ease-out）。</summary>
    public static StageEase Default { get; } = new(0, 0, 0.58, 1);

    /// <summary>
    /// 解析 CSS 曲线串。只认 <c>cubic-bezier(x1,y1,x2,y2)</c>（x1/x2 必须落在 [0,1]，CSS 规范约束；
    /// y 无界）。其余写法（ease / ease-in-out / 未来新曲线）返回 false，调用方回落 <see cref="Default"/>。
    /// </summary>
    public static bool TryParseCurve(string? text, out StageEase ease)
    {
        ease = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        const string prefix = "cubic-bezier(";
        if (!s.StartsWith(prefix, StringComparison.Ordinal) || !s.EndsWith(")", StringComparison.Ordinal))
            return false;
        var parts = s[prefix.Length..^1].Split(',');
        if (parts.Length != 4) return false;
        double x1, y1, x2, y2;
        try
        {
            x1 = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            y1 = double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            x2 = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
            y2 = double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        if (x1 < 0 || x1 > 1 || x2 < 0 || x2 > 1) return false;
        ease = new StageEase(x1, y1, x2, y2);
        return true;
    }

    /// <summary>曲线在进度 <paramref name="t"/>（0..1，越界钳制）处的输出 y。端点恒等：0→0，1→1。</summary>
    public double Evaluate(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return SampleY(SolveU(t));
    }

    // ---------- 贝塞尔求值（ax t³ + bx t² + cx t 的系数展开，Web 端 UnitBezier 同式） ----------

    private double SampleX(double u) => ((Ax * u + Bx) * u + Cx) * u;
    private double SampleY(double u) => ((Ay * u + By) * u + Cy) * u;
    private double SampleXDerivative(double u) => (3 * Ax * u + 2 * Bx) * u + Cx;

    private double Ax => 1 - Cx - Bx;
    private double Ay => 1 - Cy - By;
    private double Cx => 3 * X1;
    private double Cy => 3 * Y1;
    private double Bx => 3 * (X2 - X1) - Cx;
    private double By => 3 * (Y2 - Y1) - Cy;

    /// <summary>解 x(u)=t：牛顿迭代，导数过小（竖直段）退回二分——两段合起来覆盖全部合法曲线。</summary>
    private double SolveU(double t)
    {
        // 牛顿 8 次：三次曲线在常规参数下 2-3 次即收敛，8 次封顶足够。
        var u = t;
        for (var i = 0; i < 8; i++)
        {
            var err = SampleX(u) - t;
            if (Math.Abs(err) < 1e-7) return u;
            var d = SampleXDerivative(u);
            if (Math.Abs(d) < 1e-6) break;   // 竖直段：切线水平，牛顿失稳 → 二分
            u -= err / d;
        }
        // 二分 16 次：x(u) 在 [0,1] 上单调（x1,x2∈[0,1] 保证），区间折半必收敛到 1e-4 以内。
        var lo = 0.0;
        var hi = 1.0;
        u = t;
        for (var i = 0; i < 16; i++)
        {
            var x = SampleX(u);
            if (Math.Abs(x - t) < 1e-7) return u;
            if (x < t) lo = u;
            else hi = u;
            u = (lo + hi) / 2;
        }
        return u;
    }
}

/// <summary>
/// 台前调度条收合/展开滑动的运动学（纯函数）：给"已进行时长 + 总时长 + 缓动"算当前进度与坐标。
/// 与 <see cref="StageEase"/> 一起构成动画的**全部数学**——App 侧只负责计时与 SetWindowPos，
/// 这里可以被单测逐点验证（端点、单调、时长边界）。
/// </summary>
public static class StageSlideMotion
{
    /// <summary>
    /// 归一进度（0..1）：elapsed 钳制到 [0,duration]；duration&lt;=0 视为已完成（返回 1）——
    /// "时长为 0"是合法退化（动画开关关、token 值异常），必须一步到位而不是除零。
    /// </summary>
    public static double Progress(int elapsedMs, int durationMs, StageEase ease)
    {
        if (durationMs <= 0) return 1;
        if (elapsedMs <= 0) return 0;
        if (elapsedMs >= durationMs) return 1;
        return ease.Evaluate(elapsedMs / (double)durationMs);
    }

    /// <summary>线性插值：progress=0 → <paramref name="from"/>，1 → <paramref name="to"/>。</summary>
    public static double Interpolate(double from, double to, double progress)
        => from + (to - from) * Math.Clamp(progress, 0, 1);
}
