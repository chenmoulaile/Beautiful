namespace Beautiful.Core.Dock;

/// <summary>
/// 帧率无关动画数学（纯函数、无 UI 依赖）。
/// 组合用法：渲染循环每帧用 <see cref="Damp"/> 逼近 <see cref="MagnificationField.Compute"/>
/// 得到的目标 scale/offset，用 <see cref="CloseEnough"/> 判定收敛后停止循环；
/// 启动弹跳等一次性缓动用 <see cref="ElasticOut"/>。
/// </summary>
public static class AnimationMath
{
    /// <summary>
    /// 指数阻尼：<c>target + (current - target)·e^(-lambda·dt)</c>。
    /// 与固定步长 lerp 不同，大步长与小步长多次迭代结果等价（半群性质），
    /// 因此动画速度不随帧率变化。lambda 为每秒收敛速率（约 14 适合 dock 放大），dt 为秒。
    /// </summary>
    public static double Damp(double current, double target, double lambda, double dtSeconds)
        => target + (current - target) * Math.Exp(-lambda * dtSeconds);

    /// <summary>
    /// 弹性缓出（ease-out elastic，经典 easings.net 公式）：
    /// <c>2^(-10t)·sin((10t - 0.75)·2π/3) + 1</c>，端点精确钳制：t&lt;=0 返回 0，t&gt;=1 返回 1。
    /// 用于启动/图标弹出的弹跳效果。
    /// </summary>
    public static double ElasticOut(double t)
    {
        const double c4 = (2.0 * Math.PI) / 3.0;
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return Math.Pow(2.0, -10.0 * t) * Math.Sin((10.0 * t - 0.75) * c4) + 1.0;
    }

    /// <summary>收敛判定：|a - b| &lt;= <paramref name="eps"/>（默认 1e-4），用于停止动画循环。</summary>
    public static bool CloseEnough(double a, double b, double eps = 1e-4)
        => Math.Abs(a - b) <= eps;
}
