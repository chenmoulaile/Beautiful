using System;
using System.Linq;
using System.Text.RegularExpressions;
using Beautiful.Core.Design;
using Xunit;

/// <summary>
/// v4 Phase 1 T4：macOS token 层取证。token 是"未来重定 macOS 版本只改这里"的单一事实源，
/// 因此白名单/折算/取值必须有测试钉住；颜色字符串只允许 #RRGGBB 或 #AARRGGBB。
/// </summary>
public class MacDesignTokensTests
{
    [Theory]
    [InlineData(null, "dark")]
    [InlineData("", "dark")]
    [InlineData("Light", "light")]
    [InlineData("  AUTO  ", "auto")]
    [InlineData("sepia", "dark")]
    public void 外观模式_规范化收敛(string? raw, string expected)
        => Assert.Equal(expected, AppearanceModes.Canonical(raw));

    [Theory]
    [InlineData("light", true, false)]
    [InlineData("dark", true, true)]
    [InlineData("auto", true, false)]
    [InlineData("auto", false, true)]
    [InlineData("weird", false, true)]   // 未知 → dark（再按 dark 分支）
    public void 外观模式_折算当前是否深色(string mode, bool systemUsesLight, bool expected)
        => Assert.Equal(expected, AppearanceModes.IsDark(mode, systemUsesLight));

    [Fact]
    public void 强调色板_十二色且亮暗成对()
    {
        Assert.Equal(12, MacAccents.All.Count);
        Assert.Equal(12, MacAccents.All.Select(a => a.Id).Distinct().Count());
        var hex = new Regex("^#[0-9A-Fa-f]{6}$");
        foreach (var accent in MacAccents.All)
        {
            Assert.Matches(hex, accent.LightHex);
            Assert.Matches(hex, accent.DarkHex);
            Assert.False(string.IsNullOrWhiteSpace(accent.Name));
        }
    }

    [Fact]
    public void 强调色_未知值回落蓝色且大小写归一()
    {
        Assert.Equal("blue", MacAccents.Canonical("nope"));
        Assert.Equal("purple", MacAccents.Canonical("  PURPLE "));
        Assert.Equal(MacAccents.All.First(a => a.Id == "blue").LightHex, MacAccents.Hex("nope", dark: false));
        Assert.Equal(MacAccents.All.First(a => a.Id == "blue").DarkHex, MacAccents.Hex(null, dark: true));
    }

    [Fact]
    public void MacToken_亮暗两套关键值齐全且遵守BigSur度量()
    {
        var argb = new Regex("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");
        foreach (var t in new[] { MacTokens.Light, MacTokens.Dark })
        {
            var colors = new[]
            {
                t.WindowBackground, t.WindowBorder, t.MenuBarBackground, t.MenuBarText, t.MenuBarHairline,
                t.MenuBackground, t.MenuText, t.MenuTextSecondary, t.MenuSeparator, t.MenuSelection,
                t.DockBackground, t.DockBorder, t.DockSeparator, t.DockIndicator, t.PanelBackground,
                t.PanelBorder, t.PopupSolid, t.PopupScrim, t.TooltipBackground, t.TextPrimary,
                t.TextSecondary, t.TextTertiary, t.ControlFill, t.ControlFillHover, t.Separator, t.BackdropTint,
            };
            Assert.All(colors, c => Assert.Matches(argb, c));
            Assert.Equal(24, t.MenuBarHeight);          // Big Sur 菜单栏高 24px
            Assert.Equal(13, t.MenuBarFontSize);
            Assert.Equal(56, t.DockIconSize);           // Dock 默认图标 56px
            Assert.InRange(t.DockEdgeMargin, 0, 24);    // 距底边距默认 4
            Assert.InRange(t.DockPanelRadiusRatio, 0.42, 0.46);
            Assert.Equal(10, t.WindowCornerRadius);
            Assert.True(t.MenuPopupMs > 0 && t.GenieMs >= 450);
            Assert.Contains("SF Pro Text", t.FontText); // 携带字体失败时由后面的 Segoe 回退
            Assert.Contains("Segoe UI", t.FontText);
        }
        Assert.NotEqual(MacTokens.Dark.DockBackground, MacTokens.Light.DockBackground);
        Assert.True(MacTokens.Dark.IsDark);
        Assert.False(MacTokens.Light.IsDark);
        Assert.Same(MacTokens.Dark, MacTokens.For(true));
        Assert.Same(MacTokens.Light, MacTokens.For(false));
    }
}
