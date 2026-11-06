using System.Globalization;

namespace Beautiful.Core.Thermal;

/// <summary>
/// 温度数据源（探测优先级即枚举值序，<see cref="TemperatureProbe.ReadOnce"/> 按此逐级回退）：
/// <see cref="LibreHardwareMonitor"/>（root\LibreHardwareMonitor，最准——CPU Package 是封装温度）→
/// <see cref="OpenHardwareMonitor"/>（前身，同一 WMI schema）→
/// <see cref="AcpiThermalZone"/>（MSAcpi_ThermalZoneTemperature，多数机器不支持/只给主板热区）→
/// <see cref="None"/> = 无数据源。
/// </summary>
public enum ThermalSource
{
    None = 0,
    LibreHardwareMonitor,
    OpenHardwareMonitor,
    AcpiThermalZone,
}

/// <summary>一条温度传感器读数（WMI 行 → 扁平记录，纯数据，供 <see cref="TemperatureReader.PickCpuPackageCelsius"/> 挑选）。</summary>
/// <param name="Name">传感器名（如 "CPU Package"、"Core #1"、"Tctl"）；WMI 字段缺失时为 null。</param>
/// <param name="Parent">所属硬件标识（如 "/intelcpu/0"、"/gpu/0"）；某些提供方不给，为 null。</param>
/// <param name="Value">摄氏度读数；WMI 给 null/不可换算时为 null。</param>
public sealed record ThermalSensor(string? Name, string? Parent, double? Value);

/// <summary>一次成功的 CPU 温度读数。</summary>
public sealed record ThermalReading(double Celsius, ThermalSource Source)
{
    /// <summary>顶栏芯片文字（"45°"），与 <see cref="TemperatureReader.FormatChip"/> 同一折算。</summary>
    public string ChipText => TemperatureReader.FormatChip(Celsius);
}

/// <summary>
/// 硬件温度的纯折算层（取整、单位换算、多传感器挑 CPU Package、读数合法性）。
/// <para>
/// 【为什么是纯函数】WMI 探测在真机上才能验证，但这几条规则是纯逻辑：
/// 下沉 Core 配 xunit 穷举后，App 侧只把 WMI 行喂进来、拿结果上屏——不写第二份格式化。
/// </para>
/// <para>
/// 【绝不冒数】识别不出 CPU 传感器、读数超出可信区间，一律返回 null / <c>--°</c>，
/// 由 UI 层整体隐藏芯片——宁可没有温度，也不显示一个编造的数。
/// </para>
/// </summary>
public static class TemperatureReader
{
    /// <summary>读数可信区间（摄氏）：ACPI 热区会返回 0/负开尔文等荒谬值，超出即视为无效读数。</summary>
    public const double PlausibleMinCelsius = -20;
    public const double PlausibleMaxCelsius = 150;

    /// <summary>没有可用读数时的占位（与 <c>23°</c> 同宽，避免布局抖动；UI 对 null 另有整体隐藏路径）。</summary>
    public const string MissingChip = "--°";

    /// <summary>读数是否可信（非 NaN/无穷且落在 <see cref="PlausibleMinCelsius"/>..<see cref="PlausibleMaxCelsius"/>）。</summary>
    public static bool IsPlausible(double celsius)
        => !double.IsNaN(celsius) && !double.IsInfinity(celsius)
           && celsius >= PlausibleMinCelsius && celsius <= PlausibleMaxCelsius;

    /// <summary>MSAcpi_ThermalZoneTemperature 的 CurrentTemperature 单位是 0.1 K。</summary>
    public static double KelvinTenthsToCelsius(double tenthsKelvin) => tenthsKelvin / 10.0 - 273.15;

    /// <summary>
    /// 顶栏芯片文字：四舍五入到整数并带 °（AwayFromZero，与 <c>DockWidget.Temperature</c> 同一口径——
    /// 23.5 → 24 更符合直觉）；null / 不可信读数给 <see cref="MissingChip"/>。
    /// </summary>
    public static string FormatChip(double? celsius)
    {
        if (celsius is not { } value || !IsPlausible(value)) return MissingChip;
        return $"{Math.Round(value, MidpointRounding.AwayFromZero)}°";
    }

