using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.Core.Themes;
using Beautiful.App.Themes;
using Application = System.Windows.Application; // UseWindowsForms 隐式 using 消歧（下同）
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Beautiful.App.Settings;

/// <summary>
/// 设置窗口（--settings 临时入口；T17 托盘菜单接管正式入口）。全部改动即时生效：
/// 主题 → ThemeManager.Apply；任务栏 → TaskbarManager.Apply；自启 → 注册表；
/// 偏移 → MediaViewModel 就地重对齐；胶囊/失焦收起 → MediaViewModel 标志；
/// 固定应用 → dock.json + DockViewModel.ReloadPinned。
/// 持久化：离散控件立即 Save；Slider 走 400ms 防抖合并写盘（拖动不产生写风暴）。
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
        AutostartCheck.IsChecked = s.Autostart;
        OffsetSlider.Value = s.LyricsOffsetMs; // ValueChanged 更新标签；_loading 屏蔽应用/保存
        CapsuleCheck.IsChecked = s.ShowMediaCapsule;
        HidePanelCheck.IsChecked = s.HidePanelOnDeactivate;
        foreach (var path in _pinnedStore.Load()) _pinned.Add(path);

        // 色块预览与 Dock 实际强调色同源（主题定义 AccentHex），避免 XAML 内散落 hex
        ThemeHarmonyOsDot.Fill = AccentBrush(ThemeDefinition.HarmonyOs());
        ThemeMacOsDot.Fill = AccentBrush(ThemeDefinition.MacOs());
        ThemeLinuxDot.Fill = AccentBrush(ThemeDefinition.Linux());
    }

    private static Brush AccentBrush(ThemeDefinition theme)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentHex));
        brush.Freeze();
        return brush;
    }

    // ---------- 主题 / 任务栏 / 自启 ----------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = sender == ThemeMacOs ? "macos" : sender == ThemeLinux ? "linux" : "harmonyos";
        SettingsService.UpdateAndSave(SettingsService.Current with { Theme = theme });
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
    }

    private void Taskbar_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var policy = sender == TaskbarAutoHide ? "autohide" : sender == TaskbarKeep ? "keep" : "hide";
        SettingsService.UpdateAndSave(SettingsService.Current with { TaskbarPolicy = policy });
        _taskbar.Apply(SettingsService.PolicyOf(SettingsService.Current));
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { Autostart = on });
        SettingsService.SetAutostart(on);
    }

    // ---------- 歌词偏移 / 胶囊 / 面板失焦 ----------

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

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
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
