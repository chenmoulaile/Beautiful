using System.Globalization;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 小组件的文字与图标映射（Phase 7-T3）：时钟两行显示、温度四舍五入、天气代码 → 中文+符号。
/// <para>
/// 【为什么下沉 Core】"23.4 度该显示 23° 还是 23.4°"、"未知天气代码显示什么"这类判断如果写在
/// UI 里，只能靠肉眼验证；做成纯函数后单测能穷举 wttr.in 的整套代码。
/// </para>
/// <para>
/// 【约定】温度取整到整数并带度数符号（macOS 小组件不显示小数——小数只会让 Dock 上的字变长）；
/// 拿不到温度显示 <c>--°</c>，天气代码未知显示 <c>未知</c>，任何情况都不留空串（空串会让
/// 两行文字塌成一行，Dock 高度随之跳动）。
/// </para>
/// </summary>
public static class DockWidget
{
    /// <summary>未取到温度时的占位（两字符，与 <c>23°</c> 同宽，避免布局抖动）。</summary>
    public const string MissingTemperature = "--°";

    /// <summary>
    /// 时钟文字：24 小时制 <c>14:05</c>；12 小时制 <c>2:05 PM</c>（macOS 英文环境写法）。
    /// </summary>
    public static string Clock(DateTimeOffset now, bool use24Hour = true)
    {
        var local = now.ToLocalTime();
        return use24Hour
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 温度文字：四舍五入到整数并带 °；<c>null</c>/非有限值给 <see cref="MissingTemperature"/>。
    /// </summary>
    public static string Temperature(double? celsius)
    {
        if (celsius is not { } value || !double.IsFinite(value))
        {
            return MissingTemperature;
        }

        // Math.Round 默认银行家舍入（23.5 → 24、22.5 → 22）；温度显示用 AwayFromZero 更符合直觉。
        var rounded = (int)Math.Round(Math.Clamp(value, -99, 99), MidpointRounding.AwayFromZero);
        return $"{rounded}°";
    }

    /// <summary>
    /// wttr.in（WWO 天气代码）→ (符号, 中文描述)。未知代码返回 <c>("☁", "未知")</c>，
    /// 不抛异常：天气接口偶尔会加新代码，前端不该因为一个数字而崩。
    /// </summary>
    public static (string Glyph, string Text) DescribeWeather(int code, string? englishFallback = null)
    {
        var mapped = DescribeCode(code);
        // 代码不在表里（wttr.in 偶尔给标准外的数字，实测见过 149）时用英文描述兜底：
        // 显示 "29° 雾" 比 "29° 未知" 有用，而猜一个中文词比"未知"更糟。
        return mapped.Text == "未知" ? FromEnglish(englishFallback) ?? mapped : mapped;
    }

    /// <summary>英文天气描述 → (符号, 中文)。未命中返回 null（调用方据此保留"未知"）。</summary>
    private static (string Glyph, string Text)? FromEnglish(string? english)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            return null;
        }

        var text = english.ToLowerInvariant();
        return text switch
        {
            _ when text.Contains("thunder") => ("⛈", "雷雨"),
            _ when text.Contains("snow") || text.Contains("blizzard") => ("❄", "雪"),
            _ when text.Contains("sleet") || text.Contains("freezing") => ("🌨", "雨夹雪"),
            _ when text.Contains("heavy rain") => ("🌧", "大雨"),
            _ when text.Contains("moderate rain") => ("🌧", "中雨"),
            _ when text.Contains("rain") || text.Contains("drizzle") || text.Contains("shower") => ("🌦", "小雨"),
            _ when text.Contains("fog") || text.Contains("mist") => ("🌫", "雾"),
            _ when text.Contains("haze") || text.Contains("smoke") => ("🌫", "霾"),
            _ when text.Contains("overcast") => ("☁", "阴"),
            _ when text.Contains("cloud") => ("⛅", "多云"),
            _ when text.Contains("sunny") || text.Contains("clear") => ("☀", "晴"),
            _ => null
        };
    }

    private static (string Glyph, string Text) DescribeCode(int code) => code switch
    {
        113 => ("☀", "晴"),
        116 => ("⛅", "多云"),
        119 or 122 => ("☁", "阴"),
        143 or 248 or 260 => ("🌫", "雾"),
        176 or 263 or 266 or 293 or 296 => ("🌦", "小雨"),
        299 or 302 => ("🌧", "中雨"),
        305 or 308 or 356 or 359 => ("🌧", "大雨"),
        353 => ("🌦", "阵雨"),
        179 or 182 or 185 or 281 or 284 or 311 or 314 => ("🌨", "冻雨"),
        317 or 320 or 323 or 326 or 329 => ("🌨", "雪"),
        332 or 335 or 338 or 368 or 371 => ("❄", "大雪"),
        350 or 374 => ("🌨", "冰粒"),
        362 or 365 or 377 => ("🌨", "雨夹雪"),
        200 or 386 or 389 => ("⛈", "雷雨"),
        392 or 395 => ("⛈", "雷雪"),
        _ => ("☁", "未知")
    };

    /// <summary>
    /// 第二行文字：<c>23° 晴</c>。温度缺失时只给描述（<c>晴</c>），温度与描述都缺失时给
    /// <c>--° 未知</c>——仍然两个字段，布局不抖。
    /// </summary>
    public static string Detail(double? celsius, int code, string? englishFallback = null)
    {
        var (_, text) = DescribeWeather(code, englishFallback);
        var temperature = Temperature(celsius);
        return string.IsNullOrEmpty(text) ? temperature : $"{temperature} {text}";
    }

    /// <summary>天气缓存是否过期：拿不到时间（<c>default</c>）视为过期，强制刷新一次。</summary>
    public static bool IsStale(DateTimeOffset fetchedAt, DateTimeOffset now, TimeSpan ttl)
        => fetchedAt == default || now - fetchedAt >= ttl;
}