    /// <summary>来源的中文名（芯片 ToolTip / 设置页状态行共用，避免 UI 里散落 switch）。</summary>
    public static string Describe(ThermalSource source) => source switch
    {
        ThermalSource.LibreHardwareMonitor => "LibreHardwareMonitor",
        ThermalSource.OpenHardwareMonitor => "OpenHardwareMonitor",
        ThermalSource.AcpiThermalZone => "ACPI 热区",
        _ => "无数据源",
    };

    /// <summary>
    /// 从一组传感器读数里挑 CPU 温度（多传感器场景的裁定规则）。
    /// <para>
    /// 【候选判定】Parent 含 "cpu"（LibreHardwareMonitor 的 "/intelcpu/0"、"/amdcpu/0"）
    /// 或 Name 含 "cpu"（不给 Parent 的提供方）——GPU/主板/硬盘传感器无论名字多像都被排除
    /// （"GPU Package" 会被这条规则挡住）。
    /// </para>
    /// <para>
    /// 【优先级】① 名含 "package"（封装温度，Intel/AMD 都给，最接近 macOS 菜单栏的 CPU 温度）
    /// → ② 名含 "tctl"/"tdie"（AMD）→ ③ 名以 "core" 开头（逐核温度，取平均当整体）。
    /// 同档多条取最大值（最热封装/最热核心，监控工具惯例）。
    /// 不可信读数先被 <see cref="IsPlausible"/> 滤掉；全都挑不出来返回 null（无数据源状态）。
    /// </para>
    /// </summary>
    public static double? PickCpuPackageCelsius(IEnumerable<ThermalSensor> sensors)
    {
        var candidates = new List<ThermalSensor>();
        foreach (var sensor in sensors)
        {
            if (sensor.Value is not { } value || !IsPlausible(value)) continue;
            var name = sensor.Name ?? string.Empty;
            var parent = sensor.Parent ?? string.Empty;
            var isCpu = Contains(parent, "cpu") || Contains(name, "cpu");
            if (isCpu) candidates.Add(sensor);
        }

        // ①/②：封装温度 > AMD Tctl/Tdie；多条取最热。
        var pick = Hottest(candidates, static name => Contains(name, "package"))
                ?? Hottest(candidates, static name => Contains(name, "tctl") || Contains(name, "tdie"));
        if (pick is not null) return pick;

        // ③：只有逐核温度时取平均（"Core #1/#2/…"，AMD 的 "Core (Tctl/Tdie)" 已被 ② 接走）。
        var cores = new List<double>();
        foreach (var sensor in candidates)
        {
            var name = sensor.Name?.TrimStart() ?? string.Empty;
            if (name.StartsWith("core", StringComparison.OrdinalIgnoreCase) && sensor.Value is { } v)
                cores.Add(v);
        }

        if (cores.Count == 0) return null;
        var sum = 0.0;
        foreach (var c in cores) sum += c;
        return sum / cores.Count;
    }

    /// <summary>同档多条取最热；一条都没有返回 null。</summary>
    private static double? Hottest(List<ThermalSensor> candidates, Func<string, bool> match)
    {
        double? hottest = null;
        foreach (var sensor in candidates)
        {
            var name = sensor.Name ?? string.Empty;
            if (!match(name) || sensor.Value is not { } value) continue;
            if (hottest is not { } current || value > current) hottest = value;
        }
        return hottest;
    }

