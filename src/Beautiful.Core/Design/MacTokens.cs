namespace Beautiful.Core.Design;

/// <summary>
/// macOS Big Sur/Monterey 设计 token（v4 Phase 1 T4）。亮/暗两套完整值，所有视觉常量只在这里定义，
/// 渲染器/控件通过资源键引用；未来重定 macOS 版本只改 token 值，不改 XAML 结构。
/// <para>
/// 值与 docs/macos-spec.md 的 §6 速查表一一对应（@1x DIP）。颜色用 #AARRGGBB 或 #RRGGBB，
/// 由 App 侧 <c>MacTokenDictionary</c> 解析为冻结画刷；Core 保持无 WPF 依赖。
/// </para>
/// <para>
/// 字族只写"首选 + 回退链"的字体族名：SF Pro 是否命中由 App 侧 FontResolution 探测，
/// 未携带字体时自然回退 Segoe UI Variable / Segoe UI（CI 形态）。
/// </para>
/// </summary>
public sealed record MacTokens
{
    public bool IsDark { get; init; }

    // ---------- 颜色：表面 ----------
    public string WindowBackground { get; init; } = "";
    public string WindowBorder { get; init; } = "";
    public string MenuBarBackground { get; init; } = "";
    public string MenuBarText { get; init; } = "";
    public string MenuBarHairline { get; init; } = "";
    public string MenuBackground { get; init; } = "";
    public string MenuText { get; init; } = "";
    public string MenuTextSecondary { get; init; } = "";
    public string MenuSeparator { get; init; } = "";
    public string MenuSelection { get; init; } = "";
    public string DockBackground { get; init; } = "";
    public string DockBorder { get; init; } = "";
    public string DockSeparator { get; init; } = "";
    public string DockIndicator { get; init; } = "";
    public string PanelBackground { get; init; } = "";
    public string PanelBorder { get; init; } = "";
    public string PopupSolid { get; init; } = "";
    public string PopupScrim { get; init; } = "";
    public string TooltipBackground { get; init; } = "";

    // ---------- 颜色：文字与控件 ----------
    public string TextPrimary { get; init; } = "";
    public string TextSecondary { get; init; } = "";
    public string TextTertiary { get; init; } = "";
    public string ControlFill { get; init; } = "";
    public string ControlFillHover { get; init; } = "";
    public string Separator { get; init; } = "";
    public string BackdropTint { get; init; } = "";

    // ---------- 度量（DIP） ----------
    public double MenuBarHeight { get; init; }
    public double MenuBarFontSize { get; init; }
    public double MenuItemPaddingX { get; init; }
    public double DockIconSize { get; init; }
    public double DockItemSpacing { get; init; }
    public double DockEdgeMargin { get; init; }
    public double DockPanelRadiusRatio { get; init; }
    public double DockPanelPaddingX { get; init; }
    public double DockPanelPaddingY { get; init; }
    public double PopupCornerRadius { get; init; }
    public double MenuItemHeight { get; init; }
    public double WindowCornerRadius { get; init; }
    public double ControlCornerRadius { get; init; }

    // ---------- 字体 ----------
    public string FontText { get; init; } = "";
    public string FontDisplay { get; init; } = "";
    public string FontMono { get; init; } = "";
    public double FontSizeBody { get; init; }
    public double FontSizeCaption { get; init; }

    // ---------- 动效 ----------
    public int MenuPopupMs { get; init; }
    public int MenuCollapseMs { get; init; }
    public int DockSlideMs { get; init; }
    public int LaunchpadMs { get; init; }
    public int WindowOpenMs { get; init; }
    public int GenieMs { get; init; }
    /// <summary>CSS 语义的曲线名（App 侧映射到 WPF 缓动）；只作文档/单一来源，不在 Core 引 WPF。</summary>
    public string EaseOut { get; init; } = "";
    public string EaseInOut { get; init; } = "";

