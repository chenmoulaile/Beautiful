using System.Windows;
using System.Windows.Controls;
using Beautiful.App.Services;
using Beautiful.Core.Settings;
using Beautiful.Core.Thermal;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 FluentMenu.xaml.cs 同法）：
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Settings;

/// <summary>
/// 温度芯片设置卡（独立 UserControl，自包含样式）：开关 + 刷新间隔 + 实时数据源状态。
/// <para>
/// 【接线契约】不触碰设置窗口宿主文件——宿主只需在"系统"分区插入本控件一处标记；
/// 值的写入统一走 <see cref="App.SetThermalChip"/>（落盘 + 即时重建/停掉轮询），
/// 与设置窗口其它开关同一条路径，不存在第二份持久化逻辑。
/// </para>
/// <para>
/// 【状态行】订阅 <see cref="ThermalMonitorService.ChipChanged"/>（UI 线程）随时刷新，
/// 始终从 <see cref="ThermalMonitorService.Current"/> 取最近读数——服务没跑/没有数据源时
/// 明说"芯片保持隐藏"，不显示假状态。
/// </para>
/// </summary>
public partial class ThermalSettingsCard : UserControl
{
    /// <summary>预设间隔（秒）——与 XAML 里 ComboBoxItem 的 Tag 顺序一一对应。</summary>
    private static readonly int[] PresetSeconds = [5, 10, 15, 30, 60, 120];

    /// <summary>程序化赋值期间为 true：控件自身事件不回写设置（避免"加载即保存"）。</summary>
    private bool _syncing;

    public ThermalSettingsCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThermalMonitorService.ChipChanged += OnChipChanged;
        SyncFromSettings();
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ThermalMonitorService.ChipChanged -= OnChipChanged;

    /// <summary>把当前设置摆上控件（程序化赋值包在 _syncing 里，不触发回写）。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            ThermalEnabledCheck.IsChecked = SettingsService.Current.ThermalChipEnabled;
            var seconds = SettingsService.Current.ThermalRefreshSeconds;
            // 手改 JSON 可能给出预设外的值（如 45）：落到最接近的预设（45 → 30/60 里更近的 30 或 60）。
            var nearest = 0;
            for (var i = 0; i < PresetSeconds.Length; i++)
                if (Math.Abs(PresetSeconds[i] - seconds) < Math.Abs(PresetSeconds[nearest] - seconds))
                    nearest = i;
            RefreshSecondsBox.SelectedIndex = nearest;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>数据源状态行：开关关就说关了；开着读什么显示什么，没有就明说芯片保持隐藏。</summary>
    private void RefreshStatus()
    {
        if (ThermalEnabledCheck.IsChecked != true)
        {
            SourceStatusText.Text = "数据源状态：温度监控已关闭";
            return;
        }

        var reading = ThermalMonitorService.Current?.Latest;
        SourceStatusText.Text = reading is null
            ? "数据源状态：无数据源（LibreHardwareMonitor / OpenHardwareMonitor 未发布 WMI，ACPI 热区不可用）→ 芯片保持隐藏"
            : $"数据源状态：{TemperatureReader.Describe(reading.Source)}（当前 {reading.ChipText}）";
    }

    /// <summary>轮询推来新状态（UI 线程）：状态行跟着刷新。参数忽略——统一从 Current 取，少一份状态。</summary>
    private void OnChipChanged(object? sender, ThermalChipUpdate update) => RefreshStatus();

    private void ThermalEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.SetThermalChip(ThermalEnabledCheck.IsChecked == true, SelectedSeconds());
        RefreshStatus();
    }

    private void RefreshSeconds_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        App.SetThermalChip(ThermalEnabledCheck.IsChecked == true, SelectedSeconds());
        RefreshStatus();
    }

    /// <summary>当前选中的间隔（秒）；未选中时回默认 5（AppSettings 常量同源）。</summary>
    private int SelectedSeconds()
    {
        var index = RefreshSecondsBox.SelectedIndex;
        return index >= 0 && index < PresetSeconds.Length ? PresetSeconds[index] : AppSettings.DefaultThermalRefreshSeconds;
    }
}
