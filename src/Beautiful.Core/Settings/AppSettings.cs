using System.Globalization;

namespace Beautiful.Core.Settings;

/// <summary>
/// 应用设置（config/settings.json）。固定应用不在此处（控制器裁定：PinnedApps 只存在于
/// config/dock.json，由 App 侧 PinnedAppsStore 管理）。首次 Save 才落盘，文件缺失时一律代码默认。
/// </summary>
public sealed record AppSettings
{
    /// <summary>主题标识：macOS-Only（默认 "macos"）。Normalization 收敛为 KnownThemes。</summary>
    public string Theme { get; init; } = "macos";

    /// <summary>外观模式："dark"（默认，Big Sur 暗色形态）| "light" | "auto"（跟随系统 AppsUseLightTheme）。</summary>
    public string Appearance { get; init; } = Design.AppearanceModes.Dark;

    /// <summary>macOS 强调色板 id（blue/purple/…，白名单见 <see cref="Design.MacAccents"/>），未知值收敛为 blue。</summary>
    public string AccentColor { get; init; } = Design.MacAccents.DefaultId;

    /// <summary>任务栏策略："hide" | "autohide" | "keep"。</summary>
    public string TaskbarPolicy { get; init; } = "hide";

    /// <summary>开机自启（HKCU Run "Beautiful"）。</summary>
    public bool Autostart { get; init; }

    /// <summary>Dock 是否显示媒体胶囊条目。</summary>
    public bool ShowMediaCapsule { get; init; } = true;

    /// <summary>
    /// Dock 行尾的时钟/天气小组件（Phase 7-T3）。默认开；关掉后整块从行尾消失，
    /// 不占宽度也不联网取天气。
    /// </summary>
    public bool DockWidgetEnabled { get; init; } = true;

    /// <summary>
    /// Dock 自动隐藏（P3-T5 的隐藏模式之一）。开启后光标不在 Dock 上超过 500ms 就滑到屏幕下沿
    /// 只留 4px，光标触底立即滑回。
    /// <para>
    /// 【已知差异】仍保留 AppBar 预留带（工作区不被最大化窗口占满），与本机其他 AppBar 共存最稳；
    /// 真正"隐藏时把空间还给桌面"要改 AppBar 注册时机，属于后续项。
    /// </para>
    /// </summary>
    public bool DockAutoHide { get; init; }

    /// <summary>
    /// Dock 停靠边（实验功能）：<c>bottom</c>（默认）/ <c>left</c> / <c>right</c>。
    /// <para>
    /// 左右停靠走一块独立的竖排面板，**不参与** DockSurface 的放大场、倒影与拖放排序——
    /// 那套几何是按水平行写的（1500 行），改成垂直轴属于重写；实验版先把"能停到左右边"给出来，
    /// 代价是没有放大效果，且不向 shell 申请工作区预留（窗口浮在桌面上）。
    /// </para>
    /// </summary>
    public string DockEdge { get; init; } = "bottom";

    /// <summary>
    /// 新窗口淡入（P5-T3，实验，默认关）。走 <c>AnimateWindow(AW_BLEND)</c>；
    /// 跨进程不保证生效（由目标窗口的合成路径决定），失败静默，不会影响窗口本身。
    /// </summary>
    public bool WindowOpenAnimation { get; init; }

    /// <summary>媒体面板失焦是否自动收起。</summary>
    public bool HidePanelOnDeactivate { get; init; } = true;

    /// <summary>是否显示 macOS 风格顶栏（Finder 栏）。</summary>
    public bool TopBarEnabled { get; init; } = true;

    /// <summary>
    /// 音量键是否弹 macOS 音量 HUD（Phase 4-T7）。默认开——开启时我们会吞掉媒体键自己写音量，
    /// 所以系统音量浮层也不会再出现；关掉后媒体键完全回到系统行为（含系统浮层）。
    /// </summary>
    public bool VolumeOsdEnabled { get; init; } = true;

    /// <summary>新通知是否在右上角弹横幅（Phase 4-T5 续）。历史始终进通知中心，与这一项无关。</summary>
    public bool NotificationBannerEnabled { get; init; } = true;

    /// <summary>
    /// 顶栏时钟左键是否打开通知中心（Phase 4-T5）。默认开——Big Sur 的时钟点击就是通知中心；
    /// 关掉则退回原来的月历浮层（给只想要日历、不要读系统通知的用户一条干净的退出路）。
    /// </summary>
    public bool NotificationCenterEnabled { get; init; } = true;

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

    /// <summary>Dock 基准图标尺寸（32..96，加载时钳制）。</summary>
    public int DockIconSize { get; init; } = 56;

