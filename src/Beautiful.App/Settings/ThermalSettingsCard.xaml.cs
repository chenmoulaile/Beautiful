using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.Core.Settings;
using Beautiful.Core.Thermal;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 FluentMenu.xaml.cs 同法）：
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Settings;

/// <summary>
/// 温度芯片设置卡（独立 UserControl，自包含样式）：开关 + 刷新间隔 + 数据源四态状态行
/// + 数据源动作（获取便携包 / 管理员启动 / 打开目录）。
/// <para>
/// 【接线契约】不触碰设置窗口宿主文件——宿主只需在"系统"分区插入本控件一处标记；
/// 值的写入统一走 <see cref="App.SetThermalChip"/>（落盘 + 即时重建/停掉轮询），
/// 与设置窗口其它开关同一条路径，不存在第二份持久化逻辑。
/// 数据源动作全部走 Core <see cref="LibreHardwareMonitorTool"/>：只放文件与拉起进程，
/// 绝不静默安装任何驱动（LibreHardwareMonitor 管理员运行时装载自带驱动是它自己的行为）。
/// </para>
/// <para>
/// 【状态行】四态（未获取 / 已获取未启动 / 已启动已连接 / 已连接出数）文案由 Core
/// <see cref="ThermalSourceStatus"/> 纯函数钉死。新鲜状态走 <see cref="ThermalMonitorService.ProbeSourceStatus"/>
/// （有 WMI 查询，放后台线程，Loaded 时与 3s 定时器各来一次——后者捕捉"用户自己启动了
/// LibreHardwareMonitor"的就绪转变）；轮询推来的 <see cref="ThermalMonitorService.ChipChanged"/>
/// 只读服务快照，不重复发查询。
/// </para>
/// </summary>
public partial class ThermalSettingsCard : UserControl
{
    /// <summary>预设间隔（秒）——与 XAML 里 ComboBoxItem 的 Tag 顺序一一对应。</summary>
    private static readonly int[] PresetSeconds = [5, 10, 15, 30, 60, 120];

    /// <summary>程序化赋值期间为 true：控件自身事件不回写设置（避免"加载即保存"）。</summary>
    private bool _syncing;

    /// <summary>数据源动作（获取/启动）进行中：按钮禁用防重入，动作完成后的刷新会解除。</summary>
    private bool _busy;

    /// <summary>最近一次摆上状态行的快照（忙态下更新按钮用的兜底，避免再发探测）。</summary>
    private ThermalSourceStatus _lastStatus = ThermalSourceStatus.Resolve(null, false, false);

    /// <summary>卸载计数：异步刷新回场时比对，被卸载后的迟到结果一律丢弃。</summary>
    private int _refreshEpoch;

    /// <summary>
    /// 设置卡存活期间的轻量定时器：10s 一拍新鲜探测（后台线程），捕捉数据源的就绪转变。
    /// 【wp-borrow 收口 2026-11-10：3s→10s】就绪转变以分钟计（用户去装/启动 LHM），3s 的 WMI 查询
    /// 比芯片自身轮询默认 5s 还密，AppSettings.ThermalRefreshSeconds 的口径（5s 一拍足够）同理适用；
    /// 有 _syncing 防重入与 Unloaded 即停边界，频率不影响正确性、只影响实时性。
    /// </summary>
    private readonly DispatcherTimer _statusTimer;

