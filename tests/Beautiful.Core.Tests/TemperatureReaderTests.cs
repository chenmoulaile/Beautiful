using Beautiful.Core.Thermal;
using Xunit;

/// <summary>
/// 温度折算纯函数测试：芯片取整、0.1K 换算、读数可信区间、多传感器挑 CPU Package。
/// 这些规则决定顶栏芯片显示什么数字——显示错一个数比不显示更糟，所以在 Core 层钉死。
/// WMI 探测（TemperatureProbe）要真机才有数据源，不在单测范围。
/// </summary>
public class TemperatureReaderTests
{
    // ---------- FormatChip：取整 + 单位 ----------

    [Theory]
    [InlineData(45.4, "45°")]
    [InlineData(45.5, "46°")]   // AwayFromZero：.5 进位（银行家舍入会是 46？45.5 银行家也是 46，46.5 才能区分——补 46.5 用例）
    [InlineData(46.5, "47°")]   // 银行家舍入会把 46.5 → 46，必须 AwayFromZero
    [InlineData(-3.2, "-3°")]
    [InlineData(99.96, "100°")]
    public void 芯片文字取整带度数符号(double celsius, string expected)
        => Assert.Equal(expected, TemperatureReader.FormatChip(celsius));

    [Theory]
    [InlineData(null)]
    [InlineData(999.0)]     // 超可信区间：不可信 → 占位
    [InlineData(-273.15)]   // ACPI 0 开尔文换算出来的荒谬值
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 芯片文字空值或不可信给占位(double? celsius)
        => Assert.Equal(TemperatureReader.MissingChip, TemperatureReader.FormatChip(celsius));

    // ---------- IsPlausible：可信区间 ----------

    [Theory]
    [InlineData(-20.0, true)]
    [InlineData(-19.9, true)]
    [InlineData(0.0, true)]
    [InlineData(100.0, true)]
    [InlineData(150.0, true)]
    [InlineData(-20.1, false)]
    [InlineData(150.1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void 可信区间闭区间且排除非有限值(double celsius, bool expected)
        => Assert.Equal(expected, TemperatureReader.IsPlausible(celsius));

    // ---------- KelvinTenthsToCelsius：ACPI 0.1K 换算 ----------

    [Theory]
    [InlineData(3000, 26.85)]   // 典型 ACPI 热区读数
    [InlineData(2732, 0.05)]    // 冰点附近
    [InlineData(3732, 100.05)]  // 沸点附近
    public void 十分之一开尔文换算摄氏(double tenthsKelvin, double expected)
        => Assert.Equal(expected, TemperatureReader.KelvinTenthsToCelsius(tenthsKelvin), precision: 2);

    // ---------- PickCpuPackageCelsius：多传感器裁定 ----------

    [Fact]
    public void 优先取CPU封装温度()
    {
        var pick = TemperatureReader.PickCpuPackageCelsius(
        [
            new("CPU Package", "/intelcpu/0", 45.0),
            new("Core #1", "/intelcpu/0", 40.0),
            new("Core #2", "/intelcpu/0", 50.0),
            new("Temperature #1", "/lpc/nct6776f/0", 38.0),
            new("GPU Package", "/gpu/0/amdnv10/0", 70.0),
        ]);
        Assert.Equal(45.0, pick);
    }

    [Fact]
    public void AMD平台Tctl优先于逐核平均()
    {
        var pick = TemperatureReader.PickCpuPackageCelsius(
        [
            new("Core (Tctl/Tdie)", "/amdcpu/0", 55.0),
            new("Core (Tctl/Tdie)", "/amdcpu/0", 58.0),
            new("Core #1", "/amdcpu/0", 42.0),
            new("Core #2", "/amdcpu/0", 44.0),
        ]);
        Assert.Equal(58.0, pick);   // 同档多条取最热
    }

    [Fact]
    public void 没有封装温度时逐核取平均()
    {
        var pick = TemperatureReader.PickCpuPackageCelsius(
        [
            new("Core #1", "/intelcpu/0", 40.0),
            new("Core #2", "/intelcpu/0", 50.0),
            new("GPU Core", "/gpu/0", 60.0),   // 名含 "core" 但 parent 不是 CPU：不得混入平均
        ]);
        Assert.Equal(45.0, pick);
    }

    [Fact]
    public void 不给Parent时按传感器名识别CPU()
    {
        var pick = TemperatureReader.PickCpuPackageCelsius(
        [
            new("CPU Package", null, 47.5),
            new("GPU Package", null, 66.0),    // 名里没有 cpu：排除
        ]);
        Assert.Equal(47.5, pick);
    }

    [Fact]
    public void 不可信读数被滤掉落到下一档()
    {
        var pick = TemperatureReader.PickCpuPackageCelsius(
        [
            new("CPU Package", "/intelcpu/0", 999.0),   // 荒谬值：不取
            new("Core #1", "/intelcpu/0", 41.0),
            new("Core #2", "/intelcpu/0", 45.0),
        ]);
        Assert.Equal(43.0, pick);
    }

    [Fact]
    public void 空值与空列表返回null()
    {
        Assert.Null(TemperatureReader.PickCpuPackageCelsius(
        [
            new("CPU Package", "/intelcpu/0", null),
            new("Core #1", "/intelcpu/0", null),
        ]));
        Assert.Null(TemperatureReader.PickCpuPackageCelsius([]));
        Assert.Null(TemperatureReader.PickCpuPackageCelsius(
        [
            new("Temperature #1", "/lpc/nct6776f/0", 38.0),   // 只有主板：识别不出 CPU，绝不冒数
            new("GPU Package", "/gpu/0", 70.0),
        ]));
    }

    // ---------- Describe：来源中文名（ToolTip/设置页状态行共用） ----------

    [Theory]
    [InlineData(ThermalSource.LibreHardwareMonitor, "LibreHardwareMonitor")]
    [InlineData(ThermalSource.OpenHardwareMonitor, "OpenHardwareMonitor")]
    [InlineData(ThermalSource.AcpiThermalZone, "ACPI 热区")]
    [InlineData(ThermalSource.None, "无数据源")]
    public void 来源描述文案(ThermalSource source, string expected)
        => Assert.Equal(expected, TemperatureReader.Describe(source));

    // ---------- ThermalReading.ChipText：读数 → 芯片文字一条龙 ----------

    [Fact]
    public void 读数的芯片文字与FormatChip同口径()
    {
        var reading = new ThermalReading(51.4, ThermalSource.LibreHardwareMonitor);
        Assert.Equal("51°", reading.ChipText);
    }
}
