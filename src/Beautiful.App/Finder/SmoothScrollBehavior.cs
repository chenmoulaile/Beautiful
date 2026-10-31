using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Beautiful.App.Finder;

/// <summary>
/// macOS 手感的平滑滚轮（alpha.18，真机反馈"访达……滑动动画等太生硬"）。
/// <para>
/// 【原生为什么生硬】WPF 滚轮一格 = ScrollViewer 一次硬步进，连滚时一格格跳，没有滑行与减速，
/// 和 macOS 的惯性滑动差距肉眼可见。本行为把滚轮事件改成【目标累积 + 缓动动画】：
/// 连续滚动时目标像素不断叠加，动画每帧从当前 offset 出发滑向目标（easeOutCubic 220ms），
/// 停手后自然减速停稳——正是 macOS 的"滑行→停稳"手感。
/// </para>
/// <para>
/// 【接线】宿主可以是 ScrollViewer 本体（分栏/画廊视图），也可以是任何 ItemsControl
/// （列表/图标/侧栏——内部 ScrollViewer 运行期查找）。虚拟化安全：列表视图改用
/// <c>VirtualizingPanel.ScrollUnit="Pixel"</c>（像素粒度 + 虚拟化并存），不关 CanContentScroll。
/// </para>
/// <para>
/// 【绝不添乱】找不到内部 ScrollViewer（模板尚未生成等）或没有可滚内容时，
/// 直接放行原生滚轮行为（包括父级滚动）。
/// </para>
/// </summary>
public static class SmoothScrollBehavior
{
    /// <summary>滚轮一格的滑行像素（e.Delta=120 → 约 86px）：macOS 量级，比 WPF 默认的"3 行"更顺。</summary>
    private const double PixelsPerDelta = 0.72;

    /// <summary>单次滑行动画时长：连续滚动时不断重启，观感是连续滑行。</summary>
    private const int GlideMs = 220;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    /// <summary>每宿主滑行状态（当前累积目标）：attached property 生命周期随宿主，无需手工清理。</summary>
    private static readonly DependencyProperty GlideProperty = DependencyProperty.RegisterAttached(
        "Glide", typeof(GlideState), typeof(SmoothScrollBehavior), new PropertyMetadata(null));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            if ((bool)e.NewValue) sv.PreviewMouseWheel += OnPreviewMouseWheel;
            else sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            return;
        }

        if (d is ItemsControl control)
        {
            if ((bool)e.NewValue) control.PreviewMouseWheel += OnPreviewMouseWheel;
            else control.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = sender as ScrollViewer ?? FindScrollViewer(sender as DependencyObject);
        if (sv is null) return;

        // 主方向判定：垂直可滚 → 垂直滑行；只有横向可滚（分栏视图等）→ 横向滑行；
        // 两者都没有 → 放行原生行为（含父级滚动）。
        var horizontal = sv.ScrollableHeight <= 0.5 && sv.ScrollableWidth > 0.5;
        if (!horizontal && sv.ScrollableHeight <= 0.5) return;

        e.Handled = true;

        var state = (GlideState?)sv.GetValue(GlideProperty);
        if (state is null)
        {
            state = new GlideState();
            sv.SetValue(GlideProperty, state);
        }

        var delta = -e.Delta * PixelsPerDelta;
        var glide = new DoubleAnimation
        {
            Duration = TimeSpan.FromMilliseconds(GlideMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (horizontal)
        {
            var from = sv.HorizontalOffset;
            // 从"当前动画目标"而不是当前 offset 累积：连滚时目标不断前移，滑行连续不顿挫。
            var basePoint = state.ActiveH ? state.TargetH : from;
            state.TargetH = Math.Clamp(basePoint + delta, 0, sv.ScrollableWidth);
            state.ActiveH = true;
            glide.From = from;
            glide.To = state.TargetH;
            // ScrollViewer.HorizontalOffset 只读，但 WPF 动画走 SetHorizontalOffset 通道——社区标准做法。
            sv.BeginAnimation(ScrollViewer.HorizontalOffsetProperty, glide);
        }
        else
        {
            var from = sv.VerticalOffset;
            var basePoint = state.ActiveV ? state.TargetV : from;
            state.TargetV = Math.Clamp(basePoint + delta, 0, sv.ScrollableHeight);
            state.ActiveV = true;
            glide.From = from;
            glide.To = state.TargetV;
            // ScrollViewer.VerticalOffset 只读，但 WPF 动画走 SetVerticalOffset 通道——社区标准做法。
            sv.BeginAnimation(ScrollViewer.VerticalOffsetProperty, glide);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer found) return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var hit = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }

        return null;
    }

    private sealed class GlideState
    {
        public double TargetV;
        public bool ActiveV;
        public double TargetH;
        public bool ActiveH;
    }
}