    /// <summary>Dock 面板距屏幕底边距（0..24 DIP，默认 4；macOS 是悬浮感而非贴死底边）。</summary>
    public int DockEdgeMargin { get; init; } = 4;

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

    /// <summary>最小化效果："genie"（默认）| "zoom"（缩放淡出）| "suck"（更紧的吸吮）。</summary>
    public string MinimizeEffect { get; init; } = "genie";

    /// <summary>动画速度倍率（0.5..2.0，加载时钳制；越小越慢）。</summary>
    public double AnimationSpeed { get; init; } = 1.0;

    /// <summary>不做最小化动画的进程名（逗号/分号分隔，忽略 .exe 后缀与大小写）。</summary>
    public string MinimizeExcludes { get; init; } = "";

    public static IReadOnlyList<string> KnownMinimizeEffects { get; } = ["genie", "zoom", "suck"];

    /// <summary>
    /// 真全屏应用（游戏/F11/视频）盖住整块屏时，隐藏菜单栏与 Dock；退出全屏后恢复。
    /// 判定全部走 Core <see cref="Layout.FullscreenAvoidance"/>（物理矩形比较，最大化不触发）。
    /// </summary>
    public bool FullscreenAvoidanceEnabled { get; init; } = true;

    // ---------- 触发角（v4 Phase 5-T2；默认全部禁用，避免误触） ----------
    public string HotCornerTopLeft { get; init; } = "disabled";
    public string HotCornerTopRight { get; init; } = "disabled";
    public string HotCornerBottomLeft { get; init; } = "disabled";
    public string HotCornerBottomRight { get; init; } = "disabled";

    public static IReadOnlyList<string> KnownHotCornerActions { get; } = ["disabled", "showdesktop", "lock", "sleep"];

    /// <summary>
    /// 顶栏是否显示"托盘图标区"入口（v3.2）：点开可看到通知区域里被收起的第三方应用图标，
    /// 点击/右键会转发回图标自己的窗口。关掉只是少一颗按钮，不影响系统托盘本身。
    /// </summary>
    public bool ShowTrayInTopBar { get; init; } = true;

    /// <summary>
    /// genie 的渲染引擎：<c>mesh</c>（默认）= 一张纹理三角网，顶点走 macOS 灯罩曲线，
    /// 有真正的"脖子"；<c>strips</c> = 水平条带整体缩放，形状差一档但在任何渲染环境下都画得出来。
    /// <para>
    /// 留这一条不是怀旧：mesh 走的是 WPF 3D 贴在分层窗口上，这条组合在缺少硬件加速的机器上
    /// 只能实机看到结果，而"动画变成一块黑卡"比"动画朴素一点"糟得多。用户能一行配置退回去，
    /// 比等一个版本修复好。
    /// </para>
    /// </summary>
    public string GenieEngine { get; init; } = "mesh";

    /// <summary>合法 genie 引擎标识（小写）。</summary>
    public static IReadOnlyList<string> KnownGenieEngines { get; } = ["mesh", "strips"];

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

    /// <summary>合法主题标识（小写）。v4 只保留 macOS。</summary>
    public static IReadOnlyList<string> KnownThemes { get; } = ["macos"];

    /// <summary>合法任务栏策略标识（小写）。</summary>
    public static IReadOnlyList<string> KnownTaskbarPolicies { get; } = ["hide", "autohide", "keep"];

    /// <summary>合法切换器样式标识（小写，与主题标识同名，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownSwitcherStyles { get; } = ["macos"];

    /// <summary>合法 Dock 停靠边标识（小写，供设置面板下拉复用）。</summary>
    public static IReadOnlyList<string> KnownDockPositions { get; } = ["bottom", "top", "left", "right"];

    /// <summary>
    /// 合法图标处理方式标识（小写，v3 T6；白名单与解释器同源，避免设置面板和 Core 各写一份 switch）。
    /// </summary>
    public static IReadOnlyList<string> KnownIconTreatments { get; } = Imaging.IconTreatment.Known;

    // ---------- Native 层开关（v4 Phase 2） ----------

    /// <summary>底层增强模块总开关；false = 等价 BEAUTIFUL_NONATIVE=1（纯 C# 形态）。</summary>
    public bool NativeModules { get; init; } = true;

    /// <summary>材质引擎（DComp HostBackdrop 实时模糊）。</summary>
    public bool NativeBlur { get; init; } = true;

    /// <summary>捕获服务（WGC/DXGI 优先，GDI 兜底）。</summary>
    public bool NativeCapture { get; init; } = true;

    /// <summary>场景渲染器（genie/打开动画的 D3D11 地基）。</summary>
    public bool NativeScene { get; init; } = true;

