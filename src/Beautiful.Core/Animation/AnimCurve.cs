namespace Beautiful.Core.Animation;

/// <summary>
/// 动画缓动曲线（v2.2 §5：open=easeOutQuint / morph=easeInOutQuad / settle=easeOutQuad /
/// close=easeInQuad / genie=灯罩既有数学）。
/// <para>
/// 【为什么曲线要独立成文件】v4.3 之前每条动画各自在采样委托里写死一个 <c>GenieMath.EaseOut</c>，
/// 于是"打开太软、关闭太拖"这类问题没法统一回答：你根本不知道某条路径用的是哪条曲线。
/// 曲线是观感的语法，必须能被单独断言（t=0 → 0、t=1 → 1、单调、端点导数形态），
/// 也必须能被 AnimLab 换着试。
/// </para>
/// <para>
/// 全部为纯函数：定义域外一律钳到 [0,1]，绝不返回 NaN——渲染侧拿到 NaN 会把整块网格画没。
/// </para>
/// </summary>
public static class AnimCurve
{
    /// <summary>语义名（写进 anim-params.json 与日志，避免"第 3 条曲线"这种指代）。</summary>
    public static class Names
    {
        public const string Open = "easeOutQuint";
        public const string Morph = "easeInOutQuad";
        public const string Settle = "easeOutQuad";
        public const string Close = "easeInQuad";
        public const string Genie = "lamp";
    }

    /// <summary>打开：easeOutQuint。起步极快、尾巴极缓——"落位"该有的手感，也是唯一能在 200ms 里显得不仓促的曲线。</summary>
    public static double EaseOutQuint(double t)
    {
        var x = Clamp01(t);
        var inv = 1.0 - x;
        return 1.0 - inv * inv * inv * inv * inv;
    }

    /// <summary>形变：easeInOutQuad。两头慢中间快，适合"从旧矩形长到新矩形"这种有起点终点的位移。</summary>
    public static double EaseInOutQuad(double t)
    {
        var x = Clamp01(t);
        return x < 0.5 ? 2.0 * x * x : 1.0 - Math.Pow(-2.0 * x + 2.0, 2.0) / 2.0;
    }

    /// <summary>补偿：easeOutQuad。系统已经把窗口摆好了，我们只负责把痕迹擦掉——所以只有"减速"。</summary>
    public static double EaseOutQuad(double t)
    {
        var x = Clamp01(t);
        return 1.0 - (1.0 - x) * (1.0 - x);
    }

    /// <summary>关闭：easeInQuad。先慢后快，末尾才有"离开"的加速度，不会像被抽走。</summary>
    public static double EaseInQuad(double t)
    {
        var x = Clamp01(t);
        return x * x;
    }

    /// <summary>按名字取曲线；未知名字回落到恒等（宁可线性，也不要在运行期抛）。</summary>
    public static Func<double, double> ByName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "easeoutquint" or "open" => EaseOutQuint,
        "easeinoutquad" or "morph" => EaseInOutQuad,
        "easeoutquad" or "settle" => EaseOutQuad,
        "easeinquad" or "close" => EaseInQuad,
        _ => Linear,
    };

    /// <summary>恒等（genie 的灯罩数学自带节奏，不再叠曲线；兜底也用它）。</summary>
    public static double Linear(double t) => Clamp01(t);

    /// <summary>
    /// 归一化进度：<paramref name="elapsedMs"/> / <paramref name="durationMs"/>，钳到 [0,1]。
    /// 时长为 0 或负数时直接返回 1（"立即完成"），绝不产生除零。
    /// </summary>
    public static double Progress(double elapsedMs, double durationMs)
    {
        if (double.IsNaN(elapsedMs) || double.IsNaN(durationMs)) return 1.0;
        if (durationMs <= 0) return 1.0;
        return Clamp01(elapsedMs / durationMs);
    }

    public static double Clamp01(double v)
    {
        if (double.IsNaN(v)) return 0.0;
        return v <= 0 ? 0.0 : v >= 1 ? 1.0 : v;
    }

    public static double Lerp(double from, double to, double t) => from + (to - from) * Clamp01(t);
}
