using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brush 冲突

namespace Beautiful.App.MediaWidget;

/// <summary>
/// 歌词展开面板：跟随胶囊上方居中的独立亚克力窗（位置由 DockWindow 在 Show 前计算）。
/// 失焦自动 Hide（Deactivated → Hide，开关 = 设置项 HidePanelOnDeactivate，经 VM 标志消费）；
/// 进度条交互用 ValueChanged 差值检测 + 防抖（见构造函数注释），SeekAsync 首次失败 →
/// IsSeekable=false 隐藏进度条；歌词 CurrentLyricIndex 变化 → ScrollIntoView（VM 事件驱动）。
/// </summary>
public partial class MediaPanel
{
    private readonly MediaViewModel _vm;
    private DispatcherTimer? _seekDebounce; // seek 防抖：ValueChanged 风暴结束后统一结算一次

    public MediaPanel(MediaViewModel vm)
    {
        // 【Task 4 形态选择】面板内容铺满整个窗口（无逐像素透明留白）→ 启用模糊的主题一律走
        // "非分层 + Win11 系统背板"（真模糊 + DWM 原生圆角）；只有主题禁模糊才保留分层自绘圆角。
        // 必须在 InitializeComponent 之前设定：AllowsTransparency 在窗口源初始化后不可再改，
        // 而它与 DWMWA_SYSTEMBACKDROP_TYPE 互斥（分层窗口上背板静默不画 → 黑卡）。
        AllowsTransparency = !ThemeManager.Current.BlurEnabled;
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

    /// <summary>
    /// 面板按需创建 → 按当前主题落地背板：启用模糊走 Win11 系统背板（38 → 1029 → 旧亚克力三级降级，
    /// 见 <see cref="SystemBackdrop"/>），禁模糊（Linux）走分层窗口自绘圆角。胜出的路径决定底色：
    /// 真模糊 → 半透明透出背板；无模糊 → 自铺底，非分层形态还须把窗口底铺成不透明（无逐像素 alpha，
    /// 半透明只会露黑）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var theme = ThemeManager.Current;
        var tint = Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                  theme.AcrylicTint.G, theme.AcrylicTint.B);
        var path = ApplyBackdrop(tint, theme.BlurEnabled ? BackdropKind.Acrylic : BackdropKind.None);
        var hasBlur = path is BackdropPath.SystemBackdrop or BackdropPath.MicaLegacy
                                        or BackdropPath.CompositionAccent;

        PanelRoot.Background = (Brush)Resources[hasBlur ? "PanelFillBlur" : "PanelFillSolid"];
        if (AllowsTransparency)
        {
            PanelRoot.CornerRadius = new CornerRadius(20); // 分层窗口：逐像素 alpha 自绘圆角
        }
        else
        {
            PanelRoot.CornerRadius = new CornerRadius(0);  // 背板窗口：圆角由 DWM 裁切，卡必须铺满
            if (!hasBlur) Background = (Brush)Resources["PanelFillOpaque"]; // 兜底：不留黑区
        }
    }
}
