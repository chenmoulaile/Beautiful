using System.Drawing;

namespace Beautiful.Core.Themes;

/// <summary>
/// 主题参数对象（spec 4.2 表）：布局引擎唯一读取源，主题 = 参数 + 资源字典。
/// Color 用 System.Drawing.Color（Core 无 WPF 依赖；App 侧再转 Media.Color 传亚克力 tint）。
/// <para>
/// v3 T4 起 Dock 的**行/面板竖向几何与分隔符样式都是主题数据**：用户"下方的 dock 栏有一段黑框，
/// 我只需要 dock 的大小"与"macOS Dock 没有原生分隔符"两条不满，根因都是这些量曾被写死成
/// 全局常量（行内余量 20、无分隔符）。原生差异（macOS 紧贴图标 + 两道分隔符、GNOME dash 宽松方角无分隔符、
/// 鸿蒙胶囊最松无分隔符）只能由参数表达，不能在渲染器里 <c>if (theme == mac)</c>。
/// </para>
/// </summary>
public sealed record ThemeDefinition(
    string Id, string DisplayName,
    Color DockBackground,         // Dock 面板底色（亚克力 tint 另计）
    Color AcrylicTint,            // 主题 tint 声明值（含 alpha）：v3.1 起不再交给旧亚克力 API（缺陷 1），
                                  // 同一色值由各主题 XAML 的自绘底承担
    bool BlurEnabled,
    double PanelCornerRadius,
    double IconCornerRadiusRatio, // 0=不遮罩；0.25=鸿蒙 squircle；其余=圆角方形（半径=ratio·IconSize）
    double IconSize,
    double ItemSpacing,
    double MaxScale,              // 1=无放大
    string IndicatorStyle,        // "dot" | "bar" | "none"
    string AccentHex,             // 鸿蒙 #0A59F7
    string FontFamily,            // "HarmonyOS Sans" / "Segoe UI Variable" / "Segoe UI"
    // ---------- v3 T4：Dock 行/面板竖向几何（DIP，见 Core Dock.DockRowMath） ----------
    double DockRowInsetDip,       // 行高在图标之上的余量（图标上下各一半）：决定"面板贴不贴图标"
    double DockPanelPaddingXDip,  // 面板水平内边距（图标块左右留白）
    double DockPanelPaddingYDip,  // 面板竖向内边距：可见面板高 = 行高 + 2×本值 = AppBar 预留带高
    bool ReflectionDefault,       // 倒影主题默认值（现代 macOS/GNOME/鸿蒙 dock 都无倒影；用户显式开关优先）
    // ---------- v3 T4：分隔符（原生 macOS Dock 的结构语义） ----------
    bool DockSeparatorsEnabled,   // 仅 macOS 原生 Dock 有分隔符；GNOME dash / 鸿蒙是连续列表
    double DockSeparatorWidthDip,      // 竖线粗细
    double DockSeparatorHeightRatio,   // 竖线高占**行高**比例（0.6~0.7）
    double DockSeparatorSideInsetDip,  // 竖线左右对称留白（条目盒宽 = 粗细 + 2×本值）
    // ---------- v3 T6：图标遮罩/投影也是主题数据（形状的唯一属主是烘焙位图，见 Imaging.IconTreatment） ----------
    IconMaskStyle IconMask,     // None=原样平铺（GNOME dash）；RoundedRectangle=macOS；Squircle=鸿蒙
    double IconMaskRadiusRatio, // 圆角半径 / artwork 边长（Squircle 忽略本值，用 Superellipse 的 n）
    bool IconShadow)            // 是否烘焙"图标自身 alpha 轮廓"下方的柔影（macOS/鸿蒙有，GNOME 无）
{
    /// <summary>控制器裁定：MaxScale&gt;1 才启用高斯放大（macOS MaxScale=1.7）。</summary>
    public bool ShowMagnification => MaxScale > 1.0;

    /// <summary>
    /// 分隔符条目盒宽（DIP）：线粗 + 两侧对称留白。模板把它写成 ThemeSeparatorBoxWidthDip，
    /// 行内还各侧再加一个 ItemSpacing，合计约两个图标间距——正是 macOS Dock 分隔符处的视觉留白。
    /// </summary>
    public double DockSeparatorBoxWidthDip => DockSeparatorWidthDip + 2.0 * DockSeparatorSideInsetDip;

    /// <summary>macOS：暗色亚克力 #B4202020（tint 同底色）、系统蓝 #0A84FF、抛物线放大 MaxScale=1.7、
    /// 指示点=图标下方小圆点。原生 Dock **紧贴图标**（行内只留约 10 DIP 呼吸）、
    /// 有「固定应用 | 正在运行 | 废纸篓」两道分隔符、现代 macOS **无倒影**。
    /// </summary>
    public static ThemeDefinition MacOs() => new(
        Id: "MacOS", DisplayName: "macOS",
        DockBackground: Argb("#B4202020"),
        AcrylicTint: Argb("#B4202020"),
        BlurEnabled: true,
        PanelCornerRadius: 25,
        IconCornerRadiusRatio: 0,
        IconSize: 56,
        ItemSpacing: 7,
        MaxScale: 1.7,
        IndicatorStyle: "dot",
        AccentHex: "#0A84FF",
        FontFamily: "Segoe UI Variable",
        DockRowInsetDip: 10,          // 图标上下各 5：面板 44+10+12 = 66 DIP（原 76，倒影开时 95）
        DockPanelPaddingXDip: 10,
        DockPanelPaddingYDip: 6,
        ReflectionDefault: false,     // 现代 macOS Dock 无倒影（用户显式打开仍生效）
        DockSeparatorsEnabled: true,  // 唯一有分隔符的主题（原生结构）
        DockSeparatorWidthDip: 1,
        DockSeparatorHeightRatio: 0.65,
        DockSeparatorSideInsetDip: 5,
        // v3 T6：macOS 图标被剪成圆角方块（Big Sur ≈ 边长的 20%）并浮在一道贴近的柔影上。
        // IconCornerRadiusRatio 仍留 0 —— 那是 XAML 的 ThemeIconClip（遮罩）口径；形状改由烘焙位图
        // 拥有，ThemeManager 在 bakesMask 时不注入 clip，于是"原样不遮罩"的老注释不再自相矛盾。
        IconMask: IconMaskStyle.RoundedRectangle,
        IconMaskRadiusRatio: 0.2,
        IconShadow: true);

    /// <summary>
    /// Aurora（极光）：Aurora 骨架 + Adwaita Depth 层压卡语义 + Plasma Flow 胶囊指示的最省路径。
    /// 参数语义与 macOS 同构（图标原样平铺 + 圆点指示 + 分隔符 + 圆角方形遮罩烘焙），差异集中在：
    /// 更松的菜单/弹层度量（MenuItemPaddingX 12、MenuItemHeight 24）、26 的面板圆角、
    /// Inter 字体（App 侧 FontProbe.FamilyFor("Aurora") 解析，OFL 内嵌 → 系统已装 → Segoe 回退）
    /// 与青绿签名色。AccentHex 是主题**签名**色：用户没显式选过强调色（仍是默认 blue）时
    /// ThemeManager 按 Id 分支落到它，用户选过任何色都用户优先。
    /// </summary>
    public static ThemeDefinition Aurora() => new(
        Id: "Aurora", DisplayName: "Aurora",
        DockBackground: Argb("#B31B1D24"),
        AcrylicTint: Argb("#B31B1D24"),
        BlurEnabled: true,
        PanelCornerRadius: 26,
        IconCornerRadiusRatio: 0,
        IconSize: 56,
        ItemSpacing: 8,
        MaxScale: 1.7,
        IndicatorStyle: "dot",
        AccentHex: "#2FD4B4",
        FontFamily: "Inter",
        DockRowInsetDip: 12,
        DockPanelPaddingXDip: 12,
        DockPanelPaddingYDip: 7,
        ReflectionDefault: false,     // 现代 Dock 无倒影（用户显式打开仍生效）
        DockSeparatorsEnabled: true,
        DockSeparatorWidthDip: 1,
        DockSeparatorHeightRatio: 0.55,
        DockSeparatorSideInsetDip: 6,
        IconMask: IconMaskStyle.RoundedRectangle,
        IconMaskRadiusRatio: 0.2,
        IconShadow: true);

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
