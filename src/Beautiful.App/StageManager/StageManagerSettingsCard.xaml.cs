using System.Windows;
using System.Windows.Controls;
using Beautiful.App.Services;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 FluentMenu.xaml.cs 同法）：
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度设置卡（wp-macos-parity r6）：设置窗口宿主里的自足开关行。
/// <para>
/// 【接线契约（settingsContract）】宿主只需把本控件插进内容区（建议放在"窗口切换器"分组
/// 之后），不需要注册事件、不需要资源合并——样式全部内含，取色走全局主题键。
/// 开关行为：<c>Checked/Unchecked</c> → <c>SettingsService.UpdateAndSave</c> 写
/// <c>StageManagerEnabled</c> → <c>App.ApplyStageManager()</c> 立即生效（开 = 启动跟踪 +
/// 显出贴边小条；关 = 撤窗 + 停跟踪，无残留）。回读：Loaded 时从
/// <c>SettingsService.Current.StageManagerEnabled</c> 同步勾选态（防抖标志避免回写）。
/// </para>
/// <para>
/// 【滑动动画开关（r6）】第二行勾选写 <c>StageManagerSlideAnimation</c>：只落盘，
/// 不调 ApplyStageManager——动画与否在下一次展开/收起时生效（条的生命周期与它无关，
/// 重启跟踪反而会白撤一遍窗口）。回读同走 ReloadFromSettings。
/// </para>
/// </summary>
public partial class StageManagerSettingsCard : UserControl
{
    private bool _suppress;

    public StageManagerSettingsCard()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadFromSettings();
    }

    /// <summary>
    /// 从当前设置重读勾选态（不触发写回）。宿主"恢复默认"后调用它刷新本卡；
    /// 正常路径（窗口加载）自动走一次。
    /// </summary>
    public void ReloadFromSettings()
    {
        _suppress = true;
        try
        {
            EnabledCheck.IsChecked = SettingsService.Current.StageManagerEnabled;
            SlideCheck.IsChecked = SettingsService.Current.StageManagerSlideAnimation;
        }
        finally { _suppress = false; }
    }

    /// <summary>开关翻动：写设置 + 立即生效（与宿主既有"改动即生效，无需应用"的约定一致）。</summary>
    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var enabled = EnabledCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { StageManagerEnabled = enabled });
        App.ApplyStageManager();
        Logger.Info($"台前调度：设置页开关 → {(enabled ? "开" : "关")}");
    }

    /// <summary>滑动动画开关：写设置即生效（下一次展开/收起起按新值；无需重启跟踪）。</summary>
    private void OnSlideToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var animate = SlideCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { StageManagerSlideAnimation = animate });
        Logger.Info($"台前调度：滑动动画 → {(animate ? "开" : "关（瞬时两态）")}");
    }
}