    public ThermalSettingsCard()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusTimer.Tick += (_, _) => _ = RefreshAsync(probeFresh: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThermalMonitorService.ChipChanged += OnChipChanged;
        SyncFromSettings();
        _statusTimer.Start();
        _ = RefreshAsync(probeFresh: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Stop();
        _refreshEpoch++;                       // 在途的异步刷新（含 3s 定时器那一拍）作废
        ThermalMonitorService.ChipChanged -= OnChipChanged;
    }

    /// <summary>
    /// 宿主"恢复默认"后调用：从当前设置重读勾选态与刷新间隔（不触发写回，程序化赋值包在
    /// <see cref="_syncing"/> 里）。与 StageManagerSettingsCard.ReloadFromSettings 同一接线约定——
    /// 本卡只在 Loaded 时回读一次，恢复默认把 ThermalChipEnabled/ThermalRefreshSeconds 重置为
    /// 默认后若不回读，勾选态会停在旧值（设置窗口 ReapplyAll 末尾调用）。
    /// </summary>
    public void ReloadFromSettings() => SyncFromSettings();

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

    /// <summary>
    /// 刷新状态行与按钮态。<paramref name="probeFresh"/> = 走后台线程的新鲜探测（有 WMI 查询）；
    /// false = 只读轮询服务快照（ChipChanged 推来时的新状态就在快照里，不值得再查一次 WMI）。
    /// </summary>
    private async Task RefreshAsync(bool probeFresh)
    {
        var epoch = _refreshEpoch;
        ThermalSourceStatus status;
        if (probeFresh)
        {
            try
            {
                status = await Task.Run(ThermalMonitorService.ProbeSourceStatus);
            }
            catch (Exception)
            {
                // 探测层已吞 WMI 异常，这里是最后一道网：刷新失败只保持上一帧，绝不打扰设置窗口。
                if (IsLoaded && epoch == _refreshEpoch) UpdateButtons(_lastStatus);
                return;
            }
            if (!IsLoaded || epoch != _refreshEpoch) return;   // 已卸载/重入：丢弃迟到结果
        }
        else
        {
            status = ThermalMonitorService.Current?.GetSourceStatus() ?? _lastStatus;
        }

        _lastStatus = status;
        SourceStatusText.Text = ThermalEnabledCheck.IsChecked != true
            ? "数据源状态：温度监控已关闭"
            : "数据源状态：" + ThermalSourceStatus.DescribeStatusLine(status);
        UpdateButtons(status);
    }

    /// <summary>
    /// 按钮可用态：LibreHardwareMonitor 在线（命名空间可达或正由它出数）时 exe 被锁——
    /// 「获取」重装必失败、「启动」重复无意义，两者禁用；「打开数据源目录」只要安装了就可用。
    /// </summary>
    private void UpdateButtons(ThermalSourceStatus status)
    {
        var lhmRunning = status.LhmNamespaceReachable
            || (status.Reading is { } reading && reading.Source == ThermalSource.LibreHardwareMonitor);
        FetchSourceButton.IsEnabled = !_busy && !lhmRunning;
        LaunchSourceButton.IsEnabled = !_busy && status.LhmToolInstalled && !lhmRunning;
        OpenToolFolderButton.IsEnabled = status.LhmToolInstalled;
    }

    /// <summary>操作结果提示行（一行字，说清发生了什么 / 下一步做什么）。</summary>
    private void ShowActionHint(string text)
    {
        ActionHintText.Text = text;
        ActionHintText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>轮询推来新状态（UI 线程）：读快照刷新，不再发探测。参数忽略——统一从 Current 取，少一份状态。</summary>
    private void OnChipChanged(object? sender, ThermalChipUpdate update) => _ = RefreshAsync(probeFresh: false);

    // ---------- 数据源动作（全部委托 Core，本层只管忙态与文案） ----------

    private async void FetchSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ShowActionHint($"正在获取 LibreHardwareMonitor {LibreHardwareMonitorTool.PinnedVersion} 便携包（约 1.9 MB，官方 GitHub 发布）…");
        UpdateButtons(_lastStatus);
        try
        {
            var result = await Task.Run(() =>
                LibreHardwareMonitorTool.FetchPortableAsync(LibreHardwareMonitorTool.DefaultPaths));
            ShowActionHint(result.Success
                ? result.Message + " 点「启动（需要管理员）」运行它。"
                : "获取失败：" + result.Message);
        }
        catch (Exception ex)
        {
            ShowActionHint($"获取失败（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            _busy = false;
            if (IsLoaded) await RefreshAsync(probeFresh: true);
        }
    }

    private async void LaunchSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ShowActionHint("正在请求管理员权限（请在 UAC 弹窗确认）…");
        UpdateButtons(_lastStatus);
        try
        {
            // UAC 弹窗会阻塞到用户答复：放后台线程，不冻设置窗口。
            var result = await Task.Run(() =>
                LibreHardwareMonitorTool.LaunchElevated(LibreHardwareMonitorTool.DefaultPaths));
            ShowActionHint(result.Message);
        }
        catch (Exception ex)
        {
            ShowActionHint($"启动失败（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            _busy = false;
            if (IsLoaded) await RefreshAsync(probeFresh: true);
        }
    }

    private void OpenToolFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!LibreHardwareMonitorTool.OpenInstallDirectory(LibreHardwareMonitorTool.DefaultPaths))
            ShowActionHint("数据源目录还不存在：先点「获取温度数据源」下载并解压便携包。");
    }

    private void ThermalEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.SetThermalChip(ThermalEnabledCheck.IsChecked == true, SelectedSeconds());
        _ = RefreshAsync(probeFresh: false);
    }

    private void RefreshSeconds_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        App.SetThermalChip(ThermalEnabledCheck.IsChecked == true, SelectedSeconds());
        _ = RefreshAsync(probeFresh: false);
    }

    /// <summary>当前选中的间隔（秒）；未选中时回默认 5（AppSettings 常量同源）。</summary>
    private int SelectedSeconds()
    {
        var index = RefreshSecondsBox.SelectedIndex;
        return index >= 0 && index < PresetSeconds.Length ? PresetSeconds[index] : AppSettings.DefaultThermalRefreshSeconds;
    }
}
