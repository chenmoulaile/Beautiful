using System;
using System.Globalization;
using Beautiful.Core.Settings;
using Beautiful.Core.TopBar;
using Xunit;

/// <summary>
/// 顶栏时钟形态（v3.1 缺陷 3）：四项开关 → 模板 / 步长 / 渲染结果，以及"与机器 locale 无关"的承诺。
/// </summary>
public class ClockFormatTests
{
    // 2026-10-14 实测为周三（与 ClockFormatterTests 同一基准日）。
    private static readonly DateTime Wed2127 = new(2026, 10, 14, 21, 27, 5);

    private static string Render(ClockFormat f, DateTime when = default)
        => ClockFormatter.Format(when == default ? Wed2127 : when, f.Pattern);

    // ---------- 默认形态：升级观感不变 ----------

    [Fact]
    public void 默认形态与v3顶栏逐字符相同()
    {
        Assert.Equal(ClockFormatter.FullPattern, ClockFormat.Default.Pattern);
        Assert.Equal("10月14日 周三 21:27", Render(ClockFormat.Default));
    }

    [Fact]
    public void 默认形态即AppSettings的四个时钟默认值()
        => Assert.Equal(ClockFormat.Default, ClockFormat.From(new AppSettings()));

    // ---------- 各开关的组合 ----------

    [Fact]
    public void 显示秒_模板加秒位且结果两位补零()
    {
        var f = ClockFormat.Default with { ShowSeconds = true };
        Assert.Equal("{M}月{d}日 {w} {HH}:{mm}:{ss}", f.Pattern);
        Assert.Equal("10月14日 周三 21:27:05", Render(f));
    }

    [Fact]
    public void 关闭日期与星期_只剩时间()
    {
        var f = ClockFormat.Default with { ShowDate = false, ShowWeekday = false };
        Assert.Equal("{HH}:{mm}", f.Pattern);
        Assert.Equal("21:27", Render(f));
    }

    [Fact]
    public void 只留星期_日期段整段消失不留空括号()
        => Assert.Equal("周三 21:27", Render(ClockFormat.Default with { ShowDate = false }));

    [Fact]
    public void 只留日期_星期段整段消失()
        => Assert.Equal("10月14日 21:27", Render(ClockFormat.Default with { ShowWeekday = false }));

    [Fact]
    public void 四项全关_仍有时间不输出空串()
    {
        var f = new ClockFormat(false, false, false, true);
        Assert.Equal("21:27", Render(f));
    }

    // ---------- 12 / 24 小时制 ----------

    [Fact]
    public void 十二小时制_下午带时段()
    {
        var f = ClockFormat.Default with { Use24Hour = false };
        Assert.Equal("10月14日 周三 9:27 下午", Render(f));
    }

    [Fact]
    public void 十二小时制_零点与正午都归12()
    {
        var f = ClockFormat.Default with { Use24Hour = false, ShowDate = false, ShowWeekday = false };
        Assert.Equal("12:05 上午", Render(f, new DateTime(2026, 10, 14, 0, 5, 0)));
        Assert.Equal("12:30 下午", Render(f, new DateTime(2026, 10, 14, 12, 30, 0)));
        Assert.Equal("1:30 下午", Render(f, new DateTime(2026, 10, 14, 13, 30, 0)));
        Assert.Equal("11:59 上午", Render(f, new DateTime(2026, 10, 14, 11, 59, 0)));
    }

    [Fact]
    public void 十二小时制加秒_秒位在分钟之后时段之前()
    {
        var f = ClockFormat.Default with { Use24Hour = false, ShowSeconds = true, ShowDate = false, ShowWeekday = false };
        Assert.Equal("9:27:05 下午", Render(f));
    }

    [Fact]
    public void 二十四小时制_保持两位时()
        => Assert.Equal("09:27", Render(ClockFormat.Default with { ShowDate = false, ShowWeekday = false },
                                        new DateTime(2026, 10, 14, 9, 27, 0)));

    // ---------- 定时器纪律（"显示秒"同时决定刷新节奏） ----------

    [Fact]
    public void 步长_显示秒为1秒否则60秒()
    {
        Assert.Equal(1, (ClockFormat.Default with { ShowSeconds = true }).TickSeconds);
        Assert.Equal(60, ClockFormat.Default.TickSeconds);
        Assert.Equal(60, new ClockFormat(false, false, false, false).TickSeconds);
    }

    [Fact]
    public void 十六种组合都产出含时间的非空模板()
    {
        foreach (var s in new[] { true, false })
            foreach (var d in new[] { true, false })
                foreach (var w in new[] { true, false })
                    foreach (var h24 in new[] { true, false })
                    {
                        var f = new ClockFormat(s, d, w, h24);
                        var text = Render(f);
                        Assert.False(string.IsNullOrWhiteSpace(text));
                        // 时间永远在场：结果必须带 ":"，且 24/12 制的时段后缀只可能出现在 12 小时制上
                        Assert.Contains(":", text);
                        Assert.Equal(!h24, text.Contains("上午") || text.Contains("下午"));
                        Assert.Equal(d, text.Contains("月"));
                        Assert.Equal(w, text.Contains("周"));
                        // 秒位只在"显示秒"时出现（基准时刻秒=05，全/半角都不会误命中）
                        Assert.Equal(s, text.Contains(":05"));
                    }
    }

    // ---------- locale 无关（用户真机是中文/英文/阿拉伯文系统都要长一样） ----------

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void 输出不随机器区域设置漂移(string culture)
    {
        var origin = CultureInfo.CurrentCulture;
        var originUi = CultureInfo.CurrentUICulture;
        try
        {
            // 顶栏格式化全程走 InvariantCulture + 代码内词表；这里把当前线程 culture 换成各种语言，
            // 断言的却是**写死的中文字符串**（不是"和另一次调用相等"），因此 locale 一旦渗入就立刻失败。
            // ar-SA 尤其关键：culture 敏感的格式化会把数字变成 Arabic-Indic（٠١٢…），这里必须是 ASCII 数字。
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.Equal(culture, CultureInfo.CurrentCulture.Name);   // 确认这次真的跑在该 locale 下
            Assert.Equal("10月14日 周三 21:27", Render(ClockFormat.Default));
            Assert.Equal("10月14日 周三 21:27:05", Render(ClockFormat.Default with { ShowSeconds = true }));
            Assert.Equal("10月14日 周三 9:27 下午", Render(ClockFormat.Default with { Use24Hour = false }));
            Assert.Equal("9:27:05 下午", Render(new ClockFormat(true, false, false, false)));
            Assert.Equal("09:27", Render(new ClockFormat(false, false, false, true),
                                         new DateTime(2026, 10, 14, 9, 27, 0)));
        }
        finally
        {
            CultureInfo.CurrentCulture = origin;
            CultureInfo.CurrentUICulture = originUi;
        }
    }

    [Fact]
    public void From_逐字段映射AppSettings()
    {
        var s = new AppSettings
        {
            ClockShowSeconds = true,
            ClockShowDate = false,
            ClockShowWeekday = true,
            ClockUse24Hour = false
        };
        Assert.Equal(new ClockFormat(true, false, true, false), ClockFormat.From(s));
    }
}
