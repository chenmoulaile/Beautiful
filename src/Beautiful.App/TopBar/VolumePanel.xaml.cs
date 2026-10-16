using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beautiful.App.Controls;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;      // UseWindowsForms 隐式 using 与 System.Drawing.Brush 冲突
using Brushes = System.Windows.Media.Brushes;  // 同上

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏音量图标的自家音量面板：滑杆 + 静音 + 当前输出设备名，替掉原来的 <c>sndvol.exe</c>。
/// <para>
/// 语义与控制中心的音量砖块同源（同一个 <see cref="SystemVolumeService"/> 与同一套
/// <c>VolumeMath.ApplyDrag</c> 规则）：读不到真值就整条置灰并写明原因，绝不显示一个编出来的百分比；
/// 拖动期间把宿主 <c>KeepOpen</c> 置位，否则光标滑出面板边界那一刻面板就关了，滑杆拖到一半。
/// </para>
/// </summary>
public partial class VolumePanel : UserControl, IBackdropAware, IPopupShadow
{
    private readonly SystemVolumeService _volume = new();
    private FluentPopupHost? _host;

    /// <summary>true = 正在把真值写回控件，此时 <see cref="Volume_ValueChanged"/> 不得再回写系统。</summary>
    private bool _loading;

    private bool _muted;

    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    public VolumePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshVolume();
    }

    public void AttachHost(FluentPopupHost host) => _host = host;

    /// <summary>面板关闭时释放音频端点的 COM 引用（幂等）。</summary>
    public void Shutdown() => _volume.Dispose();

    public void OnBackdropReady(ScreenBackdrop? backdrop)
    {
        var blur = backdrop?.Brush;
        if (blur is not null)
        {
            Card.Background = blur;
            Scrim.Background = (Brush)Resources["CardFillBlur"];
            return;
        }
        Card.Background = (Brush)Resources["CardFillSolid"];
        Scrim.Background = Brushes.Transparent;
    }

    /// <summary>用系统真值重建整行：设备名、滑杆位置、读数、静音字形与置灰状态。</summary>
    private void RefreshVolume()
    {
        _loading = true;
        try
        {
            if (!_volume.IsAvailable)
            {
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                VolumeText.Text = "--";
                DeviceLabel.Text = "";
                ShowReason(_volume.UnavailableReason ?? "音频接口不可用");
                return;
            }
            VolumeReason.Visibility = Visibility.Collapsed;
            DeviceLabel.Text = _volume.DeviceName ?? "";
            if (_volume.TryGetVolume(out var scalar, out var muted))
            {
                VolumeSlider.IsEnabled = true;
                MuteButton.IsEnabled = true;
                VolumeSlider.Value = scalar * 100;
                _muted = muted;
                VolumeText.Text = muted ? "已静音" : $"{(int)Math.Round(scalar * 100)}%";
                ApplyMuteGlyph();
            }
            else
            {
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                VolumeText.Text = "--";
                ShowReason("读不到当前音量（端点未响应），本次不可调");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyMuteGlyph()
    {
        // E767 有声 / E74F 静音：静音态再压暗一点，状态一眼可读
        MuteButton.Content = _muted ? "" : "";
        MuteButton.Opacity = _muted ? 0.55 : 1.0;
    }

    private void ShowReason(string text)
    {
        VolumeReason.Text = text;
        VolumeReason.Visibility = Visibility.Visible;
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !VolumeSlider.IsEnabled) return;
        var percent = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if (!_volume.ApplySliderPercent(percent, _muted, out var newMuted))
        {
            RefreshVolume();
            return;
        }
        _muted = newMuted;
        // 静音旗标可能刚被这次拖动清掉，文案跟着真值走而不是用户的旧状态
        VolumeText.Text = _muted ? "已静音" : $"{percent}%";
        ApplyMuteGlyph();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _volume.ToggleMute();
        RefreshVolume();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshVolume();

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound")
            { UseShellExecute = true });
            _host?.Close();
        }
        catch (Exception ex)
        {
            ShowReason($"打不开系统声音设置：{ex.Message}");
        }
    }

    // ---------- 拖滑杆期间抑制"光标离开即关" ----------

    private void Volume_DragBegin(object sender, MouseButtonEventArgs e) => SetKeepOpen(true);

    private void Volume_DragEnd(object sender, RoutedEventArgs e) => SetKeepOpen(false);

    private void SetKeepOpen(bool keep)
    {
        if (_host is not null) _host.KeepOpen = keep;
    }
}