    /// <summary>
    /// TrayBridge 注入开关。<b>默认 false</b>：Phase 2 只做"注入→心跳→干净卸载"演练，
    /// 所有者真机回执通过前不默认开启（注入 explorer 的代价是桌面重启，见总纲 §4.4）。
    /// </summary>
    public bool NativeTrayBridge { get; init; }

    /// <summary>材质质量："auto"（按探针结果）/ "live"（强制 native）/ "static"（强制静态 tint）。</summary>
    public string MaterialQuality { get; init; } = "auto";

    public static IReadOnlyList<string> KnownMaterialQualities { get; } = ["auto", "live", "static"];

    /// <summary>Dock 图标尺寸下限（供设置面板滑杆复用）。</summary>
    public const int MinDockIconSize = 32;

    /// <summary>Dock 图标尺寸上限。</summary>
    public const int MaxDockIconSize = 96;

    /// <summary>Dock 距底边距下限/上限（DIP）。</summary>
    public const int MinDockEdgeMargin = 0;
    public const int MaxDockEdgeMargin = 24;

    /// <summary>放大强度下限（1.0 = 不放大）。</summary>
    public const double MinMagnificationStrength = 1.0;

    /// <summary>放大强度上限。</summary>
    public const double MaxMagnificationStrength = 2.5;

    /// <summary>
    /// 放大强度的默认/回落值：控制中心的"放大"砖块在<b>当前就是关</b>的状态下被打开时回到它
    /// （见 Controls.QuickSettings.WithMagnification）。写在这里而不是散在 UI 里，
    /// 是为了让"新配置的默认值"和"重新打开放大"永远是同一个数。
    /// </summary>
    public const double DefaultMagnificationStrength = 1.7;

    /// <summary>显示器选择器的关键字取值（序号形另由 <see cref="MaxMonitorIndex"/> 界定）。</summary>
    public static IReadOnlyList<string> KnownMonitorKeywords { get; } = ["auto", "primary"];

    /// <summary>
    /// 显示器序号上限：Core 不知道在屏有几块显示器（那是运行时的事，由 App 侧 MonitorGeometry
    /// 解析并在越界时回落 auto），这里只挡住"手改 JSON 写了个荒谬台数"的情况。
    /// </summary>
    public const int MaxMonitorIndex = 16;

    /// <summary>
    /// 加载侧规范化：数值字段钳制到合法区间（DockIconSize 32..96、
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
        Theme = Canonical(Theme, "macos", KnownThemes),
        Appearance = Design.AppearanceModes.Canonical(Appearance),
        AccentColor = Design.MacAccents.Canonical(AccentColor),
        TaskbarPolicy = Canonical(TaskbarPolicy, "hide", KnownTaskbarPolicies),
        SwitcherStyle = Canonical(SwitcherStyle, "macos", KnownSwitcherStyles),
        DockPosition = Canonical(DockPosition, "bottom", KnownDockPositions),
        SwitcherHotkey = HotkeyOrDefault(SwitcherHotkey, "alt+tab"),
        LauncherHotkey = Launcher.LauncherHotkey.Canonicalize(LauncherHotkey),
        DockIconSize = Math.Clamp(DockIconSize, MinDockIconSize, MaxDockIconSize),
        DockEdgeMargin = Math.Clamp(DockEdgeMargin, MinDockEdgeMargin, MaxDockEdgeMargin),
        MagnificationStrength = Math.Clamp(MagnificationStrength, MinMagnificationStrength, MaxMagnificationStrength),
        DockMonitor = MonitorSelectorOrDefault(DockMonitor),
        TopBarMonitor = MonitorSelectorOrDefault(TopBarMonitor),
        IconTreatment = Canonical(IconTreatment, "auto", KnownIconTreatments),
        GenieEngine = Canonical(GenieEngine, "mesh", KnownGenieEngines),
        MinimizeEffect = Canonical(MinimizeEffect, "genie", KnownMinimizeEffects),
        AnimationSpeed = Math.Clamp(AnimationSpeed, 0.5, 2.0),
        MinimizeExcludes = (MinimizeExcludes ?? string.Empty).Trim(),
        HotCornerTopLeft = Canonical(HotCornerTopLeft, "disabled", KnownHotCornerActions),
        HotCornerTopRight = Canonical(HotCornerTopRight, "disabled", KnownHotCornerActions),
        HotCornerBottomLeft = Canonical(HotCornerBottomLeft, "disabled", KnownHotCornerActions),
        HotCornerBottomRight = Canonical(HotCornerBottomRight, "disabled", KnownHotCornerActions),
        MaterialQuality = Canonical(MaterialQuality, "auto", KnownMaterialQualities)
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
