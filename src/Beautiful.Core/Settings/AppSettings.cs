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

    // ---------- 顶栏时钟形态（v3.1 缺陷 3：此前顶栏只有一种写法，用户无法配置） ----------
    // 四个开关的**语义与组合规则全在 Core.TopBar.ClockFormat**（模板拼装 + 刷新步长），这里只是持久化载体；
    // 默认值 (false, true, true, true) 与 v3.0.0 顶栏逐字符一致，所以升级后观感不变。
    // 布尔项无需 <see cref="Normalized"/> 钳制：JSON 里写成非布尔会触发 JsonException，
    // 由 AppSettingsStore 按"损坏文件"整体自愈（回到代码默认），不会留下半套值。

    /// <summary>顶栏时钟是否显示秒。<b>它同时决定刷新节奏</b>：开=对齐秒边界每秒重算，关=对齐分边界每分钟重算。</summary>
    public bool ClockShowSeconds { get; init; }

    /// <summary>顶栏时钟是否显示"几月几日"。</summary>
    public bool ClockShowDate { get; init; } = true;

    /// <summary>顶栏时钟是否显示"周几"（与日期独立：可以只要"周三 21:27"）。</summary>
    public bool ClockShowWeekday { get; init; } = true;

    /// <summary>顶栏时钟是否用 24 小时制（false = 12 小时制 + 上午/下午）。</summary>
    public bool ClockUse24Hour { get; init; } = true;

    /// <summary>是否接管 Alt+Tab（安装低阶键盘钩子）。</summary>
    public bool SwitcherEnabled { get; init; } = true;

    /// <summary>切换器样式："macos" | "linux" | "harmonyos"。</summary>
    public string SwitcherStyle { get; init; } = "macos";

    /// <summary>切换器触发键组合（小写规范形，如 "alt+tab" / "ctrl+alt+tab"）。</summary>
    public string SwitcherHotkey { get; init; } = "alt+tab";

    /// <summary>Dock 基准图标尺寸（32..72，加载时钳制）。</summary>
    public int DockIconSize { get; init; } = 48;

    /// <summary>悬停放大强度倍率（1.0..2.5，加载时钳制）。默认值与 <see cref="DefaultMagnificationStrength"/> 同源。</summary>
    public double MagnificationStrength { get; init; } = DefaultMagnificationStrength;

    /// <summary>Dock 停靠边："bottom" | "top" | "left" | "right"。</summary>
    public string DockPosition { get; init; } = "bottom";

    /// <summary>悬停是否显示 DWM 缩略图预览。</summary>
    public bool PreviewEnabled { get; init; } = true;

    /// <summary>启动固定项时是否播放弹跳动画。</summary>
    public bool BounceEnabled { get; init; } = true;

    /// <summary>
    /// 是否绘制 Dock 倒影。**三态**：null = 跟随主题默认（<c>ThemeDefinition.ReflectionDefault</c>），
    /// true/false = 用户在设置页显式选择（优先级更高，切主题不再把它冲掉）。
    /// v3 T4：现代 macOS / GNOME / 鸿蒙 dock 都没有倒影，主题默认一律关；真机用户已开过的仍照旧生效。
    /// </summary>
    public bool? ReflectionEnabled { get; init; }

    /// <summary>倒影实际取值：用户未显式选择时落主题默认。</summary>
    public bool ReflectionEnabledFor(bool themeDefault) => ReflectionEnabled ?? themeDefault;

    /// <summary>前台为全屏应用时是否放行原生 Alt+Tab（游戏兜底）。</summary>
    public bool FullscreenPassthrough { get; init; } = true;

    /// <summary>
    /// 启动器（v3 T7 Spotlight 式搜索面板）是否可用：热键与顶栏入口都受这一项控制。
    /// 关掉时键盘钩子不再为它保活（见 SwitcherService.KeepHookForLauncher）。
    /// </summary>
    public bool LauncherEnabled { get; init; } = true;

    /// <summary>
    /// 启动器热键（小写规范形，如 "ctrl+alt+space"）。
    /// <b>默认不能用 Ctrl+Space</b>：那是系统 IME 中英切换的既有绑定，占掉它等于让用户在所有应用里
    /// 敲不出空格（v3 裁定：宁可键位土一点，也不抢系统级组合），因此给 Ctrl+Alt+Space 这种
    /// 系统未占用、且不需要 Win 键的组合。
    /// </summary>
    public string LauncherHotkey { get; init; } = "ctrl+alt+space";

    /// <summary>
    /// 是否用当前壁纸的取色结果覆盖主题强调色（v3 T7）。关（默认）时主题观感与取色功能上线前完全一致；
    /// 开时由 App 侧读壁纸 → <see cref="Imaging.DominantColor"/> 取色 → 覆盖 <c>ThemeAccent</c> 一个资源键，
    /// <b>不改任何 Themes/*.xaml 的颜色值</b>（取不到色即回落，见 App 侧 WallpaperAccentService）。
    /// </summary>
    public bool UseWallpaperAccent { get; init; }

    /// <summary>Dock 是否自动收起。</summary>
    public bool AutoHideDock { get; init; }

    /// <summary>
    /// 首次运行（还没有 <c>dock.json</c>）时，是否把 Windows 原生任务栏上已固定的应用接进 Dock。
    /// 默认开：用户既然在任务栏固定过，就是要它们在手边，不该再手动固定一遍。
    /// 只影响<b>第一次</b>建列表那一刻；之后用户怎么整理 Dock 都不再被外部来源改动。
    /// </summary>
    public bool ImportTaskbarPins { get; init; } = true;

    /// <summary>
    /// 是否给最小化/最大化加 macOS 式动画（v3.2）：最小化把窗口画面切成条带收进 Dock 图标，
    /// 最大化/还原则做一段缩放过渡。关掉即回到"瞬间消失/瞬间变大"的系统行为。
    /// </summary>
    public bool MinimizeAnimation { get; init; } = true;

    /// <summary>
    /// 顶栏是否显示"托盘图标区"入口（v3.2）：点开可看到通知区域里被收起的第三方应用图标，
    /// 点击/右键会转发回图标自己的窗口。关掉只是少一颗按钮，不影响系统托盘本身。
    /// </summary>
    public bool ShowTrayInTopBar { get; init; } = true;

    /// <summary>
    /// Dock 所在显示器：auto = 启动时光标所在显示器（解析结果按进程粘住，重排不会因鼠标划过别屏而
    /// 让 Dock 跳走）、primary = 主显示器、其余为设置面板列出的十进制序号（1 起）。
    /// </summary>
    public string DockMonitor { get; init; } = "auto";

    /// <summary>顶栏所在显示器（取值口径同 <see cref="DockMonitor"/>；两条 surface 可以分处两屏）。</summary>
    public string TopBarMonitor { get; init; } = "auto";

    /// <summary>
    /// 图标处理方式（v3 T6）："auto"（跟随主题：macOS 圆角方块+投影 / 鸿蒙 squircle+投影 / GNOME 平铺）、
    /// "mask"（强制遮罩+高光+投影）、"plain"（只做 2× 超采样降到当前 DPI 的像素尺寸，不烧形状与投影，
    /// 形状属主交回 XAML 的 ThemeIconClip）。取值解释集中在 <see cref="Imaging.IconTreatment"/>。
    /// </summary>
    public string IconTreatment { get; init; } = "auto";

    /// <summary>合法主题标识（小写）。</summary>
    public static IReadOnlyList<string> KnownThemes { get; } = ["harmonyos", "macos", "linux"];

    /// <summary>合法任务栏策略标识（小写）。</summary>
    public static IReadOnlyList<string> KnownTaskbarPolicies { get; } = ["hide", "autohide", "keep"];

    /// <summary>合法切换器样式标识（小写，与主题标识同名，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownSwitcherStyles { get; } = ["macos", "linux", "harmonyos"];

    /// <summary>合法 Dock 停靠边标识（小写，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownDockPositions { get; } = ["bottom", "top", "left", "right"];

    /// <summary>
    /// 合法图标处理方式标识（小写，v3 T6；白名单与解释器同源，避免设置面板和 Core 各写一份 switch）。
    /// </summary>
    public static IReadOnlyList<string> KnownIconTreatments { get; } = Imaging.IconTreatment.Known;

    /// <summary>Dock 图标尺寸下限（供设置面板滑杆复用）。</summary>
    public const int MinDockIconSize = 32;

    /// <summary>Dock 图标尺寸上限。</summary>
    public const int MaxDockIconSize = 72;

    /// <summary>放大强度下限（1.0 = 不放大）。</summary>
    public const double MinMagnificationStrength = 1.0;

    /// <summary>放大强度上限。</summary>
    public const double MaxMagnificationStrength = 2.5;

    /// <summary>
    /// 放大强度的默认/回落值：控制中心的"放大"砖块在<b>当前就是关</b>的状态下被打开时回到它
    /// （见 Controls.QuickSettings.WithMagnification）。写在这里而不是散在 UI 里，
    /// 是为了让"新配置的默认值"和"重新打开放大"永远是同一个数。
    /// </summary>
    public const double DefaultMagnificationStrength = 1.8;

    /// <summary>显示器选择器的关键字取值（序号形另由 <see cref="MaxMonitorIndex"/> 界定）。</summary>
    public static IReadOnlyList<string> KnownMonitorKeywords { get; } = ["auto", "primary"];

    /// <summary>
    /// 显示器序号上限：Core 不知道在屏有几块显示器（那是运行时的事，由 App 侧 MonitorGeometry
    /// 解析并在越界时回落 auto），这里只挡住"手改 JSON 写了个荒谬台数"的情况。
    /// </summary>
    public const int MaxMonitorIndex = 16;

    /// <summary>
    /// 加载侧规范化：数值字段钳制到合法区间（LyricsOffsetMs ±2000、DockIconSize 32..72、
    /// MagnificationStrength 1.0..2.5）；枚举串字段（主题/策略/切换器样式/停靠边/图标处理方式）
    /// 小写去空白后比对白名单，未知值收敛为默认，避免手改配置把 App 侧映射打到兜底分支之外的未定义行为。
    /// 显示器选择器（DockMonitor/TopBarMonitor）走 <see cref="MonitorSelectorOrDefault"/>：
    /// 关键字（auto/primary）之外只接受 1..MaxMonitorIndex 的十进制序号。
    /// 热键：切换器是自由文本（只做 null/空白兜底，解析失败由 App 侧回退 Alt 并留日志）；启动器热键则在这里
    /// 就要求"至少一个修饰键 + 可识别主键"（见 Launcher.LauncherHotkey），因为它的后果是<b>全局吞键</b>——
    /// 一个没有修饰键的组合（如 "space"）会把所有应用的空格吃掉，这种值绝不允许落进配置。
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
        LauncherHotkey = Launcher.LauncherHotkey.Canonicalize(LauncherHotkey),
        DockIconSize = Math.Clamp(DockIconSize, MinDockIconSize, MaxDockIconSize),
        MagnificationStrength = Math.Clamp(MagnificationStrength, MinMagnificationStrength, MaxMagnificationStrength),
        DockMonitor = MonitorSelectorOrDefault(DockMonitor),
        TopBarMonitor = MonitorSelectorOrDefault(TopBarMonitor),
        IconTreatment = Canonical(IconTreatment, "auto", KnownIconTreatments)
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
