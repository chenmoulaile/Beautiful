using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl; // UseWPF+UseWindowsForms 并用 → Forms 同名类型二义（CS0104）

namespace Beautiful.App.MediaWidget;

/// <summary>
/// 媒体胶囊（鸿蒙"实况窗"样式，嵌入 Dock 的 Media 条目）。
/// "Artist - Title" 超出视口时 TranslateTransform.X 往复滚动（~24px/s，首帧停留 1.2s），
/// 未溢出则静态；右侧播放/暂停按钮（MouseUp 就地吞掉，不冒泡到 Dock 的条目点击 → 不会误开面板）。
/// 可见性由 DataContext（MediaViewModel）.HasSession 驱动。
/// </summary>
public partial class MediaCapsule : UserControl
{
    public MediaCapsule()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateMarquee();
        Unloaded += (_, _) => StopMarquee(); // 离开可视树即停动画，防泄漏
        MarqueeText.SizeChanged += (_, _) => UpdateMarquee();
        MarqueeHost.SizeChanged += (_, _) => UpdateMarquee();
        // 文本等宽变化时 SizeChanged 不触发：借绑定 TargetUpdated 通知后等一拍布局再评估
        MarqueeText.TargetUpdated += (_, _) => Dispatcher.BeginInvoke(UpdateMarquee, DispatcherPriority.Loaded);
        // 双保险：按钮的 MouseUp 一律标记已处理（ButtonBase 行为之外再兜底），
        // 防止冒泡到 DockSurface 的条目左键命中（OnMouseLeftButtonUp → Activated）触发"点击条目 → 展开面板"
        PlayPauseButton.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
    }

    /// <summary>溢出才滚动；先停旧动画再按新溢出量重建，避免叠加。</summary>
    private void UpdateMarquee()
    {
        if (!IsLoaded) return;
        var overflow = MarqueeText.ActualWidth - MarqueeHost.ActualWidth;
        MarqueeTf.BeginAnimation(TranslateTransform.XProperty, null);
        if (overflow <= 2) return; // 未溢出 → 静态
        var duration = TimeSpan.FromSeconds(Math.Max(4.0, overflow / 24.0));
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, -overflow, duration)
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(1.2)
        };
        MarqueeTf.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopMarquee() => MarqueeTf.BeginAnimation(TranslateTransform.XProperty, null);
}
