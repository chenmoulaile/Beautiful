using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WpfAnimation = System.Windows.Media.Animation;
using Application = System.Windows.Application;   // UseWindowsForms 隐式 using 消歧
using Beautiful.App.Services;
using Beautiful.Core.Animation;

namespace Beautiful.App.Animation;

/// <summary>
/// 自家内容窗口的关闭退场（v2.2 §4 关闭项："自家窗口一律真动画"）。
/// <para>
/// 【与第三方关闭动画的区别】第三方窗口销毁后 hwnd 失效，只能拿缓存帧演"遗照退场"
/// （<see cref="MinimizeAnimator.PlayClose"/>，默认关）；自家窗口活着、画面随时可得，
/// 演的是<b>真窗口本体</b>的 fade+scale——所以始终开启，不受 WindowCloseAnimation 设置控制，
/// 也不经过替身：直接对窗口做 Opacity 1→0 + 缩放 1→92% 的 WPF 动画，播完再真 Close。
/// </para>
/// <para>
/// 【fail-open 红线】动画是尽力而为的东西：任何异常/关停中/参数不可读，立刻放行真关闭。
/// 窗口绝不能因为"想演一段 150ms 的退场"而关不掉。
/// </para>
/// <para>
/// 【用法】窗口的 <c>OnClosing</c> 里：<c>if (WindowCloseFx.TryBegin(this)) e.Cancel = true;</c>
/// ——收尾时回调 <c>Close()</c> 重入 OnClosing，此时窗口已登记在表内，TryBegin 返回 false，
/// 真关闭照常走完。<b>登记永不撤销</b>（窗口一生只演一次；ConditionalWeakTable 以窗口为键，
/// 窗口回收登记随之回收，无泄漏）。
/// </para>
/// </summary>
internal static class WindowCloseFx
{
    /// <summary>已接管过关闭的窗口（在演或演毕）。以窗口为键的弱表：窗口销毁，登记随之消失。</summary>
    private static readonly ConditionalWeakTable<Window, object> Handled = new();

    /// <summary>
    /// 尝试接管一次关闭。true = 已开始演退场（调用方必须 e.Cancel = true）；
    /// false = 不演（演过/在演/关停中/异常），调用方照常放行真关闭。
    /// </summary>
    public static bool TryBegin(Window? window)
    {
        if (window is null || !window.IsLoaded) return false;

        lock (Handled)
        {
            if (Handled.TryGetValue(window, out _)) return false;   // 演过/在演：放行真关闭
            Handled.Add(window, new object());
        }

        try
        {
            // 退出/关停期间不演：给 Application.Shutdown 的窗口清理链路加动画税是事故
            var app = Application.Current;
            if (Environment.HasShutdownStarted || app is null || app.Dispatcher.HasShutdownStarted)
                return false;

            var p = AnimParamsStore.Current;
            var duration = Math.Max(1, p.Scaled(p.CloseDurationMs));
            var endScale = Math.Clamp(p.CloseEndScale, 0.5, 1.0);

            // 收尾只做一次（Completed 与保险计时器可能双发）；真关闭后重入 OnClosing 时
            // 窗口已在表内，TryBegin 返回 false，关闭链正常走完
            var finished = false;
            void Finish()
            {
                if (finished) return;
                finished = true;
                if (window.IsLoaded)
                {
                    try { window.Close(); } catch { /* 关不掉的窗口不因动画而更坏 */ }
                }
            }

            // 缩放：只在没有既有 RenderTransform 的窗口上加（自家内容窗口都不用）；
            // 有就只做淡出——覆写别人的变换属于埋雷，不值得为 8% 的缩放冒这个险。
            var existing = window.RenderTransform;
            if (existing is null || existing == Transform.Identity
                || (existing is TransformGroup g && g.Value.IsIdentity))
            {
                var scale = new ScaleTransform(1.0, 1.0);
                window.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                window.RenderTransform = scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Fade(1.0, endScale, duration));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Fade(1.0, endScale, duration));
            }

            window.IsHitTestVisible = false;   // 演出期间不接输入：点了也是白点
            window.BeginAnimation(UIElement.OpacityProperty,
                Fade(window.Opacity, 0.0, duration, (_, _) => Finish()));

            // 收尾保险：Completed 因渲染停摆没来的极端场合，120% 时长后强制真关闭
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = System.TimeSpan.FromMilliseconds(duration * 1.2 + 60),
            };
            t.Tick += (_, _) => { t.Stop(); Finish(); };
            t.Start();

            return true;
        }
        catch (Exception)
        {
            return false;   // 动画没演成：不取消关闭，让窗口正常走
        }
    }

    /// <summary>easeInQuad 的 DoubleAnimation：开头平缓、消失干脆（与 Core CloseAnimMath 同一曲线）。</summary>
    private static WpfAnimation.DoubleAnimation Fade(double from, double to, int durationMs,
        EventHandler? completed = null)
    {
        var a = new WpfAnimation.DoubleAnimation(from, to, System.TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new WpfAnimation.QuadraticEase { EasingMode = WpfAnimation.EasingMode.EaseIn },
            FillBehavior = WpfAnimation.FillBehavior.HoldEnd,   // 终态保持：透明窗口在 Close 前不能闪回
        };
        if (completed is not null) a.Completed += completed;
        return a;
    }
}
