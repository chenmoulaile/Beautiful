using System;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock 小组件文字规则：时钟两种制式、温度取整与缺失占位、天气代码映射、缓存过期判据。
/// 这些字符串直接画在 Dock 上，长度变化会让整条 Dock 抖动，所以必须在纯函数层钉住。
/// </summary>
public class DockWidgetTests
{
    [Fact]
    public void 二十四小时制时钟补零()
    {
        // 用本机时区偏移构造：Clock 内部有 ToLocalTime()，若用固定 +08:00 偏移，
        // 在 UTC 机器（如 CI）上钟点会被平移 → 断言必挂。本机偏移下转换是无操作。
        var time = new DateTimeOffset(2026, 10, 30, 9, 5, 0, DateTimeOffset.Now.Offset);
        Assert.Equal("09:05", DockWidget.Clock(time, use24Hour: true));
    }

    [Fact]
    public void 十二小时制带上午下午()
    {
        // 同上：本机偏移构造，避开 CI（UTC）上的时区平移。
        var afternoon = new DateTimeOffset(2026, 10, 30, 14, 5, 0, DateTimeOffset.Now.Offset);
        Assert.Equal("2:05 PM", DockWidget.Clock(afternoon, use24Hour: false));
    }

    [Theory]
    [InlineData(23.4, "23°")]
    [InlineData(23.5, "24°")]
    [InlineData(-0.4, "0°")]
    [InlineData(-12.6, "-13°")]
    [InlineData(999, "99°")]
    public void 温度取整到整数并钳制上限(double value, string expected)
    {
        Assert.Equal(expected, DockWidget.Temperature(value));
    }

    [Fact]
    public void 温度缺失给等宽占位()
    {
        Assert.Equal(DockWidget.MissingTemperature, DockWidget.Temperature(null));
        Assert.Equal("--°", DockWidget.Temperature(double.NaN));
        Assert.Equal(3, DockWidget.MissingTemperature.Length);
    }

    [Theory]
    [InlineData(113, "晴", "☀")]
    [InlineData(119, "阴", "☁")]
    [InlineData(296, "小雨", "🌦")]
    [InlineData(389, "雷雨", "⛈")]
    [InlineData(338, "大雪", "❄")]
    public void 天气代码映射到中文与符号(int code, string text, string glyph)
    {
        var (actualGlyph, actualText) = DockWidget.DescribeWeather(code);
        Assert.Equal(text, actualText);
        Assert.Equal(glyph, actualGlyph);
    }

    [Fact]
    public void 未知天气代码不抛异常()
    {
        var (glyph, text) = DockWidget.DescribeWeather(12345);
        Assert.Equal("未知", text);
        Assert.False(string.IsNullOrEmpty(glyph));
    }

    [Fact]
    public void 第二行拼接温度与描述()
    {
        Assert.Equal("23° 晴", DockWidget.Detail(23.4, 113));
        Assert.Equal("--° 晴", DockWidget.Detail(null, 113));
        Assert.Equal("--° 未知", DockWidget.Detail(null, 999));
    }

    [Fact]
    public void 缓存过期判据()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromMinutes(15);

        Assert.True(DockWidget.IsStale(default, now, ttl));                       // 从未取过
        Assert.False(DockWidget.IsStale(now - TimeSpan.FromMinutes(5), now, ttl));
        Assert.True(DockWidget.IsStale(now - TimeSpan.FromMinutes(16), now, ttl));
    }

    [Theory]
    [InlineData(149, "Mist", "雾")]
    [InlineData(149, "Patchy rain nearby", "小雨")]
    [InlineData(149, "Thundery outbreaks possible", "雷雨")]
    [InlineData(149, "Overcast", "阴")]
    [InlineData(149, "", "未知")]
    [InlineData(113, "Sunny", "晴")]
    public void 未知代码用英文描述兜底(int code, string english, string expected)
    {
        var (_, text) = DockWidget.DescribeWeather(code, english);
        Assert.Equal(expected, text);
    }
}
