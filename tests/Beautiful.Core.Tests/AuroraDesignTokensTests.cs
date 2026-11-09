using System.Text.RegularExpressions;
using Beautiful.Core.Design;
using Xunit;

/// <summary>
/// wp-detail r5：Aurora token 层守护。切换器卡圆角键化（MacSwitcherPanelRadius：macOS 18 / Aurora 20，
/// 两主题分层各异）与媒体面板实心底（AurMediaPanelFillSolid，暗/亮各一档玻璃）都是"改主题=改这里"
/// 的单一事实源，值面钉住防回归。
/// </summary>
public class AuroraDesignTokensTests
{
    [Fact]
    public void AuroraToken_切换器圆角20且媒体面板实心底亮暗齐全()
    {
        var argb = new Regex("^#[0-9A-Fa-f]{8}$");
        foreach (var t in new[] { AuroraTokens.Light, AuroraTokens.Dark })
        {
            Assert.Equal(20, t.SwitcherPanelRadius);   // 比 macOS 18 大一档，对齐 Aurora 26 面板圆角层级
            Assert.Matches(argb, t.MediaPanelFillTop);
            Assert.Matches(argb, t.MediaPanelFillBottom);
        }
        // 亮暗两档色相不同（亮色给亮玻璃，媒体面板文字走 ThemeTextPrimary 在两套外观下都可读）
        Assert.NotEqual(AuroraTokens.Dark.MediaPanelFillTop, AuroraTokens.Light.MediaPanelFillTop);
    }
}
