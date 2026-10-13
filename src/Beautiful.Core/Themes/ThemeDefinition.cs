using System.Drawing;

namespace Beautiful.Core.Themes;

/// <summary>
/// 主题参数对象（spec 4.2 表）：布局引擎唯一读取源，主题 = 参数 + 资源字典。
/// Color 用 System.Drawing.Color（Core 无 WPF 依赖；App 侧再转 Media.Color 传亚克力 tint）。
/// </summary>
public sealed record ThemeDefinition(
    string Id, string DisplayName,
    Color DockBackground,         // Dock 面板底色（亚克力 tint 另计）
    Color AcrylicTint,            // 窗口级 tint（含 alpha，传 ToAbgr）
    bool BlurEnabled,
    double PanelCornerRadius,
    double IconCornerRadiusRatio, // 0=不遮罩；0.25=鸿蒙 squircle；其余=圆角方形（半径=ratio·IconSize）
    double IconSize,
    double ItemSpacing,
    double MaxScale,              // 1=无放大
    string IndicatorStyle,        // "dot" | "bar" | "none"
    string AccentHex,             // 鸿蒙 #0A59F7
    string FontFamily)            // "HarmonyOS Sans" / "Segoe UI Variable" / "Segoe UI"
{
    /// <summary>控制器裁定：MaxScale&gt;1 才启用高斯放大（鸿蒙/Linux 无放大）。</summary>
    public bool ShowMagnification => MaxScale > 1.0;

    /// <summary>
    /// 鸿蒙：深色玻璃 #61000000（默认深色；浅色变体 #59FFFFFF 由 Task 17 系统主题跟随提供），
    /// squircle 连续圆角（Superellipse nExp=4.4），无放大（胶囊悬浮反馈，Task 15 实装）。
    /// </summary>
    public static ThemeDefinition HarmonyOs() => new(
        Id: "HarmonyOS", DisplayName: "鸿蒙",
        DockBackground: Argb("#61000000"),
        AcrylicTint: Argb("#61000000"),
        BlurEnabled: true,
        PanelCornerRadius: 28,
        IconCornerRadiusRatio: 0.25,
        IconSize: 44,
        ItemSpacing: 6,
        MaxScale: 1.0,
        IndicatorStyle: "dot",
        AccentHex: "#0A59F7",
        FontFamily: "HarmonyOS Sans");

    /// <summary>macOS：暗色亚克力 #B4202020（tint 同底色）、系统蓝 #0A84FF、抛物线放大 MaxScale=1.8、底部圆点、图标原样。</summary>
    public static ThemeDefinition MacOs() => new(
        Id: "MacOS", DisplayName: "macOS",
        DockBackground: Argb("#B4202020"),
        AcrylicTint: Argb("#B4202020"),
        BlurEnabled: true,
        PanelCornerRadius: 18,
        IconCornerRadiusRatio: 0,
        IconSize: 44,
        ItemSpacing: 6,
        MaxScale: 1.8,
        IndicatorStyle: "dot",
        AccentHex: "#0A84FF",
        FontFamily: "Segoe UI Variable");

    /// <summary>Linux(GNOME)：#E61C1C1C 实底（BlurEnabled=false 禁用亚克力）、accent #3584E4、无放大、左侧短条、圆角方形图标（半径 8）。</summary>
    public static ThemeDefinition Linux() => new(
        Id: "Linux", DisplayName: "Linux (GNOME)",
        DockBackground: Argb("#E61C1C1C"),
        AcrylicTint: Argb("#E61C1C1C"),
        BlurEnabled: false,
        PanelCornerRadius: 12,
        IconCornerRadiusRatio: 8.0 / 44.0, // 圆角方形：半径 = ratio·IconSize = 8
        IconSize: 44,
        ItemSpacing: 6,
        MaxScale: 1.0,
        IndicatorStyle: "bar",
        AccentHex: "#3584E4",
        FontFamily: "Segoe UI");

    /// <summary>#RRGGBB / #AARRGGBB → Color（内置解析：System.Drawing.Common 的 ColorTranslator 不在 net8.0 基础包内）。</summary>
    private static Color Argb(string hex)
    {
        var h = hex.TrimStart('#');
        return h.Length switch
        {
            8 => Color.FromArgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16),
                                Convert.ToByte(h[4..6], 16), Convert.ToByte(h[6..8], 16)),
            6 => Color.FromArgb(byte.MaxValue, Convert.ToByte(h[..2], 16),
                                Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16)),
            _ => throw new FormatException($"无效颜色 hex：{hex}")
        };
    }
}
