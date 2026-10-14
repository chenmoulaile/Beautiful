namespace Beautiful.Core.Settings;

/// <summary>
/// 应用设置（config/settings.json）。固定应用不在此处（控制器裁定：PinnedApps 只存在于
/// config/dock.json，由 App 侧 PinnedAppsStore 管理）。首次 Save 才落盘，文件缺失时一律代码默认。
/// </summary>
public sealed record AppSettings
{
    /// <summary>主题标识："harmonyos" | "macos" | "linux"。</summary>
    public string Theme { get; init; } = "harmonyos";

    /// <summary>任务栏策略："hide" | "autohide" | "keep"。</summary>
    public string TaskbarPolicy { get; init; } = "hide";

    /// <summary>开机自启（HKCU Run "Beautiful"）。</summary>
    public bool Autostart { get; init; }

    /// <summary>歌词对齐偏移（毫秒，-2000..2000，加载时钳制）。</summary>
    public int LyricsOffsetMs { get; init; }

    /// <summary>Dock 是否显示媒体胶囊条目。</summary>
    public bool ShowMediaCapsule { get; init; } = true;

    /// <summary>媒体面板失焦是否自动收起。</summary>
    public bool HidePanelOnDeactivate { get; init; } = true;

    /// <summary>是否显示 macOS 风格顶栏（Finder 栏）。</summary>
    public bool TopBarEnabled { get; init; } = true;

    /// <summary>是否接管 Alt+Tab（安装低阶键盘钩子）。</summary>
    public bool SwitcherEnabled { get; init; } = true;

    /// <summary>切换器样式："macos" | "linux" | "harmonyos"。</summary>
    public string SwitcherStyle { get; init; } = "macos";

    /// <summary>切换器触发键组合（小写规范形，如 "alt+tab" / "ctrl+alt+tab"）。</summary>
    public string SwitcherHotkey { get; init; } = "alt+tab";

    /// <summary>Dock 基准图标尺寸（32..72，加载时钳制）。</summary>
    public int DockIconSize { get; init; } = 48;

    /// <summary>悬停放大强度倍率（1.0..2.5，加载时钳制）。</summary>
    public double MagnificationStrength { get; init; } = 1.8;

    /// <summary>Dock 停靠边："bottom" | "top" | "left" | "right"。</summary>
    public string DockPosition { get; init; } = "bottom";

    /// <summary>悬停是否显示 DWM 缩略图预览。</summary>
    public bool PreviewEnabled { get; init; } = true;

    /// <summary>启动固定项时是否播放弹跳动画。</summary>
    public bool BounceEnabled { get; init; } = true;

    /// <summary>是否绘制 Dock 倒影（默认关，macOS 主题下手动开）。</summary>
    public bool ReflectionEnabled { get; init; }

    /// <summary>前台为全屏应用时是否放行原生 Alt+Tab（游戏兜底）。</summary>
    public bool FullscreenPassthrough { get; init; } = true;

    /// <summary>启动器热键（小写规范形，如 "ctrl+space"）。</summary>
    public string LauncherHotkey { get; init; } = "ctrl+space";

    /// <summary>Dock 是否自动收起。</summary>
    public bool AutoHideDock { get; init; }

    /// <summary>合法主题标识（小写）。</summary>
    public static IReadOnlyList<string> KnownThemes { get; } = ["harmonyos", "macos", "linux"];

    /// <summary>合法任务栏策略标识（小写）。</summary>
    public static IReadOnlyList<string> KnownTaskbarPolicies { get; } = ["hide", "autohide", "keep"];

    /// <summary>合法切换器样式标识（小写，与主题标识同名，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownSwitcherStyles { get; } = ["macos", "linux", "harmonyos"];

    /// <summary>合法 Dock 停靠边标识（小写，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownDockPositions { get; } = ["bottom", "top", "left", "right"];

    /// <summary>Dock 图标尺寸下限（供设置面板滑杆复用）。</summary>
    public const int MinDockIconSize = 32;

    /// <summary>Dock 图标尺寸上限。</summary>
    public const int MaxDockIconSize = 72;

    /// <summary>放大强度下限（1.0 = 不放大）。</summary>
    public const double MinMagnificationStrength = 1.0;

    /// <summary>放大强度上限。</summary>
    public const double MaxMagnificationStrength = 2.5;

    /// <summary>
    /// 加载侧规范化：数值字段钳制到合法区间（LyricsOffsetMs ±2000、DockIconSize 32..72、
    /// MagnificationStrength 1.0..2.5）；枚举串字段（主题/策略/切换器样式/停靠边）小写去空白后
    /// 比对白名单，未知值收敛为默认，避免手改配置把 App 侧映射打到兜底分支之外的未定义行为。
    /// 热键是自由文本（改键由 App 侧解析并在注册失败时提示），只做 null/空白兜底。
    /// 手改 JSON 可能把字符串字段写成 null（System.Text.Json 会把 null 填进非可空 string 属性）
    /// 或空串/纯空白，一律兜默认，避免 NRE 漏出 Load 的自愈网；布尔字段无需规范化——JSON 里写成
    /// 非布尔会触发 JsonException，由 AppSettingsStore 按损坏文件处理。
    /// </summary>
    public AppSettings Normalized() => this with
    {
        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs, -2000, 2000),
        Theme = Canonical(Theme, "harmonyos", KnownThemes),
        TaskbarPolicy = Canonical(TaskbarPolicy, "hide", KnownTaskbarPolicies),
        SwitcherStyle = Canonical(SwitcherStyle, "macos", KnownSwitcherStyles),
        DockPosition = Canonical(DockPosition, "bottom", KnownDockPositions),
        SwitcherHotkey = HotkeyOrDefault(SwitcherHotkey, "alt+tab"),
        LauncherHotkey = HotkeyOrDefault(LauncherHotkey, "ctrl+space"),
        DockIconSize = Math.Clamp(DockIconSize, MinDockIconSize, MaxDockIconSize),
        MagnificationStrength = Math.Clamp(MagnificationStrength, MinMagnificationStrength, MaxMagnificationStrength)
    };

    /// <summary>枚举串规范化：null/空白/大小写与首尾空白差异都归一为小写标识；不在白名单内取兜底。</summary>
    private static string Canonical(string? raw, string fallback, IReadOnlyList<string> allowed)
    {
        var value = (raw ?? fallback).Trim().ToLowerInvariant();
        return allowed.Contains(value) ? value : fallback;
    }

    /// <summary>热键串规范化：保留用户自定义组合，仅 null/空白兜默认；统一小写便于解析与比对。</summary>
    private static string HotkeyOrDefault(string? raw, string fallback)
    {
        var value = raw?.Trim();
        return string.IsNullOrEmpty(value) ? fallback : value.ToLowerInvariant();
    }
}
