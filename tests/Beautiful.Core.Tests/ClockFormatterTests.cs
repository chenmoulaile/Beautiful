using System;
using Beautiful.Core.TopBar;
using Xunit;

public class ClockFormatterTests
{
    // 完整变体：例 "10月14日 周三 21:27"（Task 9 顶栏时钟项口径）。2026-10-14 实测为周三。
    [Fact]
    public void 完整格式_月日周时分()
        => Assert.Equal("10月14日 周三 21:27",
            ClockFormatter.Format(new DateTime(2026, 10, 14, 21, 27, 0), ClockFormatter.FullPattern));

    // 仅日期变体：同上去掉时分。
    [Fact]
    public void 日期变体_不含时分()
        => Assert.Equal("10月14日 周三",
            ClockFormatter.Format(new DateTime(2026, 10, 14, 21, 27, 0), ClockFormatter.DatePattern));

    // 星期名：周一..周日 全覆盖（DayOfWeek 枚举 Monday=0..Sunday=6）。
    [Theory]
    [InlineData(0, "周一")] // 2024-01-01 是周一
    [InlineData(1, "周二")]
    [InlineData(2, "周三")]
    [InlineData(3, "周四")]
    [InlineData(4, "周五")]
    [InlineData(5, "周六")]
    [InlineData(6, "周日")]
    public void 星期名映射(int offset, string expected)
        => Assert.Equal(expected,
            ClockFormatter.Format(new DateTime(2024, 1, 1).AddDays(offset), "{w}"));

    // 零填充：个位时/分补前导零；非填充令牌不补。
    [Fact]
    public void 时分零填充()
        => Assert.Equal("09:05",
            ClockFormatter.Format(new DateTime(2026, 10, 9, 9, 5, 0), "{HH}:{mm}"));

    [Fact]
    public void 时分非填充()
        => Assert.Equal("9:5",
            ClockFormatter.Format(new DateTime(2026, 10, 9, 9, 5, 0), "{H}:{m}"));

    // 月/日不零填充（"10月14日" 而非 "10月14日" 补零；9 月 5 日 → "9月5日"）。
    [Fact]
    public void 月日不补零()
        => Assert.Equal("9月5日",
            ClockFormatter.Format(new DateTime(2026, 9, 5, 0, 0, 0), "{M}月{d}日"));

    // 未知令牌原样保留（含花括号），字面量透传。
    [Fact]
    public void 未知令牌原样保留()
        => Assert.Equal("X{zz}Y", ClockFormatter.Format(new DateTime(2026, 1, 1), "X{zz}Y"));

    [Fact]
    public void 空模式返回空()
        => Assert.Equal(string.Empty, ClockFormatter.Format(new DateTime(2026, 1, 1), ""));
}
