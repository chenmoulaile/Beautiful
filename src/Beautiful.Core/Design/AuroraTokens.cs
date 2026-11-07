namespace Beautiful.Core.Design;

/// <summary>
/// Aurora（极光）设计 token：暗/亮两套完整值，字段面 = <see cref="MacTokens"/> 全字段（Mac* 键的值源，
/// AuroraTokenDictionary 逐键写入，QuickLook/Finder/通知横幅等 Mac* 消费端才不断键）+ Aur* 扩展
/// （层压卡/弹层底/玻璃公式/弹簧曲线）。渲染器/控件经资源键引用，Core 保持无 WPF 依赖。
/// <para>
/// 与 MacTokens 的关系：键面同构但值面独立（Aurora 是自己的色板，不是 macOS 亮/暗的微调）——
/// 例如暗色窗底 #FF17181C（macOS #FF1E1E1E）、弹层圆角 14（macOS 8）。accent 派生键
/// （AurCardFillOn/AurSelectionFill/AurAccentSoft/AurHairlineBrush 等）随强调色变化，
/// 属运行时计算，放 App 侧 AuroraTokenDictionary，这里只存字面值与玻璃公式参数。
/// </para>
/// </summary>
public sealed record AuroraTokens
{
    public bool IsDark { get; init; }

    // ---------- 颜色：表面（键面与 MacTokens 同构） ----------
    public string WindowBackground { get; init; } = "";
    public string WindowBorder { get; init; } = "";
    public string MenuBarBackground { get; init; } = "";
    public string MenuBarText { get; init; } = "";
    public string MenuBarHairline { get; init; } = "";
    public string MenuBackground { get; init; } = "";
    public string MenuText { get; init; } = "";
    public string MenuTextSecondary { get; init; } = "";
    public string MenuSeparator { get; init; } = "";
    /// <summary>静态默认 = Aurora 签名色；运行时被强调色画刷覆写（MacMenuSelection 消费端）。</summary>
    public string MenuSelection { get; init; } = "";
    /// <summary>必须能被解析为 SolidColorBrush（ThumbnailPreview.ReadColor 只认实色）。</summary>
    public string DockBackground { get; init; } = "";
    public string DockBorder { get; init; } = "";
    public string DockSeparator { get; init; } = "";
    /// <summary>静态默认 = Aurora 签名色；运行时被强调色画刷覆写（Dock 运行指示点）。</summary>
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
    /// <summary>正文族（运行时被 FontProbe.FamilyFor("Aurora") 的解析结果覆写；这里是探测失败兜底串）。</summary>
    public string FontText { get; init; } = "";
    /// <summary>标题族：独立于正文（正文 Inter Regular/Medium，标题 Inter Display Medium，层级靠字重差）。</summary>
    public string FontDisplay { get; init; } = "";
    /// <summary>等宽族：Windows 自带 Cascadia Mono 优先（JetBrains Mono 捆绑移出，少一份 OFL 义务）。</summary>
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
    /// <summary>CSS 语义的曲线名（文档/单一来源口径，与 MacTokens.EaseOut 同约定；各动画点当前为硬编码 CubicEase）。</summary>
    public string EaseOut { get; init; } = "";
    public string EaseInOut { get; init; } = "";

    // ---------- Aur* 扩展（Aurora 血统） ----------
    /// <summary>Level2 卡底（语义与 MacPanelBackground 解耦：卡 ≠ 面板，Adwaita Depth 层压）。</summary>
    public string CardFill { get; init; } = "";
    public string CardFillHover { get; init; } = "";
    public string CardBorder { get; init; } = "";
    /// <summary>Dock 条目 hover 瓦片。</summary>
    public string HoverTile { get; init; } = "";
    /// <summary>玻璃公式中性基色（#RRGGBB 字符串，供 App 侧公式解析；墙纸取色 tint 在其上混入）。</summary>
    public string GlassBase { get; init; } = "";
    /// <summary>墙纸取色混入色（#AARRGGBB；默认全透明 = 取色关闭，P2 工作包写入）。</summary>
    public string WallpaperTint { get; init; } = "";
    /// <summary>弹层实心底渐变（上→下两停；"渲染恒为深色玻璃"旧裁定在 Aurora 放开的载体）。</summary>
    public string PopupFillTop { get; init; } = "";
    public string PopupFillBottom { get; init; } = "";
    /// <summary>弹层模糊背板上的遮罩（实色，保文字对比度）。</summary>
    public string PopupFillBlur { get; init; } = "";
    /// <summary>微过冲弹簧曲线（CSS 语义；WPF 落 KeySpline(0.34,1.56,0.64,1)，y 超 1 才出得过冲）。</summary>
    public string SpringEase { get; init; } = "";
    /// <summary>Level2 卡入场时长（ms）。</summary>
    public int CardEnterMs { get; init; }

