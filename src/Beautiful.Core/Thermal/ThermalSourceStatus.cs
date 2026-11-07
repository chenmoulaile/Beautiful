namespace Beautiful.Core.Thermal;

/// <summary>
/// 温度数据源的获取旅程四态（设置卡状态行的口径，<b>纯数据</b>）。
/// 由 <see cref="ThermalSourceStatus.Resolve"/> 从「当前读数 + LHM 便携包安装态 + LHM WMI 可达性」三个事实推出：
/// <list type="number">
/// <item><see cref="NotFetched"/>：固定目录没有便携包，也没有任何读数——顶栏芯片保持隐藏。</item>
/// <item><see cref="FetchedNotRunning"/>：已解压到固定目录，但 root\LibreHardwareMonitor 不可达（没启动）。</item>
/// <item><see cref="RunningConnected"/>：WMI 命名空间可达（LibreHardwareMonitor 在运行），但还没读到 CPU 温度。</item>
/// <item><see cref="ConnectedWithData"/>：读到 CPU 温度（来源可以是 LHM，也可以是已装的其他工具/ACPI 热区）。</item>
/// </list>
/// </summary>
public enum ThermalDataSourceState
{
    NotFetched = 0,
    FetchedNotRunning,
    RunningConnected,
    ConnectedWithData,
}

/// <summary>一次数据源状态快照：四态 + 撑起它的三个事实（UI 状态行与日志共用，纯数据）。</summary>
/// <param name="State">旅程四态（<see cref="ThermalDataSourceState"/>）。</param>
/// <param name="Reading">当前读数（任何来源；无读数为 null）。</param>
/// <param name="LhmToolInstalled">LibreHardwareMonitor 便携包是否已在固定目录（exe 存在）。</param>
/// <param name="LhmNamespaceReachable">root\LibreHardwareMonitor 是否可查（LibreHardwareMonitor 在运行且发布 WMI）。</param>
public sealed record ThermalSourceStatus(
    ThermalDataSourceState State,
    ThermalReading? Reading,
    bool LhmToolInstalled,
    bool LhmNamespaceReachable)
{
    /// <summary>
    /// 从三个事实推出四态（<b>纯函数</b>，xunit 钉死——设置卡状态行的每一格都从这里来，不写第二份判定）：
    /// 有读数即「出数」（哪怕来源是 OHM/ACPI——状态行会带来源名，用户看到的是"温度有了"）；
    /// 没读数时按「LHM 在线 → 已装未启动 → 未获取」逐级回落。
    /// </summary>
    public static ThermalSourceStatus Resolve(ThermalReading? reading, bool lhmToolInstalled, bool lhmNamespaceReachable)
    {
        var state = reading is not null ? ThermalDataSourceState.ConnectedWithData
            : lhmNamespaceReachable ? ThermalDataSourceState.RunningConnected
            : lhmToolInstalled ? ThermalDataSourceState.FetchedNotRunning
            : ThermalDataSourceState.NotFetched;
        return new ThermalSourceStatus(state, reading, lhmToolInstalled, lhmNamespaceReachable);
    }

    /// <summary>四态短标签（状态行前缀 / 日志共用）。</summary>
    public static string Describe(ThermalDataSourceState state) => state switch
    {
        ThermalDataSourceState.NotFetched => "未获取",
        ThermalDataSourceState.FetchedNotRunning => "已获取未启动",
        ThermalDataSourceState.RunningConnected => "已启动已连接",
        _ => "已连接出数",
    };

    /// <summary>
    /// 状态行正文（不含「数据源状态：」前缀，<b>纯函数</b>钉死文案）：
    /// 出数态带来源与读数，其余态各给一句下一步指引。
    /// </summary>
    public static string DescribeStatusLine(ThermalSourceStatus status) => status.State switch
    {
        ThermalDataSourceState.ConnectedWithData when status.Reading is { } reading
            => $"已连接出数（{TemperatureReader.Describe(reading.Source)} · 当前 {reading.ChipText}）",
        ThermalDataSourceState.RunningConnected
            => "已启动已连接（LibreHardwareMonitor 在线，暂未读到 CPU 温度传感器）",
        ThermalDataSourceState.FetchedNotRunning
            => "已获取未启动（点「启动（需要管理员）」运行它；拒绝 UAC 也没关系，仍可用其他数据源）",
        _ => "未获取（点「获取温度数据源」从官方 GitHub 发布下载便携包）",
    };
}
