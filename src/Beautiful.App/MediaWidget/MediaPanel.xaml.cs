using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Color = System.Windows.Media.Color;

namespace Beautiful.App.MediaWidget;

/// <summary>
/// 歌词展开面板：跟随胶囊上方居中的独立亚克力窗（位置由 DockWindow 在 Show 前计算）。
/// 失焦自动 Hide（Deactivated → Hide，本任务硬编码；开关归 Task 16 设置）；
/// 进度条交互用 ValueChanged 差值检测 + 防抖（见构造函数注释），SeekAsync 首次失败 →
/// IsSeekable=false 隐藏进度条；歌词 CurrentLyricIndex 变化 → ScrollIntoView（VM 事件驱动）。
/// </summary>
public partial class MediaPanel
{
    private readonly MediaViewModel _vm;
    private DispatcherTimer? _seekDebounce; // seek 防抖：ValueChanged 风暴结束后统一结算一次

    public MediaPanel(MediaViewModel vm)
    {
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

    private void OnDeactivatedHide(object? sender, EventArgs e) => Hide();

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var theme = ThemeManager.Current; // 面板按需创建：按当前主题套亚克力
        if (theme.BlurEnabled)
            ApplyAcrylic(Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R, theme.AcrylicTint.G, theme.AcrylicTint.B));
        else
            DisableAcrylic();
    }
}
