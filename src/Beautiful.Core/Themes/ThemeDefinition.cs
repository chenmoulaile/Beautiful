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
    /// <summary>控制器裁定：MaxScale&gt;1 才启用高斯放大（鸿蒙/Linux 无放大）。</summary>
    public bool ShowMagnification => MaxScale > 1.0;

    /// <summary>
    /// 分隔符条目盒宽（DIP）：线粗 + 两侧对称留白。模板把它写成 ThemeSeparatorBoxWidthDip，
    /// 行内还各侧再加一个 ItemSpacing，合计约两个图标间距——正是 macOS Dock 分隔符处的视觉留白。
    /// </summary>
    public double DockSeparatorBoxWidthDip => DockSeparatorWidthDip + 2.0 * DockSeparatorSideInsetDip;

    /// <summary>
    /// 鸿蒙：深色玻璃 #61000000（默认深色；浅色变体 #59FFFFFF 由 Task 17 系统主题跟随提供），
    /// squircle 连续圆角（Superellipse nExp=4.4），无放大（胶囊悬浮反馈，Task 15 实装）。
    /// 原生鸿蒙 dock 是一条连续列表（无分隔符），胶囊留白最松。
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
        FontFamily: "HarmonyOS Sans",
        DockRowInsetDip: 12,          // 图标上下各 6：胶囊观感 roomy（原生比 macOS 松）
        DockPanelPaddingXDip: 12,
        DockPanelPaddingYDip: 8,      // IconSize=44 → 面板 44+12+16 = 72 DIP
        ReflectionDefault: false,     // 鸿蒙 dock 无倒影
        DockSeparatorsEnabled: false, // 连续列表，无分隔符
        DockSeparatorWidthDip: 0,
        DockSeparatorHeightRatio: 0,
        DockSeparatorSideInsetDip: 0,
        IconMask: IconMaskStyle.Squircle,      // 既有 ThemeIconClip 就是 squircle（ratio 0.25），烘焙接管形状后同一观感
        IconMaskRadiusRatio: 0,                // squircle 用超椭圆指数，不用半径
        IconShadow: true);

    /// <summary>
    /// macOS：暗色亚克力 #B4202020（tint 同底色）、系统蓝 #0A84FF、抛物线放大 MaxScale=1.8、
    /// 指示点=图标下方小圆点。原生 Dock **紧贴图标**（行内只留约 10 DIP 呼吸）、
    /// 有「固定应用 | 正在运行 | 废纸篓」两道分隔符、现代 macOS **无倒影**。
    /// </summary>
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
    /// Linux(GNOME)：#E61C1C1C 实底（BlurEnabled=false 禁用亚克力）、accent #3584E4、无放大、
    /// 左侧短条指示、圆角方形图标（半径 8）。dash-to-dock 是**连续列表**（应用文件夹也在同一列里），
    /// 无分隔符；面板比方角 macOS 略松一点，但仍远紧于 v2 的统一余量。
    /// </summary>
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
        FontFamily: "Segoe UI",
        DockRowInsetDip: 16,          // dash 给图标四周留白（左侧短条也在这格里）
        DockPanelPaddingXDip: 8,
        DockPanelPaddingYDip: 5,      // IconSize=44 → 面板 44+16+10 = 70 DIP
        ReflectionDefault: false,
        DockSeparatorsEnabled: false,
        DockSeparatorWidthDip: 0,
        DockSeparatorHeightRatio: 0,
        DockSeparatorSideInsetDip: 0,
        // GNOME dash 的图标是**平铺**的：柔影与 squircle 都不是它的语言，所以烘焙不参与形状/投影
        // （IconMask=None → ThemeManager 继续注入 ThemeIconClip 的 8px 圆角，形状属主留在 XAML）。
        IconMask: IconMaskStyle.None,
        IconMaskRadiusRatio: 0,
        IconShadow: false);

    /// <summary>
    /// Windows 11 原生（第四条线）：深色 Mica 底、8px 圆角、**不遮罩图标**、无悬停放大、
    /// 运行指示是"图标下方居中短横线"。禁模糊是刻意的——Win11 的深色任务栏是一层接近实色的
    /// Mica，不是 macOS 那种透出桌面轮廓的亚克力；做成半透明模糊反而不像它。
    /// （"壁纸取色"开着时强调色同样被壁纸主色覆盖，与另三主题同一机制。）
    /// </summary>
    public static ThemeDefinition Windows() => new(
        Id: "Windows", DisplayName: "Windows 11",
        DockBackground: Argb("#F02B2B2B"),
        AcrylicTint: Argb("#F02B2B2B"),
        BlurEnabled: false,
        PanelCornerRadius: 8,
        IconCornerRadiusRatio: 0,
        IconSize: 44,
        ItemSpacing: 8,
        MaxScale: 1.0,
        IndicatorStyle: "underline",
        AccentHex: "#0078D4",
        FontFamily: "Segoe UI Variable Display",
        DockRowInsetDip: 12,
        DockPanelPaddingXDip: 12,
        DockPanelPaddingYDip: 8,          // IconSize=44 → 面板 44+12+16 = 72 DIP
        ReflectionDefault: false,         // Win11 任务栏没有倒影
        DockSeparatorsEnabled: false,     // 固定区与运行区之间没有竖线
        DockSeparatorWidthDip: 0,
        DockSeparatorHeightRatio: 0,
        DockSeparatorSideInsetDip: 0,
        IconMask: IconMaskStyle.None,
        IconMaskRadiusRatio: 0,
        IconShadow: false);

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