    /// <summary>深色（Big Sur 暗色）token。亮色=白色 60-70% 实时模糊；暗色=深灰 45-60%。</summary>
    public static MacTokens Dark { get; } = new()
    {
        IsDark = true,
        WindowBackground = "#FF1E1E1E",
        WindowBorder = "#33FFFFFF",
        MenuBarBackground = "#B31C1C1E",
        MenuBarText = "#FFFFFFFF",
        MenuBarHairline = "#33FFFFFF",
        MenuBackground = "#E62C2C2E",
        MenuText = "#FFFFFFFF",
        MenuTextSecondary = "#B3FFFFFF",
        MenuSeparator = "#33FFFFFF",
        MenuSelection = "#FF0A84FF",
        DockBackground = "#991F1F21",
        DockBorder = "#33FFFFFF",
        DockSeparator = "#33FFFFFF",
        DockIndicator = "#CCFFFFFF",
        PanelBackground = "#26FFFFFF",
        PanelBorder = "#33FFFFFF",
        PopupSolid = "#E62C2C2E",
        PopupScrim = "#8C1C1C1E",
        TooltipBackground = "#E6333335",
        TextPrimary = "#FFFFFFFF",
        TextSecondary = "#B3FFFFFF",
        TextTertiary = "#66FFFFFF",
        ControlFill = "#33FFFFFF",
        ControlFillHover = "#4DFFFFFF",
        Separator = "#33FFFFFF",
        BackdropTint = "#4D202020",
        MenuBarHeight = 24,
        MenuBarFontSize = 13,
        MenuItemPaddingX = 10,
        DockIconSize = 56,
        DockItemSpacing = 7,
        DockEdgeMargin = 4,
        DockPanelRadiusRatio = 0.44,
        DockPanelPaddingX = 12,
        DockPanelPaddingY = 8,
        PopupCornerRadius = 8,
        MenuItemHeight = 21,
        WindowCornerRadius = 10,
        ControlCornerRadius = 6,
        FontText = "SF Pro Text, Segoe UI Variable, Segoe UI",
        FontDisplay = "SF Pro Display, SF Pro Text, Segoe UI Variable Display, Segoe UI",
        FontMono = "SF Mono, Cascadia Mono, Consolas",
        FontSizeBody = 13,
        FontSizeCaption = 11,
        MenuPopupMs = 120,
        MenuCollapseMs = 100,
        DockSlideMs = 260,
        LaunchpadMs = 280,
        WindowOpenMs = 280,
        GenieMs = 500,
        EaseOut = "cubic-bezier(0,0,0.58,1)",
        EaseInOut = "cubic-bezier(0.42,0,0.58,1)",
    };

    /// <summary>浅色（Big Sur 亮色）token。</summary>
    public static MacTokens Light { get; } = new()
    {
        IsDark = false,
        WindowBackground = "#FFF5F5F7",
        WindowBorder = "#1A000000",
        MenuBarBackground = "#B3F2F2F7",
        MenuBarText = "#FF000000",
        MenuBarHairline = "#33000000",
        MenuBackground = "#F2F7F7F9",
        MenuText = "#FF1D1D1F",
        MenuTextSecondary = "#99000000",
        MenuSeparator = "#1F000000",
        MenuSelection = "#FF007AFF",
        DockBackground = "#B3F5F5F7",
        DockBorder = "#4D000000",
        DockSeparator = "#33000000",
        DockIndicator = "#8C000000",
        PanelBackground = "#1A000000",
        PanelBorder = "#1A000000",
        PopupSolid = "#F2F7F7F9",
        PopupScrim = "#66FFFFFF",
        TooltipBackground = "#F2FFFFFF",
        TextPrimary = "#FF1D1D1F",
        TextSecondary = "#99000000",
        TextTertiary = "#4D000000",
        ControlFill = "#1A000000",
        ControlFillHover = "#26000000",
        Separator = "#1A000000",
        BackdropTint = "#4DFFFFFF",
        MenuBarHeight = 24,
        MenuBarFontSize = 13,
        MenuItemPaddingX = 10,
        DockIconSize = 56,
        DockItemSpacing = 7,
        DockEdgeMargin = 4,
        DockPanelRadiusRatio = 0.44,
        DockPanelPaddingX = 12,
        DockPanelPaddingY = 8,
        PopupCornerRadius = 8,
        MenuItemHeight = 21,
        WindowCornerRadius = 10,
        ControlCornerRadius = 6,
        FontText = "SF Pro Text, Segoe UI Variable, Segoe UI",
        FontDisplay = "SF Pro Display, SF Pro Text, Segoe UI Variable Display, Segoe UI",
        FontMono = "SF Mono, Cascadia Mono, Consolas",
        FontSizeBody = 13,
        FontSizeCaption = 11,
        MenuPopupMs = 120,
        MenuCollapseMs = 100,
        DockSlideMs = 260,
        LaunchpadMs = 280,
        WindowOpenMs = 280,
        GenieMs = 500,
        EaseOut = "cubic-bezier(0,0,0.58,1)",
        EaseInOut = "cubic-bezier(0.42,0,0.58,1)",
    };

    public static MacTokens For(bool dark) => dark ? Dark : Light;
}
