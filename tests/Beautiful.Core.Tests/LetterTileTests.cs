using Beautiful.Core.Icons;
using Xunit;

namespace Beautiful.Core.Tests;

/// <summary>
/// 首字母色块参数（启动台/启动器图标兜底链最后一级）的契约：
/// 字母选取（拉丁大写 / CJK 原样 / 代理对完整取 / 空名问号）与 accent 颜色收敛、尺寸钳制。
/// 全部纯函数，无 WPF；位图渲染在 App 侧，运行时目视验收（docs/evidence）。
/// </summary>
public class LetterTileTests
{
    // ---------- LetterOf ----------

    [Theory]
    [InlineData("Family", "F")]
    [InlineData("family", "F")]
    [InlineData("  weChat ", "W")]
    [InlineData("代码编辑器", "代")]
    [InlineData("微信", "微")]
    [InlineData("12306", "1")]
    public void LetterOf_取第一个非空白字符_拉丁转大写_CJK原样(string name, string expected)
        => Assert.Equal(expected, LetterTile.LetterOf(name));

    [Fact]
    public void LetterOf_代理对取完整字符_不画半个替换符()
        => Assert.Equal("🎮", LetterTile.LetterOf(" 🎮游戏厅更多"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u3000\t\n")]
    public void LetterOf_空名回落问号(string? name)
        => Assert.Equal("?", LetterTile.LetterOf(name));

    // ---------- CanonicalAccent ----------

    [Theory]
    [InlineData("#0A84FF", "#FF0A84FF")]
    [InlineData("0A84FF", "#FF0A84FF")]
    [InlineData("#800A84FF", "#800A84FF")]
    [InlineData("  #0a84ff  ", "#FF0A84FF")]
    public void CanonicalAccent_合法值收敛为AARRGGBB(string raw, string expected)
        => Assert.Equal(expected, LetterTile.CanonicalAccent(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zzz")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#0A84FG")]
    public void CanonicalAccent_非法值回落默认蓝(string? raw)
        => Assert.Equal("#FF0A84FF", LetterTile.CanonicalAccent(raw));

    // ---------- SpecFor ----------

    [Fact]
    public void SpecFor_常规输入_参数齐备()
    {
        var spec = LetterTile.SpecFor("Family", 176, "#0A84FF");
        Assert.Equal(176, spec.Size);
        Assert.Equal("#FF0A84FF", spec.AccentHex);
        Assert.Equal("F", spec.Letter);
    }

    [Fact]
    public void SpecFor_脏输入_全部兜底_绝不让格子空白()
    {
        var spec = LetterTile.SpecFor(null, 0, "not-a-color");
        Assert.Equal(LetterTile.MinSizePx, spec.Size);
        Assert.Equal("#FF0A84FF", spec.AccentHex);
        Assert.Equal("?", spec.Letter);
    }

    [Fact]
    public void SpecFor_尺寸钳制()
    {
        Assert.Equal(LetterTile.MaxSizePx, LetterTile.SpecFor("A", 9999, null).Size);
        Assert.Equal(LetterTile.MinSizePx, LetterTile.SpecFor("A", 1, null).Size);
    }

    [Fact]
    public void CornerRadiusRatio_与macOS图标遮罩同档()
        => Assert.Equal(0.2, LetterTile.CornerRadiusRatio, 6);
}
