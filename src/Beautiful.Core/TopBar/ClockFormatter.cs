using System;
using System.Globalization;
using System.Text;

namespace Beautiful.Core.TopBar;

/// <summary>
/// 顶栏时钟/日期格式化（Task 9，v3.1 扩 12 小时制与秒）：令牌模板引擎，产出中文口径（"10月14日 周三 21:27"）。
/// Core 无 WPF 依赖，纯 <see cref="DateTime"/> 计算，便于单测。
/// <para>
/// 支持令牌：<c>{yyyy}</c> 四位年、<c>{M}</c> 月(不补零)、<c>{d}</c> 日(不补零)、
/// <c>{w}</c> 星期(周一..周日)、<c>{H}</c>/<c>{HH}</c> 时(24 小时制，HH 补零)、
/// <c>{h}</c>/<c>{hh}</c> 时(12 小时制)、<c>{tt}</c> 上午/下午、
/// <c>{m}</c>/<c>{mm}</c> 分(mm 补零)、<c>{ss}</c> 秒(补零)。
/// 未知令牌原样保留（含花括号），字面量透传。
/// </para>
/// <para>
/// 【与区域设置无关（v3.1）】所有数字都显式走 <see cref="CultureInfo.InvariantCulture"/>，星期与上下午
/// 用代码内常量表 —— 用户机器是中文/英文/法文/阿拉伯文 locale，顶栏输出的字形与数字都必须一模一样
/// （有单测把 CurrentCulture 换成 fr-FR / ar-SA 断言等值）。带 <c>tt</c> 的 12 小时制由
/// <see cref="ClockFormat"/> 统一决定"时间在前、时段在后"的排布。
/// </para>
/// <para>
/// 顶栏常驻表面的刷新节奏（每分钟 / 每秒）不在这里决定，见 <see cref="ClockFormat.TickSeconds"/>。
/// </para>
/// </summary>
public static class ClockFormatter
{
    /// <summary>完整变体模板："10月14日 周三 21:27"。</summary>
    public const string FullPattern = "{M}月{d}日 {w} {HH}:{mm}";

    /// <summary>仅日期变体模板："10月14日 周三"。</summary>
    public const string DatePattern = "{M}月{d}日 {w}";

    /// <summary>按 <paramref name="pattern"/> 令牌模板渲染 <paramref name="now"/>。</summary>
    public static string Format(DateTime now, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        var sb = new StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '{')
            {
                var end = pattern.IndexOf('}', i + 1);
                if (end > i)
                {
                    sb.Append(Expand(pattern.Substring(i + 1, end - i - 1), now));
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(pattern[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string Expand(string token, DateTime n) => token switch
    {
        "yyyy" => n.Year.ToString(CultureInfo.InvariantCulture),
        "M" => n.Month.ToString(CultureInfo.InvariantCulture),
        "d" => n.Day.ToString(CultureInfo.InvariantCulture),
        "w" => WeekdayName(n.DayOfWeek),
        "H" => n.Hour.ToString(CultureInfo.InvariantCulture),
        "HH" => n.Hour.ToString("D2", CultureInfo.InvariantCulture),
        // 12 小时制：0 点归 12（中午 12 点仍是 12），其余 hour%12。不用 DateTime.ToString("h") —— 那会
        // 跟随当前 locale（有的语言把 0 点写成 12、有的写成 0），而顶栏必须与区域设置无关。
        "h" => Hour12(n).ToString(CultureInfo.InvariantCulture),
        "hh" => Hour12(n).ToString("D2", CultureInfo.InvariantCulture),
        "m" => n.Minute.ToString(CultureInfo.InvariantCulture),
        "mm" => n.Minute.ToString("D2", CultureInfo.InvariantCulture),
        "ss" => n.Second.ToString("D2", CultureInfo.InvariantCulture),
        "tt" => n.Hour < 12 ? "上午" : "下午",
        _ => "{" + token + "}", // 未知令牌原样保留
    };

    private static int Hour12(DateTime n) => n.Hour % 12 == 0 ? 12 : n.Hour % 12;

    private static string WeekdayName(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