    private static bool Contains(string text, string keyword)
        => text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>
/// 温度数据源探测（按 <see cref="ThermalSource"/> 优先级逐级回退；对 WMI 的全部访问收口在这里）。
/// <para>
/// 【为什么走 WMI 而不是嵌 LibreHardwareMonitorLib】读库要装 WinRing0 内核驱动、要管理员权限，
/// 一个"顶栏温度"不该动内核；LHM/OHM 设置里勾"WMI 提供方"后会把全部传感器发布到 WMI 命名空间，
/// 普通用户权限可读——这层"用户自己装的工具"正是数据源，没有就诚实显示空态。
/// </para>
/// <para>
/// 【容错】命名空间不存在 / 拒绝访问 / 查询失败 / 字段怪异，一律返回 null 落到下一级，
/// 绝不外抛（App 侧轮询线程只认"有读数/没读数"两种结果）。
/// </para>
/// </summary>
// 本类经 System.Management 访问 WMI，仅 Windows 可用（CA1416）：非 Windows 上调用抛
// PlatformNotSupportedException，与 csproj 注释一致——探测层 try/catch 一律捕获按"拿不到"降级。
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class TemperatureProbe
{
    /// <summary>LibreHardwareMonitor 发布传感器的 WMI 命名空间（设置里勾选"WMI 提供方"后可用）。</summary>
    public const string LibreHardwareMonitorNamespace = "root\\LibreHardwareMonitor";

    /// <summary>OpenHardwareMonitor（LibreHardwareMonitor 的前身）发布传感器的 WMI 命名空间。</summary>
    public const string OpenHardwareMonitorNamespace = "root\\OpenHardwareMonitor";

    /// <summary>
    /// 按优先级读一次 CPU 温度：①LibreHardwareMonitor ②OpenHardwareMonitor
    /// ③ACPI 热区（多数机器不支持，视为拿不到）④返回 null = 无数据源。
    /// </summary>
    public static ThermalReading? ReadOnce()
    {
        var reading = ReadFromSensorTable(LibreHardwareMonitorNamespace, ThermalSource.LibreHardwareMonitor);
        if (reading is not null) return reading;

        reading = ReadFromSensorTable(OpenHardwareMonitorNamespace, ThermalSource.OpenHardwareMonitor);
        if (reading is not null) return reading;

        return ReadAcpiThermalZone();
    }

    /// <summary>
    /// 读 LHM/OHM 传感器表并挑 CPU Package。两个命名空间同一 schema（后者是前者 fork 的源头）：
    /// Sensor 类带 Name / SensorType / Parent / Value。
    /// </summary>
    private static ThermalReading? ReadFromSensorTable(string wmiNamespace, ThermalSource source)
    {
        try
        {
            var sensors = new List<ThermalSensor>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                wmiNamespace,
                "SELECT Name, Parent, Value FROM Sensor WHERE SensorType = 'Temperature'");
            using var results = searcher.Get();
            var enumerator = results.GetEnumerator();
            using (enumerator as IDisposable)
            {
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not System.Management.ManagementObject row) continue;
                    using (row)
                    {
                        sensors.Add(new ThermalSensor(
                            Name: row["Name"]?.ToString(),
                            Parent: row["Parent"]?.ToString(),
                            Value: ToDouble(row["Value"])));
                    }
                }
            }

            var celsius = TemperatureReader.PickCpuPackageCelsius(sensors);
            return celsius is null ? null : new ThermalReading(celsius.Value, source);
        }
        catch (Exception)
        {
            // 命名空间不存在（没装/没开 WMI 提供方）、拒绝访问、查询超时……都按"这一级拿不到"处理。
            return null;
        }
    }

    /// <summary>
    /// ACPI 热区（root\WMI 的 MSAcpi_ThermalZoneTemperature）：多数主板驱动不支持（实例化即抛），
    /// 支持的也常只有主板热区——多个热区取<b>最热</b>的一个当作 CPU 近似，且必须过可信区间。
    /// </summary>
    private static ThermalReading? ReadAcpiThermalZone()
    {
        try
        {
            double? hottest = null;
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI",
                "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            var enumerator = results.GetEnumerator();
            using (enumerator as IDisposable)
            {
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not System.Management.ManagementObject row) continue;
                    using (row)
                    {
                        var tenths = ToDouble(row["CurrentTemperature"]);
                        if (tenths is not { } value) continue;
                        var celsius = TemperatureReader.KelvinTenthsToCelsius(value);
                        if (!TemperatureReader.IsPlausible(celsius)) continue;
                        if (hottest is not { } current || celsius > current) hottest = celsius;
                    }
                }
            }

            return hottest is null ? null : new ThermalReading(hottest.Value, ThermalSource.AcpiThermalZone);
        }
        catch (Exception)
        {
            // "Not supported"（多数机器）/ 拒绝访问：这一级视为拿不到。
            return null;
        }
    }

    /// <summary>WMI 字段 → double（LHM 的 Value 是 double，ACPI 的 CurrentTemperature 是 uint；怪值一律 null）。</summary>
    private static double? ToDouble(object? raw)
    {
        if (raw is null) return null;
        try
        {
            var value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
        }
        catch (Exception)
        {
            // Convert 对无法换算的类型（byte[] 等）抛异常：该字段当没有。
            return null;
        }
    }
}
