using System.Globalization;

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

    /// <summary>
    /// Dock 所在显示器：auto = 启动时光标所在显示器（解析结果按进程粘住，重排不会因鼠标划过别屏而
    /// 让 Dock 跳走）、primary = 主显示器、其余为设置面板列出的十进制序号（1 起）。
    /// </summary>
    public string DockMonitor { get; init; } = "auto";

    /// <summary>顶栏所在显示器（取值口径同 <see cref="DockMonitor"/>；两条 surface 可以分处两屏）。</summary>
    public string TopBarMonitor { get; init; } = "auto";

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

    /// <summary>显示器选择器的关键字取值（序号形另由 <see cref="MaxMonitorIndex"/> 界定）。</summary>
    public static IReadOnlyList<string> KnownMonitorKeywords { get; } = ["auto", "primary"];

    /// <summary>
    /// 显示器序号上限：Core 不知道在屏有几块显示器（那是运行时的事，由 App 侧 MonitorGeometry
    /// 解析并在越界时回落 auto），这里只挡住"手改 JSON 写了个荒谬台数"的情况。
    /// </summary>
    public const int MaxMonitorIndex = 16;

    /// <summary>
    /// 加载侧规范化：数值字段钳制到合法区间（LyricsOffsetMs ±2000、DockIconSize 32..72、
    /// MagnificationStrength 1.0..2.5）；枚举串字段（主题/策略/切换器样式/停靠边）小写去空白后
    /// 比对白名单，未知值收敛为默认，避免手改配置把 App 侧映射打到兜底分支之外的未定义行为。
    /// 显示器选择器（DockMonitor/TopBarMonitor）走 <see cref="MonitorSelectorOrDefault"/>：
    /// 关键字（auto/primary）之外只接受 1..MaxMonitorIndex 的十进制序号。
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
        MagnificationStrength = Math.Clamp(MagnificationStrength, MinMagnificationStrength, MaxMagnificationStrength),
        DockMonitor = MonitorSelectorOrDefault(DockMonitor),
        TopBarMonitor = MonitorSelectorOrDefault(TopBarMonitor)
    };

    /// <summary>枚举串规范化：null/空白/大小写与首尾空白差异都归一为小写标识；不在白名单内取兜底。</summary>
    private static string Canonical(string? raw, string fallback, IReadOnlyList<string> allowed)
    {
        var value = (raw ?? fallback).Trim().ToLowerInvariant();
        return allowed.Contains(value) ? value : fallback;
    }

    /// <summary>
    /// 显示器选择器规范化：null/空白/非法 → auto；关键字归小写；序号写成 "+2"/" 3 "/全角数字等
    /// 都归一成裸十进制串（落盘与内存同源，App 侧 MonitorGeometry 只需 TryParse 一次）。
    /// 序号是否**真的存在**只有运行时知道（在屏显示器数），越界由 App 侧回落 auto 并留日志，
    /// 这里不做也无法做那一层校验。
    /// </summary>
    private static string MonitorSelectorOrDefault(string? raw)
    {
        var value = (raw ?? "auto").Trim().ToLowerInvariant();
        if (KnownMonitorKeywords.Contains(value)) return value;
        // NumberStyles.Number：允许前导 +/-、千位分隔与小数点——"2.0" 这类手写法也能落到序号 2，
        // 但只有"整数且无小数余数"才认（(int)n != n 时是 2.5 之类的非法台数）。
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 1 && parsed <= MaxMonitorIndex && Math.Abs(parsed - Math.Truncate(parsed)) < double.Epsilon)
            return ((int)parsed).ToString(CultureInfo.InvariantCulture);
        return "auto";
    }

    /// <summary>热键串规范化：保留用户自定义组合，仅 null/空白兜默认；统一小写便于解析与比对。</summary>
    private static string HotkeyOrDefault(string? raw, string fallback)
    {
        var value = raw?.Trim();
        return string.IsNullOrEmpty(value) ? fallback : value.ToLowerInvariant();
    }
}
