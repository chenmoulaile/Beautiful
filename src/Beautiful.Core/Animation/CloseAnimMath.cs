namespace Beautiful.Core.Animation;

/// <summary>
/// 关闭动画的几何（v2.2 §4.4：fade + scale→92%，150ms，easeInQuad；<b>默认不播</b>，
/// 设置项 <c>CloseAnimation</c> 显式打开才启用——macOS 的关闭就是干脆地消失，
/// 这不是"没做完"，README 里如实写明）。
/// <para>
/// 【观感模型】退场是"收拢着离开"：先慢后快（easeInQuad），末尾才有离开的加速度，
/// 不会像被抽走。缩放与透明度共用同一条被缓动的进度——两者必须同步收紧，
/// 否则"内容已经全透明、卡片还在缩"会拆成两层运动，观感立刻变廉价。
/// </para>
/// <para>
/// 【画面来源】EVENT_OBJECT_DESTROY 到手时窗口<b>已经销毁</b>，不可能现抓；
/// 唯一的画面是前台帧缓存里替这个窗口留的那一帧（2Hz 采样）。缓存龄超过设置上限
/// （Core token <c>CloseCacheMaxAgeMs</c>，默认 1000ms，对应规格"<1000ms 才播"）
/// 就如实不播——拿"上上上个焦点窗口"的旧画面来演，比不演更假。
/// </para>
/// </summary>
public static class CloseAnimMath
{
    /// <summary>终态缩放钳制区间（与 <see cref="AnimParams"/> 对 CloseEndScale 的规范化一致）。</summary>
    public const double MinEndScale = 0.5;

    /// <summary>
    /// 归一化一帧。<paramref name="t"/> 为原始进度（0..1，可越界，内部钳制）；
    /// 矩形<b>居中</b>于源窗口（收拢朝向窗口中心，不是朝某一边塌缩）。
    /// </summary>
    public static OpenCloseFrame Frame(double t, double endScale)
    {
        var x = AnimCurve.Clamp01(t);
        var e = AnimCurve.EaseInQuad(x);
        var scale = AnimCurve.Lerp(1.0, ClampScale(endScale), e);
        return new OpenCloseFrame(Centered(scale), 1.0 - e);
    }

    /// <summary>起始帧（t=0）：与真实窗口完全重合、完全不透明。</summary>
    public static OpenCloseFrame Start(double endScale) => Frame(0.0, endScale);

    /// <summary>终帧（t=1）：缩到 endScale、完全透明，此刻关闭替身窗口。</summary>
    public static OpenCloseFrame End(double endScale) => Frame(1.0, endScale);

    /// <summary>按缩放值取居中矩形：X = Y = (1-scale)/2，宽高 = scale。</summary>
    private static GenieRect Centered(double scale)
    {
        var half = (1.0 - scale) / 2.0;
        return new GenieRect(half, half, scale, scale);
    }

    private static double ClampScale(double s)
        => double.IsNaN(s) || double.IsInfinity(s) ? 1.0 : Math.Clamp(s, MinEndScale, 1.0);
}