    /// <summary>Aurora 深色 token。</summary>
    public static AuroraTokens Dark { get; } = new()
    {
        IsDark = true,
        WindowBackground = "#FF17181C",
        WindowBorder = "#2EFFFFFF",
        MenuBarBackground = "#C61A1B22",
        MenuBarText = "#FFFFFFFF",
        MenuBarHairline = "#24FFFFFF",
        MenuBackground = "#EE1B1D24",
        MenuText = "#FFFFFFFF",
        MenuTextSecondary = "#C2FFFFFF",
        MenuSeparator = "#26FFFFFF",
        MenuSelection = "#FF2FD4B4",
        DockBackground = "#B31B1D24",
        DockBorder = "#33FFFFFF",
        DockSeparator = "#24FFFFFF",
        DockIndicator = "#FF2FD4B4",
        PanelBackground = "#2BFFFFFF",
        PanelBorder = "#24FFFFFF",
        PopupSolid = "#F01B1D24",
        PopupScrim = "#800F1014",
        TooltipBackground = "#E61F2129",
        TextPrimary = "#FFFFFFFF",
        TextSecondary = "#C2FFFFFF",
        TextTertiary = "#78FFFFFF",
        ControlFill = "#33FFFFFF",
        ControlFillHover = "#47FFFFFF",
        Separator = "#24FFFFFF",
        BackdropTint = "#4A14151C",
        MenuBarHeight = 24,
        MenuBarFontSize = 13,
        MenuItemPaddingX = 12,
        DockIconSize = 56,
        DockItemSpacing = 8,
        DockEdgeMargin = 4,
        DockPanelRadiusRatio = 0.48,
        DockPanelPaddingX = 12,
        DockPanelPaddingY = 7,
        PopupCornerRadius = 14,
        MenuItemHeight = 24,
        WindowCornerRadius = 12,
        ControlCornerRadius = 9,
        FontText = "Inter, Inter Medium, Inter SemiBold, Inter Display, Segoe UI Variable, Segoe UI",
        FontDisplay = "Inter Display, Inter, Segoe UI Variable Display, Segoe UI",
        FontMono = "Cascadia Mono, Consolas",
        FontSizeBody = 13,
        FontSizeCaption = 11,
        MenuPopupMs = 140,
        MenuCollapseMs = 110,
        DockSlideMs = 260,
        LaunchpadMs = 280,
        WindowOpenMs = 300,
        GenieMs = 500,
        EaseOut = "cubic-bezier(0,0,0.2,1)",
        EaseInOut = "cubic-bezier(0.4,0,0.2,1)",
        CardFill = "#33FFFFFF",
        CardFillHover = "#40FFFFFF",
        CardBorder = "#24FFFFFF",
        HoverTile = "#2BFFFFFF",
        GlassBase = "#1E2027",
        WallpaperTint = "#00000000",
        PopupFillTop = "#EE1B1D24",
        PopupFillBottom = "#E617181E",
        PopupFillBlur = "#88131318",
        SpringEase = "cubic-bezier(0.34,1.56,0.64,1)",
        CardEnterMs = 180,
    };

    /// <summary>Aurora 浅色 token。</summary>
    public static AuroraTokens Light { get; } = new()
    {
        IsDark = false,
        WindowBackground = "#FFF3F5F8",
        WindowBorder = "#24000000",
        MenuBarBackground = "#C8F3F5F8",
        MenuBarText = "#FF17181C",
        MenuBarHairline = "#24000000",
        MenuBackground = "#F2FAFBFD",
        MenuText = "#FF17181C",
        MenuTextSecondary = "#B317181C",
        MenuSeparator = "#24000000",
        MenuSelection = "#FF1FA88C",
        DockBackground = "#A8F1F3F7",
        DockBorder = "#40000000",
        DockSeparator = "#2E000000",
        DockIndicator = "#FF1FA88C",
        PanelBackground = "#59FFFFFF",
        PanelBorder = "#1F000000",
        PopupSolid = "#F6FAFCFE",
        PopupScrim = "#66EEF1F5",
        TooltipBackground = "#F2FFFFFF",
        TextPrimary = "#FF17181C",
        TextSecondary = "#B317181C",
        TextTertiary = "#7317181C",
        ControlFill = "#1F000000",
        ControlFillHover = "#33000000",
        Separator = "#24000000",
        BackdropTint = "#4DFFFFFF",
        MenuBarHeight = 24,
        MenuBarFontSize = 13,
        MenuItemPaddingX = 12,
        DockIconSize = 56,
        DockItemSpacing = 8,
        DockEdgeMargin = 4,
        DockPanelRadiusRatio = 0.48,
        DockPanelPaddingX = 12,
        DockPanelPaddingY = 7,
        PopupCornerRadius = 14,
        MenuItemHeight = 24,
        WindowCornerRadius = 12,
        ControlCornerRadius = 9,
        FontText = "Inter, Inter Medium, Inter SemiBold, Inter Display, Segoe UI Variable, Segoe UI",
        FontDisplay = "Inter Display, Inter, Segoe UI Variable Display, Segoe UI",
        FontMono = "Cascadia Mono, Consolas",
        FontSizeBody = 13,
        FontSizeCaption = 11,
        MenuPopupMs = 140,
        MenuCollapseMs = 110,
        DockSlideMs = 260,
        LaunchpadMs = 280,
        WindowOpenMs = 300,
        GenieMs = 500,
        EaseOut = "cubic-bezier(0,0,0.2,1)",
        EaseInOut = "cubic-bezier(0.4,0,0.2,1)",
        CardFill = "#66FFFFFF",
        CardFillHover = "#73FFFFFF",
        CardBorder = "#1F000000",
        HoverTile = "#59FFFFFF",
        GlassBase = "#E9EDF3",
        WallpaperTint = "#00000000",
        PopupFillTop = "#F0FAFCFE",
        PopupFillBottom = "#EAF4F7FA",
        PopupFillBlur = "#88EEF2F7",
        SpringEase = "cubic-bezier(0.34,1.56,0.64,1)",
        CardEnterMs = 180,
    };

    public static AuroraTokens For(bool dark) => dark ? Dark : Light;
}
