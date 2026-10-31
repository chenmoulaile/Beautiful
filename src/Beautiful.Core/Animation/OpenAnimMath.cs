namespace Beautiful.Core.Animation;

/// <summary>
/// 打开动画的几何（v2.2 §4.3：in-place scale 0.94→1.0 + fade，200ms，easeOutQuint）。
/// <para>
/// 【观感模型】macOS 的窗口出现是"轻轻落位"：内容以约 94% 的尺寸浮现、快速淡入、
/// 尾巴极缓地长到全尺寸。缩放走 easeOutQuint（起步极快、尾巴极缓——唯一能在 200ms 里
/// 显得不仓促的曲线）；淡入走 easeOutQuad 且只占前段（见 <see cref="FadeShare"/>），
/// 因为"内容浮现"发生在开头，后半段该是纯粹的落位，透明度再爬坡就成了鬼影。
/// </para>
/// <para>
/// 【与占位帧的关系】打开是系统触发的路径（EVENT_OBJECT_SHOW 到手时窗口已在布局），
/// 首帧竞态由 App 侧的占位帧设计兜底：先以 0 透明度占住窗口矩形，捕获重试（8ms×12）
/// 就绪后当帧换入真画面起播。本函数在 t=0 返回<b>透明</b>的起始帧，恰好就是占位帧该有的
/// 样子——占位阶段与动画起点天然是同一帧，不需要第二套数学。
/// </para>
/// <para>
/// 全部为纯函数：NaN/越界输入一律钳回合法区间，绝不返回 NaN（渲染侧拿到 NaN 会把整块画没）。
/// </para>
/// </summary>
public static class OpenAnimMath
{
    /// <summary>
    /// 淡入占进度的比例（0.45 = 前 45% 完成透明度，后 55% 只做缩放落位）。
    /// 不是 AnimParams token：v2.2 §5 参数表没有它，属于几何常识而非口味旋钮；
    /// 所有者提出口味需求时再升级进参数表（热加载）。
    /// </summary>
    public const double FadeShare = 0.45;

    /// <summary>起始缩放钳制区间（与 <see cref="AnimParams"/> 对 OpenStartScale 的规范化一致）。</summary>
    public const double MinStartScale = 0.5;

    /// <summary>
    /// 归一化一帧。<paramref name="t"/> 为原始进度（0..1，可越界，内部钳制）；
    /// 返回的矩形<b>居中</b>于源窗口（窗口是从中心长出来的，不是从左上角锚定）。
    /// </summary>
    public static OpenCloseFrame Frame(double t, double startScale)
    {
        var x = AnimCurve.Clamp01(t);
        var scale = AnimCurve.Lerp(ClampScale(startScale), 1.0, AnimCurve.EaseOutQuint(x));
        var opacity = AnimCurve.EaseOutQuad(AnimCurve.Clamp01(FadeShare <= 0 ? 1.0 : x / FadeShare));
        return new OpenCloseFrame(Centered(scale), opacity);
    }

    /// <summary>起始帧（t=0）：占位阶段与动画起点共用。透明度恒 0，不遮挡真实窗口。</summary>
    public static OpenCloseFrame Start(double startScale) => Frame(0.0, startScale);

    /// <summary>终帧（t=1）：与真实窗口完全重合、完全不透明——此刻移除替身不会产生可见跳变。</summary>
    public static OpenCloseFrame End(double startScale) => Frame(1.0, startScale);

    /// <summary>按缩放值取居中矩形：X = Y = (1-scale)/2，宽高 = scale。</summary>
    private static GenieRect Centered(double scale)
    {
        var half = (1.0 - scale) / 2.0;
        return new GenieRect(half, half, scale, scale);
    }

    private static double ClampScale(double s)
        => double.IsNaN(s) || double.IsInfinity(s) ? 1.0 : Math.Clamp(s, MinStartScale, 1.0);
}
