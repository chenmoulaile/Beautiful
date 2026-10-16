using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.App.TopBar;
using Beautiful.Core.Controls;
using Beautiful.Core.Layout;
using Brush = System.Windows.Media.Brush; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brush 冲突
using Brushes = System.Windows.Media.Brushes; // 同上
using RadioButton = System.Windows.Controls.RadioButton; // 同上（分段选项：WPF 只有 RadioButton 带 GroupName）
using UserControl = System.Windows.Controls.UserControl; // 同上（WinForms 同名 UserControl）
using App = Beautiful.App.App; // 本类在 Beautiful.App.Controls 下，显式绑定 App 类避免歧义

namespace Beautiful.App.Controls;

/// <summary>
/// 控制中心面板（v3 T7）：顶栏右端图标唤出的 macOS/Win11 风格快速设置面。
/// 由 <see cref="FluentPopupHost"/> 承载（与托盘菜单同一套分层窗口 + 抓屏模糊 + 边缘翻转规则），
/// <b>瞬态生命周期</b>：每次打开新建一个实例、关闭即 <see cref="Shutdown"/> 释放音频 COM 引用。
/// <para>
/// 【为什么每次重建而不是缓存实例】控制中心的契约就是"打开那一刻读到的是真状态"
/// （当前主题、任务栏策略、各开关、系统音量的实际值）。缓存实例要保新鲜就得订阅一堆事件并小心退订，
/// 而本项目已经被"半构造对象挂在静态事件上"咬过多次；重建把这类问题一次性消掉
/// （同 <see cref="CalendarFlyout"/> 的取舍：短暂生命周期 → 构造期取当前主题，不订阅 ThemeChanged）。
/// </para>
/// <para>
/// 【每个控件都真的动东西，且只有三条落地路径】
/// ① 主题 → <see cref="TopBarContext.ApplyTheme"/>（托盘菜单/设置窗口同一条：落盘 + ThemeManager.Apply）；
/// ② 任务栏策略/四个开关 → App 的 Set* 入口（落盘 + TaskbarManager.Apply / App.ApplyDockLayout / 媒体 VM 旗标）；
/// ③ 音量 → <see cref="SystemVolumeService"/>（Core Audio 端点，真写系统）。
/// "什么算开、点了写什么值"这些判断全在 <see cref="QuickSettings"/> / <see cref="VolumeMath"/>（Core，有单测），
/// 本类只做渲染与事件接线——所以界面与设置页永远不会对同一个设置项给出两种解释。
/// </para>
/// </summary>
public partial class ControlCenterPanel : UserControl, IBackdropAware, IPopupShadow
{
    /// <summary>投影留白 = XAML 里 <c>Shell.Margin</c> 的唯一来源，宿主据此对齐<b>卡片</b>而非窗口外框。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    /// <summary>主题分段的 ToggleButton 组名（同组互斥；也用作 <see cref="Segment_Checked"/> 的分派键）。</summary>
    private const string ThemeGroup = "ccTheme";

    /// <summary>任务栏策略分段的组名。</summary>
    private const string PolicyGroup = "ccPolicy";

    private readonly TopBarContext _ctx;
    private readonly SystemVolumeService _volume = new();

    /// <summary>初始化/回读期间的屏蔽位：程序改 IsChecked/Value 不得回写系统（同 SettingsWindow 的惯用法）。</summary>
    private bool _loading = true;

    /// <summary>静音旗标缓存（只由端点回读刷新，绝不由界面推测）。</summary>
    private bool _muted;

    /// <summary>承载本面板的弹层宿主（由 <see cref="Open"/> 在显示前注入）。</summary>
    private FluentPopupHost? _host;

    public ControlCenterPanel(TopBarContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        // 先将面板整体置入"加载中"再建控件：Build*/Refresh* 会勾选中前项、设滑杆值，
        // 这些都必须只影响观感，不能在构造期就把设置与系统音量反写一遍。
        BuildThemeSegments();
        BuildPolicySegments();
        BuildTiles();
        RefreshVolume();
        _loading = false;
    }

    /// <summary>注入宿主（打开方在 ShowAt 之前调用）。</summary>
    public void AttachHost(FluentPopupHost host) => _host = host;

