using System.Windows;
using System.Windows.Media;
using Beautiful.Core.Design;
using Color = System.Windows.Media.Color;                 // UseWindowsForms 隐式 using 消歧
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace Beautiful.App.Themes;

/// <summary>
/// 把 Core 的 <see cref="MacTokens"/> 写进当前主题 ResourceDictionary。
/// <para>
/// 两套键名并存：
/// <list type="bullet">
/// <item><description><c>Mac*</c> = v4 新键（窗口/菜单栏/Dock/弹层/字体/动效），新代码只用它；</description></item>
/// <item><description><c>Theme*</c> = v3 既有键的兼容别名（Dock 底色/文字/面板底色/分隔符/tint/字体/圆角），
/// 保证还没迁移到 Mac* 的 XAML 也随外观即时换色，而不是亮色下继续白字黑底。</description></item>
/// </list>
/// 画刷全部 Freeze（DynamicResource 会在换字典时重新解析，冻结后渲染线程可共享）。
/// </para>
/// </summary>
public static class MacTokenDictionary
{
    public static void Apply(ResourceDictionary dict, MacTokens t, string accentHex, string? textFontFamily = null)
    {
        var fontText = string.IsNullOrWhiteSpace(textFontFamily) ? t.FontText : textFontFamily;

        // ---------- 新键：Mac* ----------
        dict["MacAccent"] = Brush(accentHex);
        dict["MacColorScheme"] = t.IsDark ? "dark" : "light";
        dict["MacWindowBackground"] = Brush(t.WindowBackground);
        dict["MacWindowBorder"] = Brush(t.WindowBorder);
        dict["MacMenuBarBackground"] = Brush(t.MenuBarBackground);
        dict["MacMenuBarText"] = Brush(t.MenuBarText);
        dict["MacMenuBarHairline"] = Brush(t.MenuBarHairline);
        dict["MacMenuBackground"] = Brush(t.MenuBackground);
        dict["MacMenuText"] = Brush(t.MenuText);
        dict["MacMenuTextSecondary"] = Brush(t.MenuTextSecondary);
        dict["MacMenuSeparator"] = Brush(t.MenuSeparator);
        dict["MacMenuSelection"] = Brush(t.MenuSelection);
        dict["MacDockBackground"] = Brush(t.DockBackground);
        dict["MacDockBorder"] = Brush(t.DockBorder);
        dict["MacDockSeparator"] = Brush(t.DockSeparator);
        dict["MacDockIndicator"] = Brush(t.DockIndicator);
        dict["MacPanelBackground"] = Brush(t.PanelBackground);
        dict["MacPanelBorder"] = Brush(t.PanelBorder);
        dict["MacPopupSolid"] = Brush(t.PopupSolid);
        dict["MacPopupScrim"] = Brush(t.PopupScrim);
        dict["MacTooltipBackground"] = Brush(t.TooltipBackground);
        dict["MacTextPrimary"] = Brush(t.TextPrimary);
        dict["MacTextSecondary"] = Brush(t.TextSecondary);
        dict["MacTextTertiary"] = Brush(t.TextTertiary);
        dict["MacControlFill"] = Brush(t.ControlFill);
        dict["MacControlFillHover"] = Brush(t.ControlFillHover);
        dict["MacSeparator"] = Brush(t.Separator);
        dict["MacBackdropTint"] = Brush(t.BackdropTint);

        dict["MacMenuBarHeight"] = t.MenuBarHeight;
        dict["MacMenuBarFontSize"] = t.MenuBarFontSize;
        dict["MacMenuItemPaddingX"] = t.MenuItemPaddingX;
        dict["MacDockIconSize"] = t.DockIconSize;
        dict["MacDockItemSpacing"] = t.DockItemSpacing;
        dict["MacDockEdgeMargin"] = t.DockEdgeMargin;
        dict["MacDockPanelRadiusRatio"] = t.DockPanelRadiusRatio;
        dict["MacPopupCornerRadius"] = t.PopupCornerRadius;
        dict["MacMenuItemHeight"] = t.MenuItemHeight;
        dict["MacWindowCornerRadius"] = t.WindowCornerRadius;
        dict["MacControlCornerRadius"] = t.ControlCornerRadius;
        dict["MacFont"] = new FontFamily(fontText);
        dict["MacFontDisplay"] = new FontFamily(fontText);
        dict["MacFontMono"] = new FontFamily(t.FontMono);
        dict["MacFontSizeBody"] = t.FontSizeBody;
        dict["MacFontSizeCaption"] = t.FontSizeCaption;
        dict["MacMenuPopupMs"] = t.MenuPopupMs;
        dict["MacMenuCollapseMs"] = t.MenuCollapseMs;
        dict["MacDockSlideMs"] = t.DockSlideMs;
        dict["MacLaunchpadMs"] = t.LaunchpadMs;
        dict["MacWindowOpenMs"] = t.WindowOpenMs;
        dict["MacGenieMs"] = t.GenieMs;
        dict["MacEaseOut"] = t.EaseOut;
        dict["MacEaseInOut"] = t.EaseInOut;

        // ---------- 兼容别名：v3 既有 Theme* 键 ----------
        dict["ThemeAccent"] = Brush(accentHex);
        dict["ThemeDockBackground"] = Brush(t.DockBackground);
        dict["ThemeTextPrimary"] = Brush(t.TextPrimary);
        dict["ThemePanelBackground"] = Brush(t.PanelBackground);
        dict["ThemeDockSeparatorFill"] = Brush(t.DockSeparator);
        dict["ThemeBackdropTint"] = Brush(t.BackdropTint);
        dict["ThemeFontFamily"] = new FontFamily(fontText);
        dict["ThemePanelCornerRadius"] = new CornerRadius(18);
    }

    /// <summary>#RRGGBB / #AARRGGBB → 冻结画刷；token 写错时回退透明（绝不因一个颜色字符串崩启动）。</summary>
    internal static SolidColorBrush Brush(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            var fallback = new SolidColorBrush(Colors.Transparent);
            fallback.Freeze();
            return fallback;
        }
    }

    /// <summary>#RRGGBB / #AARRGGBB → 0xAARRGGBB（native 背板 tint 用；解析失败回透明）。</summary>
    public static uint ParseArgb(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
