using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beautiful.Core.Design;
using Color = System.Windows.Media.Color;                 // UseWindowsForms 隐式 using 消歧
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace Beautiful.App.Themes;

/// <summary>
/// 把 Core 的 <see cref="AuroraTokens"/> 写进 Aurora 主题 ResourceDictionary（键面 =
/// <see cref="MacTokenDictionary.Apply"/> 逐键对照一个不少 + Aur* 扩展）。
/// <para>
/// 【为什么 Mac* 键一个不能少】QuickLook（QuickLookWindow.xaml.cs 读 MacColorScheme/MacFont/MacFontMono）、
/// 访达窗口/通知横幅/通知中心（XAML 绑 MacFont/Mac* 画刷）等消费端只认 Mac* 键：Aurora 主题下
/// 这些键若缺席，DynamicResource 悬空 → 面板黑杠/白屏。<see cref="MacTokens"/> 的兼容别名 Theme*
/// 同理（DockWindow/顶栏/日历等 v3 键还在引用）。
/// </para>
/// <para>
/// 【Aur* 扩展】层压卡/弹层底/玻璃公式基色/弹簧曲线是字面值；随强调色变化的派生键
/// （AurCardFillOn/AurSelectionFill/AurAccentSoft/AurAccentText/AurAccentGlow/AurHairlineBrush/
/// AurIndicatorGlow）与 Dock 玻璃渐变（ThemeDockGlassBackground 的 Aurora 形态）在这里按 HSL
/// 纯函数计算——同时修复"亮色模式 Dock 玻璃仍是暗渐变"的存量缺陷（macOS 侧玻璃只来自静态字典）。
/// </para>
/// </summary>
public static class AuroraTokenDictionary
{
    public static void Apply(ResourceDictionary dict, AuroraTokens t, string accentHex,
                             string? textFontFamily = null, double panelCornerRadius = 26)
    {
        var fontText = string.IsNullOrWhiteSpace(textFontFamily) ? t.FontText : textFontFamily;
        var accent = MacTokenDictionary.Brush(accentHex);

        // ---------- Mac* 颜色（键面与 MacTokenDictionary.Apply 一致） ----------
        dict["MacAccent"] = accent;
        dict["MacColorScheme"] = t.IsDark ? "dark" : "light";
        dict["MacWindowBackground"] = MacTokenDictionary.Brush(t.WindowBackground);
        dict["MacWindowBorder"] = MacTokenDictionary.Brush(t.WindowBorder);
        dict["MacMenuBarBackground"] = MacTokenDictionary.Brush(t.MenuBarBackground);
        dict["MacMenuBarText"] = MacTokenDictionary.Brush(t.MenuBarText);
        dict["MacMenuBarHairline"] = MacTokenDictionary.Brush(t.MenuBarHairline);
        dict["MacMenuBackground"] = MacTokenDictionary.Brush(t.MenuBackground);
        dict["MacMenuText"] = MacTokenDictionary.Brush(t.MenuText);
        dict["MacMenuTextSecondary"] = MacTokenDictionary.Brush(t.MenuTextSecondary);
        dict["MacMenuSeparator"] = MacTokenDictionary.Brush(t.MenuSeparator);
        dict["MacMenuSelection"] = accent;                    // 运行时强调色画刷（Aurora 契约）
        dict["MacDockBackground"] = MacTokenDictionary.Brush(t.DockBackground);   // 纯色（ReadColor 只认实色）
        dict["MacDockBorder"] = MacTokenDictionary.Brush(t.DockBorder);
        dict["MacDockSeparator"] = MacTokenDictionary.Brush(t.DockSeparator);
        dict["MacDockIndicator"] = accent;                    // 运行指示点 = 强调色
        dict["MacPanelBackground"] = MacTokenDictionary.Brush(t.PanelBackground);
        dict["MacPanelBorder"] = MacTokenDictionary.Brush(t.PanelBorder);
        dict["MacPopupSolid"] = MacTokenDictionary.Brush(t.PopupSolid);
        dict["MacPopupScrim"] = MacTokenDictionary.Brush(t.PopupScrim);
        dict["MacTooltipBackground"] = MacTokenDictionary.Brush(t.TooltipBackground);
        dict["MacTextPrimary"] = MacTokenDictionary.Brush(t.TextPrimary);
        dict["MacTextSecondary"] = MacTokenDictionary.Brush(t.TextSecondary);
        dict["MacTextTertiary"] = MacTokenDictionary.Brush(t.TextTertiary);
        dict["MacControlFill"] = MacTokenDictionary.Brush(t.ControlFill);
        dict["MacControlFillHover"] = MacTokenDictionary.Brush(t.ControlFillHover);
        dict["MacSeparator"] = MacTokenDictionary.Brush(t.Separator);
        dict["MacBackdropTint"] = MacTokenDictionary.Brush(t.BackdropTint);

        // ---------- Mac* 度量/字体/动效 ----------
        dict["MacMenuBarHeight"] = t.MenuBarHeight;
        dict["MacMenuBarFontSize"] = t.MenuBarFontSize;
        dict["MacMenuItemPaddingX"] = t.MenuItemPaddingX;
        dict["MacDockIconSize"] = t.DockIconSize;
        dict["MacDockItemSpacing"] = t.DockItemSpacing;
        dict["MacDockEdgeMargin"] = t.DockEdgeMargin;
        dict["MacDockPanelRadiusRatio"] = t.DockPanelRadiusRatio;
        // CornerRadius 类型（同 MacTokenDictionary）：XAML 的 CornerRadius="{DynamicResource}" 直取。
        dict["MacPopupCornerRadius"] = new CornerRadius(t.PopupCornerRadius);
        dict["MacSwitcherPanelRadius"] = new CornerRadius(t.SwitcherPanelRadius);   // 切换器卡圆角（Aurora 20，层级对齐 26 面板圆角）
        dict["MacMenuItemHeight"] = t.MenuItemHeight;
        dict["MacWindowCornerRadius"] = t.WindowCornerRadius;
        dict["MacControlCornerRadius"] = t.ControlCornerRadius;
        dict["MacFont"] = new FontFamily(fontText);
        dict["MacFontDisplay"] = new FontFamily(t.FontDisplay);   // 标题族独立于正文（层级靠字重差）
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

        // ---------- Theme* 兼容别名（v3 既有键，一个不少） ----------
        dict["ThemeAccent"] = accent;
        dict["ThemeDockBackground"] = MacTokenDictionary.Brush(t.DockBackground);
        dict["ThemeTextPrimary"] = MacTokenDictionary.Brush(t.TextPrimary);
        dict["ThemePanelBackground"] = MacTokenDictionary.Brush(t.PanelBackground);
        dict["ThemeDockSeparatorFill"] = MacTokenDictionary.Brush(t.DockSeparator);
        dict["ThemeBackdropTint"] = MacTokenDictionary.Brush(t.BackdropTint);
        dict["ThemeFontFamily"] = new FontFamily(fontText);
        dict["ThemePanelCornerRadius"] = new CornerRadius(panelCornerRadius);

        // Dock 玻璃（Aurora 形态）：运行时公式输出（亮/暗各算各的，静态字典里的三停只是首帧值）
        dict["ThemeDockGlassBackground"] = DockGlassGradient(t, accentHex);

        // ---------- Aur* 字面键 ----------
        dict["AurCardFill"] = MacTokenDictionary.Brush(t.CardFill);
        dict["AurCardFillHover"] = MacTokenDictionary.Brush(t.CardFillHover);
        dict["AurCardBorder"] = MacTokenDictionary.Brush(t.CardBorder);
        dict["AurHoverTile"] = MacTokenDictionary.Brush(t.HoverTile);
        dict["AurGlassBase"] = t.GlassBase;
        dict["AurWallpaperTint"] = MacTokenDictionary.Brush(t.WallpaperTint);
        dict["AurPopupFillSolid"] = VerticalGradient(t.PopupFillTop, t.PopupFillBottom);
        dict["AurPopupFillBlur"] = MacTokenDictionary.Brush(t.PopupFillBlur);
        dict["AurMediaPanelFillSolid"] = VerticalGradient(t.MediaPanelFillTop, t.MediaPanelFillBottom);  // 媒体面板实心底（降级形态）
        dict["AurEaseSpring"] = t.SpringEase;
        dict["AurCardEnterMs"] = t.CardEnterMs;

        // ---------- Aur* 派生键（随强调色） ----------
        dict["AurCardFillOn"] = AlphaBrush(accentHex, (byte)(t.IsDark ? 0x59 : 0x4D));   // 「开」态瓦片软填充
        dict["AurSelectionFill"] = AlphaBrush(accentHex, (byte)(t.IsDark ? 0x3D : 0x33)); // 选中行软填充
        dict["AurSelectionHairline"] = accent;                                            // 选中行 1px 发丝边
        dict["AurAccentSoft"] = SolidBrush(ShiftLightness(accentHex, +0.08));
        dict["AurAccentText"] = MacTokenDictionary.Brush(IsLightAccent(accentHex) ? "#FF0F1214" : "#FFFFFFFF"); // on-accent 自动对比
        dict["AurAccentGlow"] = AlphaBrush(accentHex, 0xB3);
        dict["AurHairlineBrush"] = HairlineBrush(accentHex);      // Dock 顶缘色相扫掠发丝线
        dict["AurInnerGlowBrush"] = InnerGlowBrush(accentHex);    // 弹层顶部内高光（统一替换各局部渐变）
        dict["AurIndicatorGlow"] = new DropShadowEffect           // 运行指示点辉光（不 Freeze：accent 变要能换色）
        {
            BlurRadius = 8,
            ShadowDepth = 0,
            Color = AlphaColor(accentHex, 0xB3),
        };
        dict["AurElevation1"] = Elevation(36, 10, t.IsDark ? 0.42 : 0.22, t.IsDark ? "#000000" : "#1A2A35");
        dict["AurElevation2"] = Elevation(48, 12, t.IsDark ? 0.50 : 0.26, t.IsDark ? "#000000" : "#1A2A35");
        dict["AurElevation3"] = Elevation(64, t.IsDark ? 18 : 16, t.IsDark ? 0.55 : 0.30, t.IsDark ? "#000000" : "#1A2A35");
    }

