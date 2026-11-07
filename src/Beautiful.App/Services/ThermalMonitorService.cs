using System.Threading;
using System.Windows.Threading;
using Beautiful.Core.Thermal;

namespace Beautiful.App.Services;

/// <summary>顶栏温度芯片的一次状态更新。Text 为 null = 无数据源（UI 应整体隐藏芯片）。</summary>
public sealed record ThermalChipUpdate(string? Text, string? ToolTip);

/// <summary>
/// CPU 温度轮询（对标 MyDockFinder 温度显示）：<see cref="TemperatureProbe.ReadOnce"/> 按优先级探测
/// LibreHardwareMonitor → OpenHardwareMonitor → ACPI 热区，低频后台轮询，把**已格式化**的芯片状态
/// 经 <see cref="ChipChanged"/>（UI 线程）推给顶栏。
/// <para>
/// 【生命周期】App 在"开关开且顶栏在屏"时创建并 <see cref="Start"/>；设置开关/改间隔时整体重建；
/// 应用退出 Dispose（停表）。实例同时登记在 <see cref="Current"/>——顶栏（重）创建后用它立即同步
/// 一次当前状态，避免"重载顶栏后芯片闪空"。
/// </para>
/// <para>
/// 【容错】WMI 的一切异常都在探测层被吞掉（返回 null = 没有数据源）；本层对不可预期的异常同样
/// 全量捕获 + 只在**失败串开始**时记一条日志（5s 一拍连续失败不刷屏），恢复读数即重置——
/// 温度是纯装饰能力，任何故障都降级为"芯片隐藏"，绝不影响顶栏与主 UI。
/// </para>
/// <para>
/// 【节流】上一拍没读完就跳过这一拍（WMI 查询偶发慢），事件只在**芯片文字变化**时发布——
/// UI 线程不会每 5s 被打扰一次。
/// </para>
/// </summary>
public sealed class ThermalMonitorService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly System.Threading.Timer _timer;
    private readonly int _intervalSeconds;
    private int _reading;               // 0=空闲 1=读取中：防重入（上一拍未返回时跳过本拍）
    private bool _failureLogged;
    private bool _noSourceLogged;       // "无数据源"只在状态切换时记一条（5s 一拍不刷屏）
    private bool _disposed;
    private string? _lastPublishedText;

    /// <summary>当前活跃实例（App 创建/销毁；顶栏（重）创建后经 <see cref="PublishLatest"/> 同步首帧）。</summary>
    public static ThermalMonitorService? Current { get; private set; }

    /// <summary>芯片状态变化（UI 线程触发）。TopBarWindow 订阅；无数据时 Text 为 null。</summary>
    public static event EventHandler<ThermalChipUpdate>? ChipChanged;

    /// <summary>轮询间隔（秒，AppSettings.ThermalRefreshSeconds 已钳制 5..120）。</summary>
    public int IntervalSeconds => _intervalSeconds;

    /// <summary>最近一次成功读数（含来源）；从未成功或数据源消失时为 null。</summary>
    public ThermalReading? Latest { get; private set; }

    /// <summary>最近一拍维持的数据源状态快照（未 Start 时为构造初值；设置卡有 ChipChanged 推送时的快速来源）。</summary>
    private ThermalSourceStatus _status = ThermalSourceStatus.Resolve(null, lhmToolInstalled: false, lhmNamespaceReachable: false);

    /// <summary>数据源状态查询接口：返回轮询最近一拍维持的四态快照（不发起 WMI 查询；要新鲜数据用 <see cref="ProbeSourceStatus"/>）。</summary>
    public ThermalSourceStatus GetSourceStatus() => _status;

    /// <summary>
    /// 数据源状态一次性探测（设置卡打开 / 动作完成后要新鲜数据时用；<b>有 WMI 查询，调后台线程</b>）：
    /// 服务在不在跑都可直接调——走同一条 <see cref="TemperatureProbe.ReadOnceDetailed"/> 探测链，
    /// 状态四态由 Core 纯函数 <see cref="ThermalSourceStatus.Resolve"/> 收敛。
    /// </summary>
    public static ThermalSourceStatus ProbeSourceStatus()
    {
        var outcome = TemperatureProbe.ReadOnceDetailed();
        return ThermalSourceStatus.Resolve(
            outcome.Reading,
            LibreHardwareMonitorTool.IsInstalled(LibreHardwareMonitorTool.DefaultPaths),
            outcome.LhmNamespaceReachable);
    }

    public ThermalMonitorService(int intervalSeconds, Dispatcher dispatcher)
    {
        _intervalSeconds = intervalSeconds;
        _dispatcher = dispatcher;
        // dueTime=0：启动即读首拍（芯片不必等一个间隔才出现）。
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>启动轮询表。构造后必须调用（构造期不投递任何事件）。</summary>
    public void Start()
    {
        if (_disposed) return;
        Current = this;
        var period = TimeSpan.FromSeconds(_intervalSeconds);
        _timer.Change(TimeSpan.Zero, period);
        Logger.Info($"温度监控已启动 间隔={_intervalSeconds}s");
    }

    /// <summary>
    /// 顶栏（重）创建后的首帧同步：有活跃实例就发布它当前状态（有数据给数据、没数据给空），
    /// 没有活跃实例就发布空（芯片保持隐藏）。幂等、随时可调。
    /// </summary>
    public static void PublishLatest()
    {
        var service = Current;
        if (service is null)
        {
            ChipChanged?.Invoke(null, new ThermalChipUpdate(null, null));
            return;
        }

        var reading = service.Latest;
        // 投递端自查 _disposed：Dispose 与本投递在 UI 线程上的先后顺序不定，已被停掉的表
        // 不得再把芯片"复活"（App 停表后会显式隐藏芯片，迟到的显示事件必须作废）。
        service._dispatcher.BeginInvoke(() =>
        {
            if (service._disposed) return;
            ChipChanged?.Invoke(service, ToUpdate(reading));
        });
    }

    private void OnTick(object? state)
    {
        if (_disposed || Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return;
        try
        {
            // 同一条探测链（ReadOnce 的带可达性回执版）：读数喂芯片，可达性 + 安装态喂状态四态。
            var outcome = TemperatureProbe.ReadOnceDetailed();
            var reading = outcome.Reading;
            _status = ThermalSourceStatus.Resolve(
                reading,
                LibreHardwareMonitorTool.IsInstalled(LibreHardwareMonitorTool.DefaultPaths),
                outcome.LhmNamespaceReachable);
            if (_disposed) return;
            var hadData = Latest is not null;
            Latest = reading;
            var update = ToUpdate(reading);
            _failureLogged = false;

            // 数据源出现/消失都留一条痕迹：用户问"为什么没有温度"时，日志要能直接回答
            // （没装 LHM/OHM 就是"没有可用数据源"，而不是让我们对着空芯片猜）。
            if (reading is null && !_noSourceLogged)
            {
                _noSourceLogged = true;
                Logger.Info("温度探测：没有可用数据源（LibreHardwareMonitor/OpenHardwareMonitor 未发布 WMI，ACPI 热区不可用）→ 芯片保持隐藏");
            }
            else if (reading is not null)
            {
                _noSourceLogged = false;
                if (!hadData)
                    Logger.Info($"温度探测：数据源就绪（{TemperatureReader.Describe(reading.Source)}，当前 {reading.ChipText}）");
            }

            if (update.Text == _lastPublishedText) return;   // 芯片文字没变：不打扰 UI
            _lastPublishedText = update.Text;
            // 投递端自查 _disposed：Dispose 与本投递的先后顺序不定，停掉的表不得再驱动 UI
            //（否则 App 停表→隐藏芯片 之后，一拍在途事件会把芯片又顶出来且再无人收走）。
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                ChipChanged?.Invoke(this, update);
            });
        }
        catch (Exception ex)
        {
            // 探测层已吞 WMI 异常；这里是最后一道网（Dispatcher 排队失败等）。
            // 温度是纯装饰能力：失败降级为"没有新读数"，只在失败串开始时记一条。
            if (!_failureLogged)
            {
                _failureLogged = true;
                Logger.Warn($"温度轮询失败（继续按当前状态显示）: {ex.GetType().Name} {ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reading, 0);
        }
    }

    private static ThermalChipUpdate ToUpdate(ThermalReading? reading)
        => reading is null
            ? new ThermalChipUpdate(null, null)
            : new ThermalChipUpdate(reading.ChipText,
                $"{reading.ChipText} · 来源 {TemperatureReader.Describe(reading.Source)}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        // 停表。已投递但未执行的事件在执行端自查 _disposed 作废（见两处 BeginInvoke），所以这里
        // 不需要也不去等在途回调。Dispose 不发布"隐藏"——关开关时 App 会显式调 TopBar.HideThermalChip；
        // 改间隔的重建不该让芯片闪一下空。
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
    }
}
