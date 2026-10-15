using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Services;
using Brush = System.Windows.Media.Brush; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brush 冲突
using Brushes = System.Windows.Media.Brushes; // 同上，System.Drawing.Brushes

namespace Beautiful.App.MediaWidget;

/// <summary>
/// 歌词展开面板：跟随胶囊上方居中的独立分层窗（位置由 DockWindow 在 Show 前计算）。
/// 失焦自动 Hide（Deactivated → Hide，开关 = 设置项 HidePanelOnDeactivate，经 VM 标志消费）；
/// 进度条交互用 ValueChanged 差值检测 + 防抖（见构造函数注释），SeekAsync 首次失败 →
/// IsSeekable=false 隐藏进度条；歌词 CurrentLyricIndex 变化 → ScrollIntoView（VM 事件驱动）。
/// <para>
/// 【形态（v3 T2/T3）】<see cref="AcrylicWindow"/> 分层窗口：20px 圆角 + 1px 亮边自绘，
/// 圆角缺口靠 <c>Background={x:Null}</c> 真透明（也真点击穿透）。玻璃感来自
/// <see cref="RefreshBackdrop"/> 的抓屏自绘模糊，与 DWM 材质无关；抓不到就回落 <c>PanelFillSolid</c>，
/// 两条分支都把整张卡铺满（分层窗口上"没画到"= 看穿到桌面的洞）。
/// 主题色不再需要重建实例：底色遮罩与 tint 全走 DynamicResource，换主题即时变色。
/// </para>
/// </summary>
public partial class MediaPanel
{
    private readonly MediaViewModel _vm;
    private DispatcherTimer? _seekDebounce; // seek 防抖：ValueChanged 风暴结束后统一结算一次

    public MediaPanel(MediaViewModel vm)
    {
        // 形态由基类 AcrylicWindow 决定（分层窗口），这里不再按主题开关 AllowsTransparency：
        // v3 的模糊来自抓屏自绘，与主题 BlurEnabled 无关，窗口形态因此是恒定的（少一个易错分支）。
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _vm.CurrentLyricIndexChanged += OnLyricIndexChanged;
        Closed += (_, _) =>
        {
            _vm.CurrentLyricIndexChanged -= OnLyricIndexChanged; // 防 VM 侧泄漏
            _seekDebounce?.Stop();                               // 拖动中关闭：不再结算 seek
            _vm.CancelSeek();                                    // 恢复定时器/时间线的进度回写
        };
        Deactivated += OnDeactivatedHide;

        // 进度条交互：不挂鼠标事件（MouseLeftButton* 是 Direct 路由策略事件，Thumb 捕获并标记
        // Handled 后不可靠），改用 ValueChanged 差值检测用户 seek：与 VM 当前值偏差大的变化
        // 必然来自用户（点击跳转/拖动），绑定回写与定时器推进的回声差值≈0；防抖 300ms 结算。
        Progress.ValueChanged += OnProgressValueChanged;
        _seekDebounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(300) };
        _seekDebounce.Tick += OnSeekDebounceTick;
    }

    /// <summary>
    /// 取一次抓屏背板并铺到面板底（调用方：<c>DockWindow</c> 在 <c>PositionMediaPanel</c> 之后、
    /// <c>Show</c> 之前）。
    /// <para>
    /// 【为什么必须由调用方在 Show 前触发】本面板是缓存实例（失焦 Hide 不 Close），每次展开位置都可能
    /// 随 Dock 移动；而抓屏必须在窗口还不可见时进行——显示之后再抓就把面板自己的像素抓进背板（鬼影）。
    /// Show 之后再抓也不可行（要么闪一帧隐藏，要么永远拿到自己的旧画面）。
    /// </para>
    /// <para>
    /// 面板尺寸恒为 420×520（XAML），因此用一次性冻结画刷（<see cref="ScreenBlurBackdrop.ForWindow"/>）；
    /// 拿不到背板（抓屏失败 / <c>BEAUTIFUL_NOBLUR=1</c>）→ 回落 XAML 里的实心渐变，绝不留透明洞。
    /// </para>
    /// </summary>
    public void RefreshBackdrop()
    {
        try
        {
            var blur = ScreenBlurBackdrop.ForWindow(this, new Rect(0, 0, Width, Height));
            PanelRoot.Background = blur ?? (Brush)Resources["PanelFillSolid"];
            // Scrim 铺**深色遮罩** PanelFillBlur 而非模糊画刷本身（同 FluentMenu 的两层材质）：
            // 模糊直接赋给 Scrim 就没人压暗卡片，浮在亮背景上会被冲成浅灰、歌词对比度丢失。
            // 实心底分支保持透明——PanelFillSolid 本身已是深色，再叠一层只会闷。
            Scrim.Background = blur is not null ? (Brush)Resources["PanelFillBlur"] : Brushes.Transparent;
        }
        catch (Exception ex)
        {
            // 背板是装饰层：任何异常都不该让"展开歌词面板"这件事失败。实心底已经铺在 XAML 里。
            Logger.Warn($"媒体面板背板失败（改用实心底）: {ex.Message}");
        }
    }

    private void OnProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm is null || _seekDebounce is null) return;
        if (Math.Abs(e.NewValue - _vm.PositionSeconds) < 0.75) return; // 回声（绑定回写/定时器推进）
        _vm.BeginSeek();                                              // 挂起 VM→Slider 回写，避免拖动被覆盖
        _seekDebounce.Stop();
        _seekDebounce.Start();
    }

    private void OnSeekDebounceTick(object? sender, EventArgs e)
    {
        _seekDebounce?.Stop();
        _ = _vm.SeekToAsync(TimeSpan.FromSeconds(Progress.Value));
    }

    private void OnDeactivatedHide(object? sender, EventArgs e)
    {
        if (_vm.PanelAutoHideOnDeactivate) Hide(); // 设置项（Task 16）：失焦收起可关
    }

    private void OnLyricIndexChanged(int index)
    {
        if ((uint)index < (uint)_vm.Lyrics.Count)
            LyricsList.ScrollIntoView(_vm.Lyrics[index]);
    }

    /// <summary>面板展开时对齐到当前歌词行（DockWindow 在 Show 后调用；等一拍布局再滚动）。</summary>
    public void ScrollToCurrentLine()
        => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if ((uint)_vm.CurrentLyricIndex < (uint)_vm.Lyrics.Count)
                LyricsList.ScrollIntoView(_vm.Lyrics[_vm.CurrentLyricIndex]);
        }));
}
