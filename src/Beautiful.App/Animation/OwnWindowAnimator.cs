using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Beautiful.App.Services;
using Beautiful.Core.Animation;

namespace Beautiful.App.Animation;

/// <summary>
/// 自家窗口的关闭动画（v2.2 §4.4"自家窗口一律真动画"的落点）。
/// <para>
/// 【为什么自成一类】别人的窗口销毁后我们只能拿缓存帧事后补偿（<c>MinimizeAnimator.PlayClose</c>）；
/// 设置/关于本机这些窗口的循环在我们手里，可以<b>真动画</b>：先按 macOS 语义把内容收拢
/// （fade + scale→92%，easeInQuad，几何直接复用 Core 的 <see cref="CloseAnimMath"/>），
/// 播完再调真正的 <see cref="Window.Close"/>——不存在"事后猜测画面"的问题。
/// </para>
/// <para>
/// 【为什么动 Content 而不动 HWND】WPF 普通窗口带系统边框，对 HWND 做 scale 需要重设窗口矩形
/// 并与 DWM 搏斗；对 Content 做 RenderTransform + Opacity 是纯合成树操作，零风险、零闪烁，
/// 且视觉上与 macOS 的"窗口内容收拢消失"一致。
/// </para>
/// </summary>
public static class OwnWindowAnimator
{
    /// <summary>正在收拢的窗口（防止重复触发把动画播成两份）。窗口关闭后引用由本表释放。</summary>
    private static readonly HashSet<Window> InFlight = new();

    /// <summary>
    /// 带退场动画地关闭窗口：内容 fade + scale→92%（150ms easeInQuad，读 <see cref="AnimParams"/>），
    /// 播完真正 Close。再次调用（动画中）幂等；无内容/已关窗直接走原路径。
    /// </summary>
    public static void CloseAnimated(Window window)
    {
        if (window.Content is not UIElement root || !window.IsLoaded)
        {
            window.Close();
            return;
        }
        if (!InFlight.Add(window)) return;   // 已在收拢：重复的 Close 请求不再重播

        var p = AnimParamsStore.Current;
        var duration = p.Scaled(p.CloseDurationMs);
        var endScale = p.CloseEndScale;

        var group = new TransformGroup();
        var scale = new ScaleTransform(1.0, 1.0);
        group.Children.Add(scale);
        root.RenderTransform = group;
        root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);   // 从中心收拢（与 Core 归一化矩形同语义）

        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            try
            {
                var t = AnimCurve.Progress(clock.Elapsed.TotalMilliseconds, duration);
                var f = CloseAnimMath.Frame(t, endScale);
                root.Opacity = f.Opacity;
                scale.ScaleX = scale.ScaleY = f.Rect.Width;
                if (t < 1.0) return;
                CompositionTarget.Rendering -= tick;
                InFlight.Remove(window);
                window.Close();   // 触发窗口自己的 OnClosing → base 关闭链（保存设置等照常执行）
            }
            catch
            {
                // 收拢动画是锦上添花：任何异常都直接落地关闭，绝不挡住窗口真正该关的事实
                CompositionTarget.Rendering -= tick;
                InFlight.Remove(window);
                try { window.Close(); } catch { /* 已关 */ }
            }
        };
        CompositionTarget.Rendering += tick;
    }
}
