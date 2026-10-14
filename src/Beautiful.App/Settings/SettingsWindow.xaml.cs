using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Beautiful.App.Themes;
using Application = System.Windows.Application; // UseWindowsForms 隐式 using 消歧（下同）
using App = Beautiful.App.App;                   // 设置窗口在 Beautiful.App.Settings 下，显式绑定 App 类避免命名空间/类型歧义
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Beautiful.App.Settings;

/// <summary>
/// 设置窗口 v2（入口：托盘"设置"/托盘双击/顶栏"设置…"/--settings 命令行；App 侧单例管理）。
/// 全部改动即时生效并持久化，无"确定/应用"按钮；非模态、非置顶，不遮挡 Dock。
/// 分组：外观（主题/图标大小/放大强度/倒影/弹跳）· 系统（任务栏策略/顶栏/自启 + 退出提示）·
/// 窗口切换器（接管开关/样式/全屏直通/应急键说明）· 媒体与歌词（胶囊/失焦收起/偏移/预览）· 固定应用。
/// 各 live 目标：
/// 主题 → ThemeManager.Apply（DockWindow 订阅 ThemeChanged 自行重落尺寸/放大/遮罩）；
/// 图标大小/放大强度/倒影 → App.ApplyDockLayout（DockWindow 重排条目几何 + 覆盖 ThemeIconSize/遮罩 + 按需重排条带）；
/// 任务栏 → TaskbarManager.Apply；顶栏 → App.SetTopBarEnabled；自启 → 注册表；
/// 切换器接管 → App.SetSwitcherEnabled；样式 → 持久化（面板下次呼出重读）；全屏直通 → App.ApplySwitcherHotkey；
/// 偏移 → MediaViewModel.ApplyLyricsOffset；胶囊/失焦收起 → MediaViewModel 标志；
/// 固定应用 → dock.json + DockViewModel.ReloadPinned。
/// 持久化：离散控件立即 Save；三个滑杆走 400ms 防抖合并写盘（拖动不产生写风暴）。
/// 弹跳 / 悬停预览目前无渲染器消费（见报告）——控件仅落盘并回显，视觉待后续版本；Dock 停靠边未提供（底部 AppBar 专化）。
/// 写入统一经 SettingsService.Update/UpdateAndSave（内部过 AppSettings.Normalized），内存态永不越界。
/// 初始化期间 _loading 屏蔽 Checked/ValueChanged 处理器，避免构造期回写。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly TaskbarManager _taskbar;
    private readonly DockViewModel _dockVm;
    private readonly MediaViewModel _mediaVm;
    private readonly PinnedAppsStore _pinnedStore = new();
    private readonly ObservableCollection<string> _pinned = new();
    private readonly DispatcherTimer _saveDebounce;
    private bool _loading = true;

    public SettingsWindow(TaskbarManager taskbar, DockViewModel dockVm, MediaViewModel mediaVm)
    {
        InitializeComponent();
        _taskbar = taskbar;
        _dockVm = dockVm;
        _mediaVm = mediaVm;
        DataContext = this; // 固定应用列表绑定 PinnedApps

        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SettingsService.Save();
        };

        LoadState();
        _loading = false;
    }

    /// <summary>固定应用路径列表（ListBox 绑定；变更即写 dock.json 并触发 Dock 重载）。</summary>
    public ObservableCollection<string> PinnedApps => _pinned;

    // ---------- 初始化 ----------

    private void LoadState()
    {
        var s = SettingsService.Current;
        (s.Theme switch
        {
            "macos" => ThemeMacOs,
            "linux" => ThemeLinux,
            _ => ThemeHarmonyOs
        }).IsChecked = true;
        (s.TaskbarPolicy switch
        {
            "autohide" => TaskbarAutoHide,
            "keep" => TaskbarKeep,
            _ => TaskbarHide
        }).IsChecked = true;
        (s.SwitcherStyle switch
        {
            "linux" => StyleLinux,
            "harmonyos" => StyleHarmonyOs,
            _ => StyleMacOs
        }).IsChecked = true;

        AutostartCheck.IsChecked = s.Autostart;
        TopBarCheck.IsChecked = s.TopBarEnabled;
        SwitcherCheck.IsChecked = s.SwitcherEnabled;
        FullscreenPassthroughCheck.IsChecked = s.FullscreenPassthrough;
        ReflectionCheck.IsChecked = s.ReflectionEnabled;
        BounceCheck.IsChecked = s.BounceEnabled;
        CapsuleCheck.IsChecked = s.ShowMediaCapsule;
        HidePanelCheck.IsChecked = s.HidePanelOnDeactivate;
        PreviewCheck.IsChecked = s.PreviewEnabled;

        // 滑杆：设值触发 ValueChanged 更新标签；_loading 屏蔽应用/保存（值已由 Current 提供，均已规范化）
        IconSizeSlider.Value = s.DockIconSize;
        MagnifySlider.Value = s.MagnificationStrength;
        OffsetSlider.Value = s.LyricsOffsetMs;

        _pinned.Clear();
        foreach (var path in _pinnedStore.Load()) _pinned.Add(path);

        // 色块预览与 Dock 实际强调色同源（主题定义 AccentHex），避免 XAML 内散落 hex
        ThemeHarmonyOsDot.Fill = AccentBrush(ThemeDefinition.HarmonyOs());
        ThemeMacOsDot.Fill = AccentBrush(ThemeDefinition.MacOs());
        ThemeLinuxDot.Fill = AccentBrush(ThemeDefinition.Linux());

        UpdateThemeDependentControls();
        UpdateTaskbarHint();
    }

    private static Brush AccentBrush(ThemeDefinition theme)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentHex));
        brush.Freeze();
        return brush;
    }

    /// <summary>放大强度滑杆与倒影开关仅 macOS 主题有意义（其余主题 ShowMagnification=false）。</summary>
    private void UpdateThemeDependentControls()
    {
        var mac = SettingsService.Current.Theme == "macos";
        MagnifySlider.IsEnabled = mac;
        ReflectionCheck.IsEnabled = mac;
    }

    private void UpdateTaskbarHint()
    {
        TaskbarHint.Text = SettingsService.Current.TaskbarPolicy switch
        {
            "hide" => "隐藏：原生任务栏被移出屏幕，Dock 与屏幕真底边无缝衔接。",
            "autohide" => "自动隐藏：光标移到屏幕底边即可唤出原生任务栏。",
            _ => "保留：不改动原生任务栏，Dock 悬停在其上方。"
        };
    }

    // ---------- 外观 ----------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = sender == ThemeMacOs ? "macos" : sender == ThemeLinux ? "linux" : "harmonyos";
        SettingsService.UpdateAndSave(SettingsService.Current with { Theme = theme });
        // Apply → DockWindow 订阅 ThemeChanged 自行重落尺寸/放大/遮罩；设置窗口只刷新依赖主题的控件可用态
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
        UpdateThemeDependentControls();
    }

    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IconSizeLabel is null) return; // XAML 解析期初次触发时标签尚未就绪
        var px = (int)e.NewValue;           // IsSnapToTickEnabled 步进 1
        IconSizeLabel.Text = $"{px} px";
        if (_loading) return;
        SettingsService.Update(SettingsService.Current with { DockIconSize = px });
        App.ApplyDockLayout();
        ScheduleSave();
    }

    private void MagnifySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MagnifyLabel is null) return;
        var m = Math.Round(e.NewValue, 1);
        MagnifyLabel.Text = $"×{m:0.0}";
        if (_loading || !MagnifySlider.IsEnabled) return; // 非 macOS 主题滑杆禁用：不回写（值保留待切回 macOS）
        SettingsService.Update(SettingsService.Current with { MagnificationStrength = m });
        App.ApplyDockLayout();
        ScheduleSave();
    }

    private void Reflection_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ReflectionCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { ReflectionEnabled = on });
        App.ApplyDockLayout(); // 落旗标到 DockSurface.ReflectionEnabled（倒影渲染器待后续任务）
    }

    private void Bounce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // 弹跳动画尚无渲染器消费：仅持久化 + 回显（见报告"待接线"）
        SettingsService.UpdateAndSave(SettingsService.Current with { BounceEnabled = BounceCheck.IsChecked == true });
    }

    // ---------- 系统 ----------

    private void Taskbar_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var policy = sender == TaskbarAutoHide ? "autohide" : sender == TaskbarKeep ? "keep" : "hide";
        SettingsService.UpdateAndSave(SettingsService.Current with { TaskbarPolicy = policy });
        _taskbar.Apply(SettingsService.PolicyOf(SettingsService.Current));
        UpdateTaskbarHint();
    }

    private void TopBar_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetTopBarEnabled(TopBarCheck.IsChecked == true); // 内部 UpdateAndSave + 创建/销毁顶栏
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { Autostart = on });
        SettingsService.SetAutostart(on);
    }

    // ---------- 窗口切换器 ----------

    private void Switcher_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetSwitcherEnabled(SwitcherCheck.IsChecked == true); // 装/摘键盘钩子 + 装配/收起面板
    }

    private void SwitcherStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var style = sender == StyleLinux ? "linux" : sender == StyleHarmonyOs ? "harmonyos" : "macos";
        // 面板在每次呼出时重读 SwitcherStyle，无需重启；此处仅落盘（钩子状态不变）
        SettingsService.UpdateAndSave(SettingsService.Current with { SwitcherStyle = style });
    }

    private void Passthrough_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = FullscreenPassthroughCheck.IsChecked == true;
        // 触发键当前固定（alt+tab），连同直通标志一并落盘并按新配置重装钩子
        App.ApplySwitcherHotkey(SettingsService.Current.SwitcherHotkey, on);
    }

    // ---------- 媒体与歌词 ----------

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OffsetLabel is null) return; // XAML 解析期初次触发时标签尚未就绪
        OffsetLabel.Text = $"{(int)e.NewValue} ms"; // 标签始终跟手（含初始化）
        if (_loading) return;
        var offset = (int)e.NewValue; // IsSnapToTickEnabled 步进 50，已对齐
        SettingsService.Update(SettingsService.Current with { LyricsOffsetMs = offset });
        _mediaVm.ApplyLyricsOffset(offset);
        ScheduleSave();
    }

    private void Capsule_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = CapsuleCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { ShowMediaCapsule = on });
        _mediaVm.CapsuleVisible = on;
    }

    private void HidePanel_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = HidePanelCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { HidePanelOnDeactivate = on });
        _mediaVm.PanelAutoHideOnDeactivate = on;
    }

    private void Preview_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // DWM 缩略图预览渲染器尚未实现（Task 13）：仅持久化 + 回显（见报告"待接线"）
        SettingsService.UpdateAndSave(SettingsService.Current with { PreviewEnabled = PreviewCheck.IsChecked == true });
    }

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    // ---------- 恢复默认 ----------

    /// <summary>
    /// 把 settings.json 承载的全部设置重置为代码默认（<see cref="AppSettings"/> 默认值）并即时重放所有 live 目标。
    /// 固定应用归属 dock.json、独立于 AppSettings，不受"恢复默认"影响（保留用户已配置项）。
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.UpdateAndSave(new AppSettings());
        _loading = true;
        LoadState();        // 回填全部控件（含重新读 dock.json 固定列表——未被改动）
        _loading = false;
        ReapplyAll();       // 重放到运行中的 Dock/顶栏/切换器/媒体
    }

    /// <summary>把当前设置整体重放到各 live 目标（恢复默认后调用）。</summary>
    private void ReapplyAll()
    {
        var s = SettingsService.Current;
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(s), Application.Current);
        _taskbar.Apply(SettingsService.PolicyOf(s));
        SettingsService.SetAutostart(s.Autostart);
        App.SetTopBarEnabled(s.TopBarEnabled);
        App.SetSwitcherEnabled(s.SwitcherEnabled);
        App.ApplyDockLayout();
        _mediaVm.CapsuleVisible = s.ShowMediaCapsule;
        _mediaVm.PanelAutoHideOnDeactivate = s.HidePanelOnDeactivate;
        _mediaVm.ApplyLyricsOffset(s.LyricsOffsetMs);
        UpdateThemeDependentControls();
        UpdateTaskbarHint();
    }

    // ---------- 固定应用 ----------

    private void SavePinned()
    {
        _pinnedStore.Save(_pinned);
        _dockVm.ReloadPinned(); // Dock 固定块即时重建（设置窗口与 Dock 共享 dock.json）
    }

    private void AddPinned_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "添加固定应用",
            Filter = "程序|*.lnk;*.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;
        if (_pinned.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "该应用已在固定列表中。", "Beautiful 设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _pinned.Add(dlg.FileName);
        SavePinned();
    }

    private void RemovePinned_Click(object sender, RoutedEventArgs e)
    {
        if (PinnedList.SelectedIndex < 0) return;
        _pinned.RemoveAt(PinnedList.SelectedIndex);
        SavePinned();
    }

    private void MoveUpPinned_Click(object sender, RoutedEventArgs e)
    {
        var idx = PinnedList.SelectedIndex;
        if (idx <= 0) return;
        _pinned.Move(idx, idx - 1);
        PinnedList.SelectedIndex = idx - 1;
        SavePinned();
    }

    private void MoveDownPinned_Click(object sender, RoutedEventArgs e)
    {
        var idx = PinnedList.SelectedIndex;
        if (idx < 0 || idx >= _pinned.Count - 1) return;
        _pinned.Move(idx, idx + 1);
        PinnedList.SelectedIndex = idx + 1;
        SavePinned();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