    // ---------- 背板（与 FluentMenu 完全同一套两层材质） ----------

    /// <summary>
    /// 拿到抓屏模糊 → 卡底铺该画刷并在其上压 <c>CardFillBlur</c> 深色遮罩；拿不到（抓屏失败 /
    /// <c>BEAUTIFUL_NOBLUR=1</c> / 瞄准越界）→ 卡底铺实心 <c>CardFillSolid</c>、遮罩透明。
    /// 两条分支都铺满整张卡：宿主是分层窗口，"没画到"就是能看穿到桌面的洞。
    /// </summary>
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

    // ---------- 分段：主题 / 系统任务栏 ----------

    /// <summary>
    /// 主题三选一。列表与当前选中项都来自 <see cref="QuickSettings"/> 的投影，界面不认识任何白名单常量。
    /// 点击走 <see cref="TopBarContext.ApplyTheme"/>——与托盘菜单"主题▸"完全同一个委托，
    /// 于是控制中心切主题 = 落盘 + ThemeManager.Apply + Dock/顶栏/切换面板重落参数，一条不差。
    /// </summary>
    private void BuildThemeSegments()
    {
        var selected = QuickSettings.SelectedThemeId(SettingsService.Current);
        BuildSegments(ThemeHost, QuickSettings.Themes, ThemeGroup, selected);
        ThemeLabel.Text = $"当前主题：{ThemeManager.Current.DisplayName}";
    }

    /// <summary>系统任务栏三选一：落盘 + <see cref="TaskbarManager.Apply"/>（App.SetTaskbarPolicy 内做）。</summary>
    private void BuildPolicySegments()
    {
        var selected = QuickSettings.SelectedTaskbarPolicyId(SettingsService.Current);
        BuildSegments(PolicyHost, QuickSettings.TaskbarPolicies, PolicyGroup, selected);
        PolicyHintText.Text = QuickSettings.PolicyHint(selected);
    }

    /// <summary>把一组分段选项铺成 UniformGrid 里的 RadioButton，并把当前值那一项选上（<see cref="_loading"/> 屏蔽回写）。</summary>
    private void BuildSegments(UniformGrid host, IReadOnlyList<QuickOption> options, string group, string selectedId)
    {
        var previous = _loading;
        _loading = true;
        try
        {
            host.Children.Clear();
            foreach (var option in options)
            {
                var radio = new RadioButton
                {
                    Content = option.Label,
                    Tag = option.Id,
                    GroupName = group,
                    Style = (Style)FindResource("CcSegmentStyle"),
                    ToolTip = option.Label,
                    IsChecked = option.Id == selectedId,
                };
                // UIA 名称：弹层是激活式窗口（键盘可达），有名称后屏幕阅读器与自动化脚本都能指名这一项
                AutomationProperties.SetName(radio, $"控制中心 {option.Label}");
                radio.Checked += Segment_Checked;
                host.Children.Add(radio);
            }
        }
        finally { _loading = previous; }
    }

