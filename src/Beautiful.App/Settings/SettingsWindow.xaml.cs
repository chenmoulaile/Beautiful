using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.Interop;
using Beautiful.App.MediaWidget;
using Beautiful.App.Services;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Beautiful.Core.TopBar;   // 时钟样张与顶栏共用同一套 Core 折算（ClockFormat / ClockFormatter）
using Beautiful.App.Themes;
using Application = System.Windows.Application; // UseWindowsForms 隐式 using 消歧（下同）
using App = Beautiful.App.App;                   // 设置窗口在 Beautiful.App.Settings 下，显式绑定 App 类避免命名空间/类型歧义
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;                                 // 消歧：WinForms 隐式 using 带入同名控件
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using RadioButton = System.Windows.Controls.RadioButton;                           // 同上（主题/策略/样式三组分段按钮的按 Tag 反查）
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;                        // 同上（图标处理方式三项）
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs; // 同上
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Beautiful.App.Settings;

/// <summary>
/// 设置窗口 v2（入口：托盘"设置"/托盘双击/顶栏"设置…"/--settings 命令行；App 侧单例管理）。
/// 全部改动即时生效并持久化，无"确定/应用"按钮；非模态，默认可被别的窗口压住，绿点可临时置顶。
/// 标题栏是自绘的 macOS 三点（关闭/最小化/置顶开关），系统 chrome 已退役，见 XAML 头部注释。
/// 分组：外观（主题/图标大小/放大强度/倒影/弹跳）· 系统（任务栏策略/顶栏/顶栏时钟四项/自启 + 退出提示）·
/// 显示器（Dock 与顶栏各自所在显示器，v3 T1）· 窗口切换器（接管开关/样式/全屏直通/应急键说明）·
/// 媒体（胶囊/失焦收起/预览）· 固定应用。
/// 各 live 目标：
/// 主题 → ThemeManager.Apply（DockWindow 订阅 ThemeChanged 自行重落尺寸/放大/遮罩）；
/// 图标大小/放大强度/倒影 → App.ApplyDockLayout（DockWindow 重排条目几何 + 覆盖 ThemeIconSize/遮罩 + 按需重排条带）；
/// 任务栏 → TaskbarManager.Apply；顶栏 → App.SetTopBarEnabled；顶栏时钟四项 → App.ApplyTopBarClock
/// （重算文本 + 按"是否显示秒"重排定时器步长）；自启 → 注册表；
/// 显示器选择 → App.ApplyDockLayout / App.ApplyTopBarGeometry（按新屏的 rcMonitor 与有效 DPI 重新协商条带并搬窗）；
/// 切换器接管 → App.SetSwitcherEnabled；样式 → 持久化（面板下次呼出重读）；全屏直通 → App.ApplySwitcherHotkey；
/// 胶囊/失焦收起 → MediaViewModel 标志；
/// 固定应用 → dock.json + DockViewModel.ReloadPinned。
/// 持久化：离散控件立即 Save；三个滑杆走 400ms 防抖合并写盘（拖动不产生写风暴）。
/// 渲染器覆盖（Task 15 收尾核对）：倒影/启动弹跳自 Task 15 起在 DockSurface 有渲染器并即时生效；
/// 悬停预览自 Task 13 起由 DWM 缩略图渲染（最小化窗口无可预览源，属 Windows 合成本身的限制）。
/// 唯一无渲染项是 Dock 停靠边：未提供控件，底部注释已如实说明（底部 AppBar 专化）。
/// 写入统一经 SettingsService.Update/UpdateAndSave（内部过 AppSettings.Normalized），内存态永不越界。
/// 初始化期间 _loading 屏蔽 Checked/ValueChanged/SelectionChanged 处理器，避免构造期回写。
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
        SectionNav.SelectedIndex = 0;
        AboutVersionText.Text = $"Beautiful v{ProductVersion()}（v4.0-alpha / Phase 1）";
        AboutFontText.Text = Beautiful.Core.Design.FontResolution.Describe(FontProbe.Current);
        AboutConfigText.Text = $"配置与日志：{AppContext.BaseDirectory}";
        VersionLine.Text = $"Beautiful v{ProductVersion()} · {Beautiful.Core.Design.FontResolution.Describe(FontProbe.Current)} · 配置与日志在 {AppContext.BaseDirectory}";
    }

    /// <summary>侧栏导航：把对应分组卡片滚进视野（v4 Phase 1 T7）。不重建控件、不切页，避免丢状态。</summary>
    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionNav.SelectedItem is not ListBoxItem { Tag: string name }) return;
        if (FindName(name) is FrameworkElement target) target.BringIntoView();
    }

    /// <summary>
    /// 显示用的版本号：取 InformationalVersion（MSBuild 由 Directory.Build.props 的 &lt;Version&gt; 生成），
    /// 并砍掉 SourceLink 可能追加的 <c>+提交哈希</c> 后缀——那一串对报障没用，只会占掉整行宽度。
    /// </summary>
    private static string ProductVersion()
    {
        var v = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(v) ? "未知" : v.Split('+')[0];
    }

    /// <summary>固定应用路径列表（ListBox 绑定；变更即写 dock.json 并触发 Dock 重载）。</summary>
    public ObservableCollection<string> PinnedApps => _pinned;

    // ---------- 初始化 ----------

    private void LoadState()
    {
        var s = SettingsService.Current;
        // 外观模式（v4 T4）：互斥单选，未命中回落深色
        (s.Appearance switch
        {
            "light" => AppearanceLight,
            "auto" => AppearanceAuto,
            _ => AppearanceDark
        }).IsChecked = true;
        _loading = true;
        AccentColorBox.ItemsSource = Beautiful.Core.Design.MacAccents.All
            .Select(a => new AccentOption(a.Id, a.Name, AccentBrush(ThemeManager.IsDark ? a.DarkHex : a.LightHex)))
            .ToList();
        AccentColorBox.SelectedValue = s.AccentColor;
        _loading = false;
        (s.TaskbarPolicy switch
        {
            "autohide" => TaskbarAutoHide,
            "keep" => TaskbarKeep,
            _ => TaskbarHide
        }).IsChecked = true;
        StyleMacOs.IsChecked = true;

        AutostartCheck.IsChecked = s.Autostart;
        TopBarCheck.IsChecked = s.TopBarEnabled;
        NotificationCenterCheck.IsChecked = s.NotificationCenterEnabled;
        VolumeOsdCheck.IsChecked = s.VolumeOsdEnabled;
        NotificationBannerCheck.IsChecked = s.NotificationBannerEnabled;
        // 顶栏时钟四项（v3.1）：直接落 AppSettings 的四个 bool，样张由 Core 的同一套折算给出
        ClockSecondsCheck.IsChecked = s.ClockShowSeconds;
        ClockDateCheck.IsChecked = s.ClockShowDate;
        ClockWeekdayCheck.IsChecked = s.ClockShowWeekday;
        Clock24Check.IsChecked = s.ClockUse24Hour;
        UpdateClockSample();
        SwitcherCheck.IsChecked = s.SwitcherEnabled;
        FullscreenPassthroughCheck.IsChecked = s.FullscreenPassthrough;
        // 倒影是三态设置：用户没显式动过（null）时显示**当前主题默认**（现代 macOS/GNOME/鸿蒙都无倒影），
        // 勾/去勾一次就写入显式值，此后切主题不再冲掉用户的选择（AppSettings.ReflectionEnabledFor）。
        ReflectionCheck.IsChecked = s.ReflectionEnabledFor(SettingsService.ThemeDefinitionOf(s).ReflectionDefault);
        BounceCheck.IsChecked = s.BounceEnabled;
        CapsuleCheck.IsChecked = s.ShowMediaCapsule;
        DockWidgetCheck.IsChecked = s.DockWidgetEnabled;
        DockAutoHideCheck.IsChecked = s.DockAutoHide;
        HidePanelCheck.IsChecked = s.HidePanelOnDeactivate;
        PreviewCheck.IsChecked = s.PreviewEnabled;

        // 滑杆：设值触发 ValueChanged 更新标签；_loading 屏蔽应用/保存（值已由 Current 提供，均已规范化）
        IconSizeSlider.Value = s.DockIconSize;
        DockEdgeMarginSlider.Value = s.DockEdgeMargin;
        DockEdgeMarginLabel.Text = $"{s.DockEdgeMargin} px";
        MagnifySlider.Value = s.MagnificationStrength;


        // 图标处理方式（v3 T6）：按 Tag 选中，未知值由 AppSettings.Normalized 收敛成 auto，这里必定命中一项
        SelectTreatment(s.IconTreatment);
        SelectByTag(GenieEngineBox, s.GenieEngine, "mesh");
        LauncherEnabledCheck.IsChecked = s.LauncherEnabled;
        LauncherHotkeyBox.Text = s.LauncherHotkey;
        MinimizeAnimationCheck.IsChecked = s.MinimizeAnimation;
        SelectByTag(MinimizeEffectBox, s.MinimizeEffect, "genie");
        AnimationSpeedSlider.Value = s.AnimationSpeed;
        AnimationSpeedLabel.Text = $"×{s.AnimationSpeed:0.0}";
        MinimizeExcludesBox.Text = s.MinimizeExcludes;
        FullscreenAvoidanceCheck.IsChecked = s.FullscreenAvoidanceEnabled;
        NativeModulesCheck.IsChecked = s.NativeModules;
        NativeBlurCheck.IsChecked = s.NativeBlur;
        NativeCaptureCheck.IsChecked = s.NativeCapture;
        NativeSceneCheck.IsChecked = s.NativeScene;
        NativeTrayBridgeCheck.IsChecked = s.NativeTrayBridge;
        SelectByTag(HotCornerTopLeftBox, s.HotCornerTopLeft, "disabled");
        SelectByTag(HotCornerTopRightBox, s.HotCornerTopRight, "disabled");
        SelectByTag(HotCornerBottomLeftBox, s.HotCornerBottomLeft, "disabled");
        SelectByTag(HotCornerBottomRightBox, s.HotCornerBottomRight, "disabled");
        SelectByTag(MaterialQualityBox, s.MaterialQuality, "auto");
        UpdateNativeControls();

        LoadMonitorSelectors();

        _pinned.Clear();
        foreach (var path in _pinnedStore.Load()) _pinned.Add(path);

        UpdateThemeDependentControls();
        UpdateTaskbarHint();
    }

    /// <summary>强调色下拉项：Core 只给 hex，WPF 侧带一枚预览色块。</summary>
    public sealed record AccentOption(string Id, string Name, Brush Swatch);

    private static Brush AccentBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 放大强度滑杆与倒影开关只对"这个主题真的会放大"有意义——判据取主题参数
    /// <see cref="ThemeDefinition.ShowMagnification"/>，不再写 <c>theme == "macos"</c>：
    /// 以后再有第二个会放大的主题，这里不用改。
    /// </summary>
    private void UpdateThemeDependentControls()
    {
        var magnifies = ThemeManager.Current.ShowMagnification;
        MagnifySlider.IsEnabled = magnifies;
        ReflectionCheck.IsEnabled = magnifies;
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

    /// <summary>
    /// 填充两个显示器下拉并回显当前设置。选项由 <see cref="MonitorGeometry.Options"/> 现场枚举
    /// （auto / primary / 每一块屏的真实分辨率与原点）——序号只有在本次枚举里才有意义，
    /// 所以标签必须把分辨率/原点写出来，用户才知道"显示器 2"是哪一块。
    /// </summary>
    private void LoadMonitorSelectors()
    {
        var s = SettingsService.Current;
        FillMonitorBox(DockMonitorBox, s.DockMonitor);
        FillMonitorBox(TopBarMonitorBox, s.TopBarMonitor);
    }

    private static void FillMonitorBox(ComboBox box, string selected)
    {
        box.DisplayMemberPath = nameof(MonitorOption.Label);
        box.SelectedValuePath = nameof(MonitorOption.Value);
        box.ItemsSource = MonitorGeometry.Options();
        box.SelectedValue = selected;
        // 设置里的序号指向一块已经不存在的屏（拔掉过显示器）：匹配不到项就把下拉显示成 auto，
        // 但**不改写设置值**——运行时 MonitorGeometry 同样按 auto 解析，两边口径一致，
        // 且插回那块屏后原选择自动恢复（而不是被一次打开设置悄悄抹掉）。
        if (box.SelectedItem is null) box.SelectedIndex = 0;
    }

    // ---------- 外观 ----------

    /// <summary>外观模式单选：dark/light/auto，落地走 App.ApplyAppearance（落盘 + token 重写，不重建窗口）。</summary>
    private void Appearance_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = AppearanceLight.IsChecked == true ? "light"
                 : AppearanceAuto.IsChecked == true ? "auto"
                 : "dark";
        App.ApplyAppearance(mode);
        UpdateThemeDependentControls();
    }

    /// <summary>强调色下拉：12 色板统一来自 Core MacAccents；落地重写 ThemeAccent/MacAccent。</summary>
    private void AccentColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentColorBox.SelectedValue is not string id) return;
        App.ApplyAccentColor(id);
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

    private void DockEdgeMargin_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DockEdgeMarginLabel is null) return;
        var px = (int)e.NewValue;
        DockEdgeMarginLabel.Text = $"{px} px";
        if (_loading) return;
        SettingsService.Update(SettingsService.Current with { DockEdgeMargin = px });
        App.ApplyDockLayout();
        ScheduleSave();
    }

    private void MagnifySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)    {
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
        // DockSurface 倒影渲染器即时增删镜像元素并加长条目行（ApplyDockLayout → 重落旗标 + 重排条带）
        App.ApplyDockLayout();
    }

    /// <summary>
    /// 图标处理方式（v3 T6）变更：落盘 + 走既有的 Dock 布局重放入口
    /// （App.ApplyDockLayout → DockWindow.RefreshLayoutFromSettings → 重落烘焙参数 → 缓存清空 → 防抖重建条目）。
    /// 与图标尺寸同一条路径，所以不需要重启，也不会出现"设置已改、图标还是上一套形状"。
    /// </summary>
    private void IconTreatment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = TagOf(IconTreatmentBox.SelectedItem) ?? "auto";
        SettingsService.UpdateAndSave(SettingsService.Current with { IconTreatment = value });
        App.ApplyDockLayout();
    }

    /// <summary>按 Tag 选中处理方式那一项（找不到就退回第一项 auto，永不显示空白）。</summary>
    private void SelectTreatment(string value)
    {
        foreach (var item in IconTreatmentBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(TagOf(item), value, StringComparison.OrdinalIgnoreCase))
            {
                IconTreatmentBox.SelectedItem = item;
                return;
            }
        IconTreatmentBox.SelectedIndex = 0;
    }

    private static string? TagOf(object? item)
        => (item as ComboBoxItem)?.Tag as string;

    /// <summary>按 Tag 选中 ComboBox 项；未知值回落 fallback（Core 的 Normalized 已保证合法）。</summary>
    private static void SelectByTag(ComboBox box, string value, string fallback)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (TagOf(item) == value)
            {
                box.SelectedItem = item;
                return;
            }
        }
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (TagOf(item) == fallback)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    // ---------- 启动台与搜索 ----------

    private void Launcher_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetLauncherEnabled(LauncherEnabledCheck.IsChecked == true);
    }

    /// <summary>热键应用：Core Canonicalize 负责"至少一个修饰键 + 非空格主键"；非法值自愈为默认串。</summary>
    private void LauncherHotkey_Apply(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var canonical = Beautiful.Core.Launcher.LauncherHotkey.Canonicalize(LauncherHotkeyBox.Text);
        App.ApplyLauncherHotkey(canonical);
        LauncherHotkeyBox.Text = SettingsService.Current.LauncherHotkey;
    }

    // ---------- 动画 ----------

    private void MinimizeAnimation_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // 动画器在启动时装配：只落盘，不假装即时生效（设置页文案已写明"重启生效"）
        SettingsService.UpdateAndSave(SettingsService.Current with { MinimizeAnimation = MinimizeAnimationCheck.IsChecked == true });
    }

    private void FullscreenAvoidance_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with { FullscreenAvoidanceEnabled = FullscreenAvoidanceCheck.IsChecked == true });
        // 服务每 1s 读设置；这里不强行触发，下一拍自然按新值恢复/让位。
    }

    private void AnimationSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationSpeedLabel is null) return;
        var speed = Math.Round(e.NewValue, 1);
        AnimationSpeedLabel.Text = $"×{speed:0.0}";
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with { AnimationSpeed = speed });
    }

    private void MinimizeEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var effect = TagOf(MinimizeEffectBox.SelectedItem) ?? "genie";
        SettingsService.UpdateAndSave(SettingsService.Current with { MinimizeEffect = effect });
    }

    private void MinimizeExcludes_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with { MinimizeExcludes = MinimizeExcludesBox.Text.Trim() });
    }

    private void GenieEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = TagOf(GenieEngineBox.SelectedItem) ?? "mesh";
        SettingsService.UpdateAndSave(SettingsService.Current with { GenieEngine = value });
    }

    // ---------- 触发角 ----------

    private void HotCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            HotCornerTopLeft = TagOf(HotCornerTopLeftBox.SelectedItem) ?? "disabled",
            HotCornerTopRight = TagOf(HotCornerTopRightBox.SelectedItem) ?? "disabled",
            HotCornerBottomLeft = TagOf(HotCornerBottomLeftBox.SelectedItem) ?? "disabled",
            HotCornerBottomRight = TagOf(HotCornerBottomRightBox.SelectedItem) ?? "disabled"
        });
        // HotCornerService 每 100ms 读一次设置，下一拍自然生效。
    }

    // ---------- 底层增强（Phase 2） ----------

    private void Native_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            NativeModules = NativeModulesCheck.IsChecked == true,
            NativeBlur = NativeBlurCheck.IsChecked == true,
            NativeCapture = NativeCaptureCheck.IsChecked == true,
            NativeScene = NativeSceneCheck.IsChecked == true,
            NativeTrayBridge = NativeTrayBridgeCheck.IsChecked == true
        });
        UpdateNativeControls();
    }

    private void MaterialQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            MaterialQuality = TagOf(MaterialQualityBox.SelectedItem) ?? "auto"
        });
    }

    /// <summary>native 不可用时把子项置灰并写明原因；总开关关闭时同理（不制造亮着却无效的勾选框）。</summary>
    private void UpdateNativeControls()
    {
        var available = NativeLoader.Available;
        var total = NativeModulesCheck.IsChecked == true;
        var enabled = available && total;
        NativeBlurCheck.IsEnabled = enabled;
        NativeCaptureCheck.IsEnabled = enabled;
        NativeSceneCheck.IsEnabled = enabled;
        NativeTrayBridgeCheck.IsEnabled = enabled;
        MaterialQualityBox.IsEnabled = enabled;
        InjectDrillButton.IsEnabled = enabled && NativeTrayBridgeCheck.IsChecked == true;

        var reason = !available
            ? NativeLoader.Status
            : !total ? "总开关已关闭（纯 C# 形态）" : "";
        NativeHealthText.Text = available
            ? $"Native {NativeLoader.Version} · modules=0x{NativeLoader.Modules:X}"
            : $"不可用：{reason}";
        NativeHealthText.ToolTip = string.IsNullOrEmpty(reason) ? null : reason;
    }

    /// <summary>
    /// 注入演练：只有所有者点击 + 二次确认才执行；注入后立即卸载并提示检查 explorer。
    /// 绝不自动执行（注入 explorer 失败的代价是桌面重启）。
    /// </summary>
    private void InjectDrill_Click(object sender, RoutedEventArgs e)
    {
        if (!NativeLoader.Available)
        {
            MessageBox.Show(this, $"Native 层不可用：{NativeLoader.Status}", "注入演练",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (NativeTrayBridgeCheck.IsChecked != true)
        {
            MessageBox.Show(this, "请先勾选“TrayBridge 注入”。", "注入演练",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var probe = NativeLoader.InjectProbe();
        if (probe != 0)
        {
            MessageBox.Show(this, $"OS build 不在白名单：code={probe}\n{NativeLoader.LastError()}", "注入演练",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                "将把 Beautiful.TrayBridge.dll 注入 explorer.exe（空 payload：只做心跳/卸载）。\n\n" +
                "这是高风险演练：失败可能导致桌面重启。确认现在执行？",
                "注入演练", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var dll = System.IO.Path.Combine(AppContext.BaseDirectory, "Beautiful.TrayBridge.dll");
        var injected = NativeLoader.InjectExplorer(dll);
        if (injected != 0)
        {
            MessageBox.Show(this, $"注入失败 code={injected}\n{NativeLoader.LastError()}", "注入演练",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var unload = NativeLoader.InjectUnload();
        MessageBox.Show(this,
            $"注入成功；卸载命令={unload}。\n请确认 explorer 未重启、桌面无异常，并把 logs/native.log 与 %TEMP%\\Beautiful.TrayBridge.log 发回。",
            "注入演练", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateNativeControls();
    }

    private void Bounce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // 弹跳无需重放：启动固定项的路径每次实时读 BounceEnabled（DockWindow.ActivateItem → Surface.StartBounce）
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

    /// <summary>通知横幅开关（Phase 4-T5 续）：关掉后新通知只进通知中心，不弹横幅。</summary>
    private void NotificationBanner_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            NotificationBannerEnabled = NotificationBannerCheck.IsChecked == true
        });
    }

    /// <summary>音量 HUD 开关（Phase 4-T7）：接管/放行媒体键，立即生效（重建控制器）。</summary>
    private void VolumeOsd_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = VolumeOsdCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { VolumeOsdEnabled = on });
        App.ApplyVolumeOsd(on);
    }

    /// <summary>通知中心开关（Phase 4-T5）：只改"时钟左键打开什么"，不影响已读到的历史。</summary>
    private void NotificationCenter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            NotificationCenterEnabled = NotificationCenterCheck.IsChecked == true
        });
    }

    /// <summary>
    /// 顶栏时钟四项变更（v3.1）：四个勾选框共用这一个处理器，一次把四个 bool 整体写进
    /// <see cref="AppSettings"/>（避免"改一项丢三项"），落盘后立即重放顶栏——<b>无需重启</b>：
    /// 顶栏按新形态重排自己的时钟定时器（显示秒=对齐秒边界的每秒，关秒=对齐分边界的每分钟）。
    /// </summary>
    private void Clock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsService.Current;
        SettingsService.UpdateAndSave(s with
        {
            ClockShowSeconds = ClockSecondsCheck.IsChecked == true,
            ClockShowDate = ClockDateCheck.IsChecked == true,
            ClockShowWeekday = ClockWeekdayCheck.IsChecked == true,
            ClockUse24Hour = Clock24Check.IsChecked == true
        });
        App.ApplyTopBarClock();
        UpdateClockSample();
    }

    /// <summary>把当前四项折算出的模板渲染成"此刻长什么样"，勾选即见结果（与顶栏同一个格式化函数）。</summary>
    private void UpdateClockSample()
    {
        var format = ClockFormat.From(SettingsService.Current);
        ClockSample.Text = ClockFormatter.Format(DateTime.Now, format.Pattern);
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { Autostart = on });
        SettingsService.SetAutostart(on);
    }

    // ---------- 显示器 ----------

    /// <summary>
    /// Dock 所在显示器变更：落盘 + 走与图标尺寸同一条重排路径（App.ApplyDockLayout →
    /// DockWindow.RefreshLayoutFromSettings → ScheduleReposition 防抖 → PositionAsAppBar
    /// 用新屏的 rcMonitor/有效 DPI 重新协商 ABE_BOTTOM 条带并搬窗）。
    /// </summary>
    private void DockMonitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = DockMonitorBox.SelectedValue as string ?? "auto";
        if (value == SettingsService.Current.DockMonitor) return;   // ItemsSource 重设等引起的重复选择事件不回写
        SettingsService.UpdateAndSave(SettingsService.Current with { DockMonitor = value });
        App.ApplyDockLayout();
    }

    /// <summary>顶栏所在显示器变更：落盘 + 顶栏按新屏重新协商 ABE_TOP 条带（离散下拉事件，无需防抖）。</summary>
    private void TopBarMonitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = TopBarMonitorBox.SelectedValue as string ?? "auto";
        if (value == SettingsService.Current.TopBarMonitor) return;
        SettingsService.UpdateAndSave(SettingsService.Current with { TopBarMonitor = value });
        App.ApplyTopBarGeometry();
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
        // v4：切换器只剩 macOS 卡片；保留落盘路径，旧配置值由 Normalized 自愈。
        SettingsService.UpdateAndSave(SettingsService.Current with { SwitcherStyle = "macos" });
    }

    private void Passthrough_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = FullscreenPassthroughCheck.IsChecked == true;
        // 触发键当前固定（alt+tab），连同直通标志一并落盘并按新配置重装钩子
        App.ApplySwitcherHotkey(SettingsService.Current.SwitcherHotkey, on);
    }

    // ---------- 媒体 ----------

    /// <summary>Dock 自动隐藏（P3-T5 隐藏模式）：立即生效，不需要重启 Dock。</summary>
    private void DockAutoHide_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.ApplyDockAutoHide(DockAutoHideCheck.IsChecked == true);
    }

    /// <summary>Dock 小组件开关（Phase 7-T3）：立即重排行尾，不需要重启 Dock。</summary>
    private void DockWidget_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = DockWidgetCheck.IsChecked == true;
        SettingsService.UpdateAndSave(SettingsService.Current with { DockWidgetEnabled = on });
        App.ApplyDockWidget(on);
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
        SettingsService.UpdateAndSave(SettingsService.Current with { PreviewEnabled = PreviewCheck.IsChecked == true });
        // 即时生效（Task 13）：关闭立即收起正在显示的预览，开启则下次悬停放行（悬停路径每次读 Current）。
        App.ApplyPreviewEnabled();
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
        ThemeManager.ApplyAppearance(s.Appearance, Application.Current);
        _taskbar.Apply(SettingsService.PolicyOf(s));
        SettingsService.SetAutostart(s.Autostart);
        App.SetTopBarEnabled(s.TopBarEnabled);
        // 恢复默认后通知中心回到开启（控件状态在 RestoreDefaults 的统一 Apply 里刷新）
        NotificationCenterCheck.IsChecked = s.NotificationCenterEnabled;
        VolumeOsdCheck.IsChecked = s.VolumeOsdEnabled;
        NotificationBannerCheck.IsChecked = s.NotificationBannerEnabled;
        App.ApplyTopBarGeometry();   // 恢复默认后 TopBarMonitor 回到 auto：已存在的顶栏须按新屏重算条带
        App.ApplyTopBarClock();      // 恢复默认后时钟回到"日期+星期+24 小时制、不显示秒"（v3.0.0 观感）
        UpdateClockSample();
        App.SetSwitcherEnabled(s.SwitcherEnabled);
        // 恢复默认后必须连同"触发键 + 全屏直通"整体重放（Task 16 评审 Important#2）：
        // SwitcherService.SetHookEnabled 仅在钩子**未启动**时才回读 Hook.FullscreenPassthrough，
        // 上面那步若把钩子原样留着，直通旗标就停在旧值——文件/勾选框已回到默认 true 而钩子还拿着
        // false。ApplySwitcherHotkey 走 StopHook + 重装路径，两个值都会按当前设置重读。
        if (s.SwitcherEnabled)
            App.ApplySwitcherHotkey(s.SwitcherHotkey, s.FullscreenPassthrough);
        App.ApplyDockLayout();
        _mediaVm.CapsuleVisible = s.ShowMediaCapsule;
        _mediaVm.PanelAutoHideOnDeactivate = s.HidePanelOnDeactivate;
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

    // ---------- 自绘标题栏（macOS 三点）----------

    /// <summary>
    /// Win11 的圆角策略默认只覆盖"带标题栏的窗口"：WindowStyle=None 后系统会给一个方角窗口，
    /// 与 macOS 的圆润轮廓直接冲突，所以在这里显式请求 ROUND。走的是 DWM 自己的圆角——
    /// 客户区内容被 DWM 裁剪，不需要 AllowsTransparency，字体因此仍是次像素渲染。
    /// 返回值不检查：属性请求失败只是回到方角，不影响任何功能，为它写分支属于噪声。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle,
            NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>
    /// 绿点 = 置顶开关，不是"最大化"：本窗口 SizeToContent + MaxHeight，最大化只会得到一块
    /// 居中的窄条，是个没有意义的动作。置顶则有真实用途——Dock 条带与部分全屏窗口会压住设置页，
    /// 勾选态用 Tag="on" 驱动模板里的状态环（没有可见状态的开关会被当成坏掉的按钮）。
    /// </summary>
    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        ZoomDot.Tag = Topmost ? "on" : null;
        ZoomDot.ToolTip = Topmost ? "取消置顶" : "保持置顶";
    }
}