    // ══════════════════════════ 玻璃公式（tokenTable【8】） ══════════════════════════
    // base = mix(mix(AurGlassBase, AurWallpaperTint, w=0.35), accent, k)（k：暗 0.10 / 亮 0.06）
    // 三停：Stop0 = Lighten(base,+0.08L)、Stop0.55 = base、Stop1 = Darken(base,−0.07L)；
    // alpha：暗 D9/C2/A6、亮 E8/DC/CC（亮模式玻璃更实是刻意的：浅面板不提高不透明度会被花墙纸洗掉文字）。
    private static LinearGradientBrush DockGlassGradient(AuroraTokens t, string accentHex)
    {
        var glass = Parse(t.GlassBase);
        var tint = Parse(t.WallpaperTint);
        var withTint = Lerp(glass, Opaque(tint), tint.ScA * 0.35);
        var withAccent = Lerp(withTint, Opaque(Parse(accentHex)), t.IsDark ? 0.10 : 0.06);
        var (h, s, l) = ToHsl(withAccent);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(WithAlpha(FromHsl(h, s, Math.Min(1, l + 0.08)), (byte)(t.IsDark ? 0xD9 : 0xE8)), 0.0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(withAccent, (byte)(t.IsDark ? 0xC2 : 0xDC)), 0.55));
        brush.GradientStops.Add(new GradientStop(WithAlpha(FromHsl(h, s, Math.Max(0, l - 0.07)), (byte)(t.IsDark ? 0xA6 : 0xCC)), 1.0));
        brush.Freeze();
        return brush;
    }

    // ══════════════════════════ HSL 纯函数（~40 行，可单测） ══════════════════════════

    internal static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Colors.Transparent; }
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.ScR, g = c.ScG, b = c.ScB;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var d = max - min;
        if (d <= 0) return (0, 0, l);
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60.0, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t switch
            {
                < 1.0 / 6.0 => p + (q - p) * 6 * t,
                < 1.0 / 2.0 => q,
                < 2.0 / 3.0 => p + (q - p) * (2.0 / 3.0 - t) * 6,
                _ => p,
            };
        }
        if (s <= 0)
        {
            var gray = (float)l;
            return Color.FromScRgb(1f, gray, gray, gray);
        }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        return Color.FromScRgb(1f,
            (float)HueToRgb(p2, q2, h / 360 + 1.0 / 3.0),
            (float)HueToRgb(p2, q2, h / 360),
            (float)HueToRgb(p2, q2, h / 360 - 1.0 / 3.0));
    }

    /// <summary>色相/饱和度不变、明度位移（AurAccentSoft 的 L+0.08、玻璃三停的 ±0.07/0.08 共用）。</summary>
    private static Color ShiftLightness(string hex, double delta)
    {
        var c = Parse(hex);
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
    }

    private static bool IsLightAccent(string hex) => ToHsl(Parse(hex)).L > 0.55;

    private static Color Opaque(Color c) { c.ScA = 1f; return c; }

    private static Color WithAlpha(Color c, byte a) { c.A = a; return c; }

    private static Color AlphaColor(string hex, byte a) => WithAlpha(Parse(hex), a);

    /// <summary>强调色降透明画刷（AurCardFillOn/AurSelectionFill/AurAccentGlow 共用）。</summary>
    private static SolidColorBrush AlphaBrush(string hex, byte a) => SolidBrush(WithAlpha(Parse(hex), a));

    private static SolidColorBrush SolidBrush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>逐通道线性插值（玻璃公式的两次 mix；t 是 b 的权重）。</summary>
    private static Color Lerp(Color a, Color b, double t) => Color.FromScRgb(
        (float)(a.ScA + (b.ScA - a.ScA) * t),
        (float)(a.ScR + (b.ScR - a.ScR) * t),
        (float)(a.ScG + (b.ScG - a.ScG) * t),
        (float)(a.ScB + (b.ScB - a.ScB) * t));

    private static LinearGradientBrush VerticalGradient(string topHex, string bottomHex)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Parse(topHex), 0.0));
        brush.GradientStops.Add(new GradientStop(Parse(bottomHex), 1.0));
        brush.Freeze();
        return brush;
    }

    // ══════════════════════════ Aur* 画刷规格 ══════════════════════════

    /// <summary>
    /// Dock 顶缘发丝线（高 1.5dip，只铺顶缘）：强调色色相扫掠三停
    /// hue@0xE6 → hue+38°@0x8C(0.45) → hue+95°@0x4D(0.8) → hue+95°@0x00(1.0)。
    /// </summary>
    private static LinearGradientBrush HairlineBrush(string accentHex)
    {
        var (h, s, l) = ToHsl(Parse(accentHex));
        Color At(double hueShift, byte alpha) => WithAlpha(FromHsl(h + hueShift, s, l), alpha);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(At(0, 0xE6), 0.0));
        brush.GradientStops.Add(new GradientStop(At(38, 0x8C), 0.45));
        brush.GradientStops.Add(new GradientStop(At(95, 0x4D), 0.8));
        brush.GradientStops.Add(new GradientStop(At(95, 0x00), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 弹层顶部内高光（统一替换 FluentMenu/控制中心/托盘/网络/音量/启动器/切换器/台前调度/顶栏的
    /// 局部 InnerGlow 渐变）：mix(白, accent, 0.18)@0x66 → 白@0x00，竖向。
    /// </summary>
    private static LinearGradientBrush InnerGlowBrush(string accentHex)
    {
        var tinted = Lerp(Colors.White, Opaque(Parse(accentHex)), 0.18);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(WithAlpha(tinted, 0x66), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>Elevation 阴影规格（E1 卡 / E2 约束浮层 / E3 QuickLook 等大浮层；Direction=270 向下）。</summary>
    private static DropShadowEffect Elevation(double blur, double depth, double opacity, string colorHex) => new()
    {
        BlurRadius = blur,
        ShadowDepth = depth,
        Direction = 270,
        Color = Parse(colorHex),
        Opacity = opacity,
    };
}
