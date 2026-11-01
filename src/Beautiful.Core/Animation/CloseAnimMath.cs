using static Beautiful.Core.Animation.AnimCurve;

namespace Beautiful.Core.Animation;

/// <summary>
/// 关闭退场动画的几何（纯函数，v2.2 §5 参数表：close 150ms / 终态缩到 92%，曲线 easeInQuad）。
/// <para>
/// 【为什么关闭动画独立成数学】macOS 关窗口没有动画——这是裁定过的忠实默认（README 写明
/// "这不是没做完"）。但 Windows 用户习惯了"窗口总要有个交代"，于是 <c>CloseAnim=true</c>
/// 时给一条 150ms 的退场：窗口画面缩到 92% 同时淡出，<b>开头快收尾更快</b>（easeInQuad：
/// 起步平缓、消失干脆），与打开的 easeOutCubic（落位稳）互为镜像。退场一律快于入场，
/// 这是"东西离开"与"东西到来"的观感差——离开不该拖泥带水。
/// </para>
/// <para>
/// 【数据从哪来】第三方窗口的 DESTROY 事件到手时窗口已经销毁（hwnd 失效、抓不到帧），
/// 唯一的画面来源是前台帧缓存（见 App 侧 <c>ForegroundFrameCache</c>），几何来源是
/// "最后已知非图标矩形"表。所以本数学只负责"给进度 t 算出那一帧长什么样"，
/// 不关心帧从哪来——App 侧拿缓存帧喂给同一套 GhostLayer 管线。
/// </para>
/// <para>
/// 【与打开动画共用归一化矩形口径】缩放以目标矩形中心为原点，输出 <see cref="GenieRect"/>
/// 是相对画布的归一化坐标（X/Y/Width/Height ∈ [0,1]），与 PlayOpen 的采样器同构，
/// GhostLayer 无需知道方向。
/// </para>
/// </summary>
public static class CloseAnimMath
{
    /// <summary>一帧关闭动画：内容缩放与整体不透明度（二者同走 easeInQuad）。</summary>
    public readonly record struct CloseFrame(double Scale, double Opacity);

    /// <summary>
    /// 采样进度 t（0→1）处的缩放与不透明度。
    /// <para>t=0 → (1.0, 1.0) 原样；t=1 → (endScale, 0.0) 缩到终态、完全透明。
    /// <paramref name="endScale"/> 先钳到 [0.5, 1.0]（与 AnimParams.CloseEndScale 同区间），
    /// 非法值（NaN/Infinity）按默认 0.92 处理——配置写坏只能降级，不能倒放或放大。</para>
    /// </summary>
    public static CloseFrame Sample(double t, double endScale = 0.92)
    {
        var e = EaseInQuad(Clamp01(t));
        var end = double.IsNaN(endScale) || double.IsInfinity(endScale)
            ? 0.92
            : Math.Clamp(endScale, 0.5, 1.0);
        return new CloseFrame(
            Scale: 1.0 + (end - 1.0) * e,   // 1.0 → end
            Opacity: 1.0 - e);              // 1.0 → 0.0
    }

    /// <summary>
    /// 缩放 <paramref name="scale"/> 的<b>居中归一化矩形</b>（相对画布，0..1 口径）。
    /// <para>
    /// 【为什么与画布几何无关】GhostLayer 的条带坐标 = 归一化值 × 画布尺寸（画布即源矩形本身，
    /// 见 StartGhost），所以"在画布内居中缩到 s"的归一化表达恒为
    /// ((1-s)/2, (1-s)/2, s, s)——与 PlayOpen 采样器在 canvas==窗口时算出的数值完全一致，
    /// 只是这里把那串"除回自己宽度"的算术化简成了它的唯一解。
    /// </para>
    /// </summary>
    public static GenieRect RectFor(double scale)
    {
        var s = Math.Clamp(scale, 0.05, 1.0);
        var inset = (1.0 - s) / 2;
        return new GenieRect(inset, inset, s, s);
    }
}
