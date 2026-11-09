using System.Windows;
using System.Windows.Controls;
using Beautiful.App.Themes;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 ThermalSettingsCard 同法）：
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Settings;

/// <summary>
/// 墙纸取色设置卡（独立 UserControl，自包含样式）：开关 + 强度滑杆 + 状态行。
/// <para>
/// 【接线契约】不触碰设置窗口宿主文件——宿主只需在"外观"分区（放大强度行之后）插入本控件一处
/// 标记；值的写入统一走 <see cref="App.SetWallpaperTint"/>（落盘 + 即时撤/重提 tint），与设置
/// 窗口其它开关同一条路径，不存在第二份持久化逻辑。取色本体在
/// <see cref="Services.WallpaperThemeService"/>（后台线程 + 防抖 + 缓存），本卡只管摆值与落值。
/// </para>
/// <para>
/// 【状态行】显示当前生效 tint（ThemeManager.WallpaperTintHex）：空 = 玻璃保持主题原生色
/// （关闭 / 幻灯片 / 灰阶壁纸），否则显示混入色——让"为什么没有变色"在这里就有答案。
/// </para>
/// </summary>
public partial class WallpaperTintCard : UserControl
{
    /// <summary>程序化赋值期间为 true：控件自身事件不回写设置（避免"加载即保存"）。</summary>
    private bool _syncing;

    public WallpaperTintCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncFromSettings();

    /// <summary>
    /// 宿主"恢复默认"后调用：从当前设置重读勾选态与强度（不触发写回，程序化赋值包在
    /// <see cref="_syncing"/> 里）。与 ThermalSettingsCard.ReloadFromSettings 同一接线约定。
    /// </summary>
    public void ReloadFromSettings() => SyncFromSettings();

    /// <summary>把当前设置摆上控件（程序化赋值包在 _syncing 里，不触发回写）。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            TintEnabledCheck.IsChecked = Services.SettingsService.Current.WallpaperTintEnabled;
            TintStrengthSlider.Value = Services.SettingsService.Current.WallpaperTintStrength;
            UpdateStateText();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void TintEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.SetWallpaperTint(TintEnabledCheck.IsChecked == true, (int)TintStrengthSlider.Value);
        UpdateStateText();
    }

    private void TintStrength_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (TintStrengthLabel is null) return;   // XAML 解析期初次触发时标签尚未就绪
        var strength = (int)e.NewValue;
        TintStrengthLabel.Text = $"{strength}%";
        if (_syncing) return;
        App.SetWallpaperTint(TintEnabledCheck.IsChecked == true, strength);
        UpdateStateText();
    }

    /// <summary>状态行：开关关 = 已关闭；开着就显示当前生效 tint（取不出色时玻璃保持原生）。</summary>
    private void UpdateStateText()
    {
        if (TintStateText is null) return;
        TintStateText.Text = TintEnabledCheck.IsChecked != true
            ? "状态：已关闭"
            : ThemeManager.WallpaperTintHex.Length > 0
                ? $"状态：已生效，当前混入色 {ThemeManager.WallpaperTintHex}"
                : "状态：已开启，等待取色（无壁纸/灰阶壁纸时玻璃保持主题原生色）";
    }
}
