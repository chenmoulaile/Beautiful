using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beautiful.Core.Design;
using Beautiful.Core.Imaging;
using Color = System.Windows.Media.Color;                 // UseWindowsForms 隐式 using 消歧
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

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
    public static void Apply(ResourceDictionary dict, MacTokens t, string accentHex, string? textFontFamily = null,
                             double panelCornerRadius = 18)
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
        dict["MacWallpaperTint"] = Brush(t.WallpaperTint);   // 墙纸取色（键位与 AurWallpaperTint 对齐）

        dict["MacMenuBarHeight"] = t.MenuBarHeight;
        dict["MacMenuBarFontSize"] = t.MenuBarFontSize;
        dict["MacMenuItemPaddingX"] = t.MenuItemPaddingX;
        dict["MacDockIconSize"] = t.DockIconSize;
        dict["MacDockItemSpacing"] = t.DockItemSpacing;
        dict["MacDockEdgeMargin"] = t.DockEdgeMargin;
        dict["MacDockPanelRadiusRatio"] = t.DockPanelRadiusRatio;
        // CornerRadius（而非 boxed double）：XAML 侧 CornerRadius="{DynamicResource MacPopupCornerRadius}"
        // 直接取值，DynamicResource 不走 TypeConverter，double 会赋不上（CalendarFlyout 卡底圆角消费点）。
        dict["MacPopupCornerRadius"] = new CornerRadius(t.PopupCornerRadius);
        dict["MacSwitcherPanelRadius"] = new CornerRadius(t.SwitcherPanelRadius);   // 切换器卡圆角（macOS 18 = 原生观感）
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
        // Dock 玻璃（macOS 形态）：静态字典的三停只是首帧值，这里带上墙纸 tint 重算。
        // tint 全透明（取色关闭）时权重 0 → 逐字节等于 MacOS.xaml:15-19 的静态渐变，5.0.0 观感零变化；
        // 混入权重与 Aurora 公式同源（WallpaperTint.GlassMixWeight，alpha × 0.35）。
        dict["ThemeDockGlassBackground"] = DockGlassGradient(t);
        // 面板圆角由 ThemeManager 按主题传入（macOS 维持既有 18 —— record 25 与字典 18 的存量矛盾按
        // "不回归"原则保留现状；Aurora 注入其 PanelCornerRadius=26）。
        dict["ThemePanelCornerRadius"] = new CornerRadius(panelCornerRadius);

        // ---------- Aur* 弹层语义键（macOS 兜底值） ----------
        // FluentMenu/控制中心/托盘/网络/音量/启动器/切换器/QuickLook 等共享面板已改读 Aur* 键
        // （弹层底统一替换表）；macOS 主题下这些键不能悬空（DynamicResource 悬空 → 黑杠/透明洞）。
        // 这里写与各面板原就地硬编码同值的深色玻璃，macOS 视觉零回归；Aurora 由
        // AuroraTokenDictionary 按自身 token 覆写（亮色走亮弹层底）。
        dict["AurPopupFillSolid"] = VerticalGradient("#F82C2C2F", "#FB1F1F23");
        dict["AurPopupFillBlur"] = VerticalGradient("#8A232327", "#9919191C");
        dict["AurMediaPanelFillSolid"] = VerticalGradient("#C81C1C1E", "#D5171719");  // 媒体面板实心底：MediaPanel.xaml 原就地硬编码同值（macOS 视觉零回归）
        dict["AurInnerGlowBrush"] = InnerGlow();
        dict["AurCardFill"] = Brush("#14FFFFFF");
        dict["AurCardFillHover"] = Brush("#14FFFFFF");
        dict["AurCardBorder"] = Brush("#1FFFFFFF");
        dict["AurCardFillOn"] = Brush("#3FFFFFFF");
        dict["AurSelectionFill"] = AlphaBrush(accentHex, 0xEB);   // 原选中行 = accent + Opacity 0.92（≈ alpha EB）
        dict["AurSelectionHairline"] = Brush(accentHex);
        dict["AurAccentText"] = Brush("#FFFFFFFF");               // on-accent 文字（横幅徽标；macOS 原本就是写死白）
        dict["AurElevation3"] = new DropShadowEffect              // QuickLook 卡层阴影（沿用原影板规格）
        {
            BlurRadius = 22,
            ShadowDepth = 4,
            Direction = 270,
            Color = (Color)ColorConverter.ConvertFromString("#000000"),
            Opacity = 0.28,
        };
    }

    /// <summary>竖向双停渐变（macOS Aur* 兜底值用）。</summary>
    private static LinearGradientBrush VerticalGradient(string topHex, string bottomHex)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(topHex), 0.0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottomHex), 1.0));
        brush.Freeze();
        return brush;
    }

    // ══════════════════════════ Dock 玻璃公式（墙纸取色的 macOS 混入位） ══════════════════════════
    //
    // 三停字面值与 MacOS.xaml:15-19 的静态渐变逐字节相同（静态字典是"首帧值"的出处，这里运行时重算）。
    // tint 权重 = tint alpha × WallpaperTint.GlassMixWeight(0.35)，与 AuroraTokenDictionary.DockGlassGradient
    // 的 tint.ScA × 0.35 同源——两条主题线对同一张壁纸的混入力度永远一致。权重 0（取色关闭）时
    // Core.WallpaperTint.MixHex 恒等返回基色 → 画刷与静态字典逐字节相同（关态零回归由 Core 单测锁定）。

    /// <summary>macOS Dock 玻璃三停（上亮下沉；= MacOS.xaml 的 ThemeDockGlassBackground）。</summary>
    private static readonly (string Hex, double Offset)[] DockGlassStops =
    [
        ("#C838383A", 0.0),
        ("#B8242426", 0.5),
        ("#A8161618", 1.0),
    ];

    /// <summary>把墙纸 tint 按同源权重混进 macOS 玻璃三停（tint 解析失败/全透明 = 静态渐变原样）。</summary>
    private static LinearGradientBrush DockGlassGradient(MacTokens t)
    {
        var weight = 0.0;
        if (WallpaperTint.TryParseHex(t.WallpaperTint, out _, out _, out _, out var alpha))
            weight = alpha / 255.0 * WallpaperTint.GlassMixWeight;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (hex, offset) in DockGlassStops)
            brush.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(WallpaperTint.MixHex(hex, t.WallpaperTint, weight)),
                offset));
        brush.Freeze();
        return brush;
    }

    /// <summary>弹层顶部内高光（macOS 各面板原 InnerGlow 的主流形态）。</summary>
    private static LinearGradientBrush InnerGlow()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 0.35));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>强调色降透明画刷（macOS 选中行软填充兜底用）。</summary>
    private static SolidColorBrush AlphaBrush(string hex, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
