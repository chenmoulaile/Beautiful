using System.Windows;
using System.Windows.Controls;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 WallpaperTintCard 同法）：
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Settings;

/// <summary>
/// Dock 托盘收纳列设置卡（独立 UserControl，自包含样式）：开关 + 挂点下拉。
/// <para>
/// 【接线契约】不触碰设置窗口宿主文件——宿主只需在 Dock 分区（"Dock 隐藏方式"行之后、
/// 时钟/天气小组件勾选行之前）插入本控件一处标记；开关与挂点的写入统一走
/// <see cref="App.ApplyDockTray"/>（落盘 + 各 Dock 实例立即重排行尾），与设置窗口其它开关
/// 同一条路径，不存在第二份持久化逻辑。挂点白名单与插入语义在 Core
/// <c>DockTrayPlacement</c>（配置文件被手改坏会自愈为默认，这里无需做值校验）。
/// </para>
/// <para>
/// 【默认值】开关默认关（<c>DockTrayEnabled</c> = false）：托盘收纳是可选扩展，
/// 不改变既有 Dock 行结构；挂点默认 <c>widget</c>（时钟/天气小组件之前）。
/// </para>
/// </summary>
public partial class TrayDockCard : UserControl
{
    /// <summary>程序化赋值期间为 true：控件自身事件不回写设置（避免"加载即保存"）。</summary>
    private bool _syncing;

    public TrayDockCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncFromSettings();

    /// <summary>
    /// 宿主"恢复默认"后调用：从当前设置重读勾选态与挂点（不触发写回，程序化赋值包在
    /// <see cref="_syncing"/> 里）。与 WallpaperTintCard.ReloadFromSettings 同一接线约定。
    /// </summary>
    public void ReloadFromSettings() => SyncFromSettings();

    /// <summary>把当前设置摆上控件（程序化赋值包在 _syncing 里，不触发回写）。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            TrayEnabledCheck.IsChecked = Services.SettingsService.Current.DockTrayEnabled;
            // 白名单顺序 widget → end 与下拉项一一对应；未知值（理论上不会出现，Normalized 已收敛）按 0 档显示。
            TrayPositionBox.SelectedIndex = Services.SettingsService.Current.DockTrayPosition == "end" ? 1 : 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void TrayEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.ApplyDockTray(TrayEnabledCheck.IsChecked == true, CurrentPosition());
    }

    private void TrayPosition_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        // 开关开着才落值：关着的挂点没有运行态效果，等开关打开时 CurrentPosition 会带上它。
        if (TrayEnabledCheck.IsChecked == true)
        {
            App.ApplyDockTray(true, CurrentPosition());
        }
    }

    /// <summary>当前挂点下拉的值（与 Core DockTrayPlacement.PositionWidget/PositionEnd 对应）。</summary>
    private string CurrentPosition() => TrayPositionBox.SelectedIndex == 1 ? "end" : "widget";
}