    /// <summary>
    /// 分段被点。<b>按 GroupName 分派而不是往投影里塞委托</b>：Core 那侧只给"有哪些项/当前是哪项"，
    /// 落地动作留在这里，于是 Core 保持可单测、面板保持只有两条真正的落地调用。
    /// 主题点击后必须重投影砖块（主题决定"放大"是否可用），排在 Background 优先级以避开在本次
    /// Checked 事件里拆掉正被遍历的控件树。
    /// </summary>
    private void Segment_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton radio || radio.Tag is not string id) return;
        switch (radio.GroupName)
        {
            case ThemeGroup:
                _ctx.ApplyTheme(id);
                ThemeLabel.Text = $"当前主题：{ThemeManager.Current.DisplayName}";
                RebuildTilesDeferred();
                break;
            case PolicyGroup:
                App.SetTaskbarPolicy(id);
                PolicyHintText.Text = QuickSettings.PolicyHint(QuickSettings.SelectedTaskbarPolicyId(SettingsService.Current));
                break;
        }
    }

    // ---------- 开关砖块 ----------

    /// <summary>
    /// 四个开关砖块：完全按 <see cref="QuickSettings.ToggleTiles"/> 的投影生成（含置灰与原因）。
    /// 其中"壁纸取色"的一块要问 <see cref="WallpaperAccentService"/> 当前读不读得到壁纸文件——
    /// 读不到（纯色桌面/幻灯片）就灰着写原因，而不是点上去后画面一动不动。
    /// </summary>
    private void BuildTiles()
    {
        var previous = _loading;
        _loading = true;
        try
        {
            var settings = SettingsService.Current;
            var theme = ThemeManager.Current;
            var accentAvailable = WallpaperAccentService.IsWallpaperReadable();
            TileHost.Children.Clear();
            foreach (var tile in QuickSettings.ToggleTiles(settings, theme, accentAvailable))
            {
                var toggle = new ToggleButton
                {
                    Content = tile.Label,
                    Tag = tile.Id,
                    Style = (Style)FindResource("CcTileStyle"),
                    IsChecked = tile.IsOn,
                    IsEnabled = tile.IsEnabled,
                    // 置灰时样式把这一行文本抄到砖块的状态行上；可用时它就是普通提示，不打扰
                    ToolTip = tile.BlockReason ?? $"{tile.Label}（当前：{(tile.IsOn ? "开" : "关")}）",
                };
                AutomationProperties.SetName(toggle, $"控制中心 {tile.Label}");
                toggle.Checked += Tile_Changed;
                toggle.Unchecked += Tile_Changed;
                TileHost.Children.Add(toggle);
            }
        }
        finally { _loading = previous; }
    }

    /// <summary>
    /// 砖块被点：把新状态交给 App 的落地入口（落盘 + 即时重放），然后延迟重投影一次回显。
    /// 回显而不是"信界面"的理由：Dock 缺席时 App 侧是 no-op、倒影写入后还会过
    /// <see cref="Beautiful.Core.Settings.AppSettings.Normalized"/> 的钳制——界面必须显示**设置里的真值**，
    /// 而不是点击意图（否则会出现"砖块亮着但配置没变"的假状态）。
    /// </summary>
    private void Tile_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleButton toggle || toggle.Tag is not string id) return;
        bool on = toggle.IsChecked == true;
        switch (id)
        {
            case QuickSettings.MagnificationId: App.SetMagnification(on); break;
            case QuickSettings.ReflectionId: App.SetReflection(on); break;
            case QuickSettings.MediaCapsuleId: App.SetMediaCapsule(on); break;
            case QuickSettings.WallpaperAccentId: App.SetWallpaperAccent(on); break;
            case QuickSettings.TaskbarGlassId: App.SetTaskbarGlass(on); break;
            default: break;   // 未知砖块 Id：不写任何东西，下面的重投影会把界面拉回真值
        }
        RebuildTilesDeferred();
    }

    /// <summary>把砖块重投影排到 Background 优先级（不在本次 Checked 事件里拆掉自己所在的控件树）。</summary>
    private void RebuildTilesDeferred()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BuildTiles));

    // ---------- 音量 ----------

    /// <summary>
    /// 从端点回读音量并刷界面。<b>读失败也走置灰路径</b>：整行禁用 + 中文原因，
    /// 绝不保留上一次的数字当"当前音量"（那正是"伪造读数"）。
    /// </summary>
    private void RefreshVolume()
    {
        var previous = _loading;
        _loading = true;
        try
        {
            if (!_volume.IsAvailable)
            {
                DisableVolumeRow(_volume.UnavailableReason ?? "音频接口不可用");
                return;
            }
            if (!_volume.TryGetVolume(out var scalar, out _muted))
            {
                DisableVolumeRow("读取音量失败（端点可能已被拔出或禁用）");
                return;
            }
            VolumeReason.Visibility = Visibility.Collapsed;
            VolumeSlider.IsEnabled = true;
            MuteButton.IsEnabled = true;
            VolumeSlider.Value = VolumeMath.SliderPercent(scalar);
            VolumeText.Text = _muted ? "已静音" : $"{VolumeMath.ToPercent(scalar)}%";
            MuteButton.ToolTip = _muted ? "已静音，点击解除静音" : "点击静音";
            MuteButton.Opacity = _muted ? 0.55 : 1.0;
            VolumeSlider.ToolTip = $"系统音量 {VolumeMath.ToPercent(scalar)}%";
        }
        finally { _loading = previous; }
    }

    private void DisableVolumeRow(string reason)
    {
        VolumeSlider.IsEnabled = false;
        MuteButton.IsEnabled = false;
        VolumeSlider.Value = 0;
        VolumeText.Text = "不可用";
        VolumeReason.Text = reason;
        VolumeReason.Visibility = Visibility.Visible;
        VolumeRowToolTip(reason);
    }

    private void VolumeRowToolTip(string reason)
    {
        VolumeSlider.ToolTip = reason;
        MuteButton.ToolTip = reason;
    }

    /// <summary>
    /// 滑杆改动 → 真写系统音量。这里是即时写（不是防抖）：音量的反馈环就在听觉上，
    /// 防抖会让用户拖完滑杆听不到变化，也不会比直接写更省（端点写入本身没有磁盘 IO）。
    /// 写失败（拔设备瞬间）→ 回读端点真值重画，界面绝不停在一个"看着改了其实没改"的位置。
    /// </summary>
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
        // 静音旗标可能刚被这一次拖动清掉（见 VolumeMath.ApplyDrag），文案跟着真值走而不是用户的旧状态
        VolumeText.Text = _muted ? "已静音" : $"{percent}%";
        MuteButton.Opacity = _muted ? 0.55 : 1.0;
        MuteButton.ToolTip = _muted ? "已静音，点击解除静音" : "点击静音";
        VolumeSlider.ToolTip = $"系统音量 {percent}%";
    }

    /// <summary>点静音：写旗标后一律回读端点真值重画（<see cref="SystemVolumeService.ToggleMute"/> 内部
    /// 已处理"0% 端点上取消静音要给一个可闻下限"的边界，界面不需要复述这条规则）。</summary>
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _volume.ToggleMute();
        RefreshVolume();
    }

    // ---------- 拖滑杆期间抑制"光标离开即关" ----------

    /// <summary>
    /// 拖动滑杆时把宿主 <see cref="FluentPopupHost.KeepOpen"/> 置位：宿主的看门狗按光标是否还在
    /// 弹层矩形里决定关闭，而拖到最大/最小值时光标很容易越出卡片边缘 —— 没有这个开关就会出现
    /// "音量条拖到一半面板自己关了"。<see cref="LostMouseCapture"/> 兜住"松手在窗口外"的路径。
    /// </summary>
    private void Volume_DragBegin(object sender, MouseButtonEventArgs e) => SetKeepOpen(true);

    private void Volume_DragEnd(object sender, RoutedEventArgs e) => SetKeepOpen(false);

    private void SetKeepOpen(bool keep)
    {
        if (_host is not null) _host.KeepOpen = keep;
    }

    // ---------- 底部动作：复用托盘/顶栏菜单的同一批委托 ----------

    /// <summary>打开设置…：与 logo 菜单"设置…"同一个 <see cref="TopBarContext.OpenSettings"/>（App.OpenSettings 单例语义）。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => CloseThenRun(_ctx.OpenSettings);

    /// <summary>重载 Dock：与托盘菜单同一个 <see cref="TopBarContext.ReloadDock"/>。重载会销毁并重建顶栏，
    /// 所以必须先关面板再执行（否则本面板所在的宿主会被连带销毁在半路上）。</summary>
    private void Reload_Click(object sender, RoutedEventArgs e) => CloseThenRun(_ctx.ReloadDock);

    /// <summary>
    /// 先拆弹层、再把动作排到 Input 优先级执行（与 <see cref="FluentMenu"/> 的叶子命中同一套时序）：
    /// 动作里可能有 MessageBox、也可能有关闭/重建顶栏，跟着弹层拆除同一条栈跑会互相抢激活与析构顺序。
    /// </summary>
    private void CloseThenRun(Action action)
    {
        _host?.Close();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, action);
    }

    // ---------- 释放 ----------

    /// <summary>
    /// 面板拆除（宿主 Closed 时由打开方调用）：释放音频 COM 引用 + 取消未完成的滑杆保活标记。
    /// 幂等，且刻意不订阅任何静态事件——本面板没有需要对称解绑的静态挂点。
    /// </summary>
    public void Shutdown()
    {
        SetKeepOpen(false);
        _host = null;
        _volume.Dispose();
    }
}
