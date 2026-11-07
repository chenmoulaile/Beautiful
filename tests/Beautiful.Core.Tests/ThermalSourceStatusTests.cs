using Beautiful.Core.Thermal;
using Xunit;

/// <summary>
/// 数据源状态四态测试：三个事实（读数 / 安装态 / LHM 命名空间可达性）→ 四态的收敛规则，
/// 与状态行文案。这是设置卡"数据源状态"行的全部判定来源——显示错状态比不显示更误导，
/// 在 Core 层钉死后 App 侧没有第二份判定。
/// </summary>
public class ThermalSourceStatusTests
{
    private static readonly ThermalReading LhmReading = new(45.0, ThermalSource.LibreHardwareMonitor);
    private static readonly ThermalReading OhmReading = new(52.0, ThermalSource.OpenHardwareMonitor);
    private static readonly ThermalReading AcpiReading = new(61.0, ThermalSource.AcpiThermalZone);

    // ---------- Resolve：四态收敛 ----------

    [Theory]
    [InlineData(ThermalSource.LibreHardwareMonitor)]
    [InlineData(ThermalSource.OpenHardwareMonitor)]
    [InlineData(ThermalSource.AcpiThermalZone)]
    public void 有读数即已连接出数_无论来源(ThermalSource source)
    {
        var status = ThermalSourceStatus.Resolve(new ThermalReading(45.0, source),
            lhmToolInstalled: false, lhmNamespaceReachable: false);
        Assert.Equal(ThermalDataSourceState.ConnectedWithData, status.State);
        Assert.NotNull(status.Reading);
    }

    [Fact]
    public void 无读数但LHM命名空间可达_已启动已连接()
    {
        var status = ThermalSourceStatus.Resolve(null, lhmToolInstalled: true, lhmNamespaceReachable: true);
        Assert.Equal(ThermalDataSourceState.RunningConnected, status.State);
        Assert.Null(status.Reading);
        Assert.True(status.LhmNamespaceReachable);
    }

    [Fact]
    public void 无读数已安装未启动_已获取未启动()
    {
        var status = ThermalSourceStatus.Resolve(null, lhmToolInstalled: true, lhmNamespaceReachable: false);
        Assert.Equal(ThermalDataSourceState.FetchedNotRunning, status.State);
        Assert.True(status.LhmToolInstalled);
        Assert.False(status.LhmNamespaceReachable);
    }

    [Fact]
    public void 无读数未安装_未获取()
    {
        var status = ThermalSourceStatus.Resolve(null, lhmToolInstalled: false, lhmNamespaceReachable: false);
        Assert.Equal(ThermalDataSourceState.NotFetched, status.State);
    }

    [Fact]
    public void 读数优先于安装态_温度来自ACPI且便携包已装时算出数()
    {
        // 用户已获取便携包但没启动，ACPI 热区兜底出数：旅程停在"出数"，不是"已获取未启动"。
        var status = ThermalSourceStatus.Resolve(AcpiReading, lhmToolInstalled: true, lhmNamespaceReachable: false);
        Assert.Equal(ThermalDataSourceState.ConnectedWithData, status.State);
        Assert.Same(AcpiReading, status.Reading);
    }

    [Fact]
    public void OHM出数时LHM可达性事实照实携带()
    {
        var status = ThermalSourceStatus.Resolve(OhmReading, lhmToolInstalled: false, lhmNamespaceReachable: false);
        Assert.Equal(ThermalDataSourceState.ConnectedWithData, status.State);
        Assert.False(status.LhmToolInstalled);
        Assert.False(status.LhmNamespaceReachable);
    }

    // ---------- Describe：四态短标签 ----------

    [Theory]
    [InlineData(ThermalDataSourceState.NotFetched, "未获取")]
    [InlineData(ThermalDataSourceState.FetchedNotRunning, "已获取未启动")]
    [InlineData(ThermalDataSourceState.RunningConnected, "已启动已连接")]
    [InlineData(ThermalDataSourceState.ConnectedWithData, "已连接出数")]
    public void 四态短标签文案(ThermalDataSourceState state, string expected)
        => Assert.Equal(expected, ThermalSourceStatus.Describe(state));

    // ---------- DescribeStatusLine：状态行正文钉死 ----------

    [Fact]
    public void 出数态带来源与当前读数()
    {
        var line = ThermalSourceStatus.DescribeStatusLine(
            ThermalSourceStatus.Resolve(LhmReading, true, true));
        Assert.Equal("已连接出数（LibreHardwareMonitor · 当前 45°）", line);
    }

    [Fact]
    public void 已连接态给出下一步指引()
    {
        var line = ThermalSourceStatus.DescribeStatusLine(
            ThermalSourceStatus.Resolve(null, true, true));
        Assert.Equal("已启动已连接（LibreHardwareMonitor 在线，暂未读到 CPU 温度传感器）", line);
    }

    [Fact]
    public void 已获取未启动态写明拒绝UAC的后果()
    {
        var line = ThermalSourceStatus.DescribeStatusLine(
            ThermalSourceStatus.Resolve(null, true, false));
        Assert.Equal("已获取未启动（点「启动（需要管理员）」运行它；拒绝 UAC 也没关系，仍可用其他数据源）", line);
    }

    [Fact]
    public void 未获取态写明获取入口()
    {
        var line = ThermalSourceStatus.DescribeStatusLine(
            ThermalSourceStatus.Resolve(null, false, false));
        Assert.Equal("未获取（点「获取温度数据源」从官方 GitHub 发布下载便携包）", line);
    }
}
