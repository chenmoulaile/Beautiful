using Beautiful.Core.Settings;
using Beautiful.Core.Themes;

namespace Beautiful.Core.Controls;

/// <summary>
/// 控制中心的一个分段选项（主题 / 系统任务栏策略）。
/// <see cref="Id"/> 恒取 <see cref="AppSettings"/> 里的落盘标识（"macos" / "hide"…），
/// <see cref="Label"/> 是界面上的品牌名/中文措辞——UI 只要把列表铺开、把当前 Id 高亮，
/// 不必自己知道任何白名单细节（白名单只有一个属主，见 AppSettings.KnownThemes）。
/// </summary>
public sealed record QuickOption(string Id, string Label);

/// <summary>
/// 控制中心的一个开关砖块在<b>此刻</b>的完整观感：标签 + 是否打开 + 是否可点 + 不可点的原因。
/// 面板照单渲染，不在 XAML/代码里另写一份"哪些开关在哪些主题下有意义"的判断。
/// </summary>
/// <param name="Id"><see cref="QuickSettings.MagnificationId"/> 等常量，面板据此分派落地动作。</param>
/// <param name="Label">砖块标题（中文）。</param>
/// <param name="IsOn">当前是否打开（三态的倒影已按主题默认折算成实际取值）。</param>
/// <param name="IsEnabled">false = 置灰；<paramref name="BlockReason"/> 必非空。</param>
/// <param name="BlockReason">置灰原因（进 ToolTip）。按裁定：没有系统能力支撑的开关一律置灰并写明原因，
/// 绝不留一个"点了没反应"的砖块。</param>
public sealed record QuickTile(string Id, string Label, bool IsOn, bool IsEnabled, string? BlockReason);

/// <summary>
/// 控制中心（v3 T7）的纯投影层：把"当前设置 + 当前主题"折算成界面应当显示的分段与砖块，
/// 并把"用户点了某一项"折算成<b>新的 AppSettings</b>。
/// <para>
/// 【为什么这些逻辑必须在 Core】控制中心的每一项都要真的改到系统/ Dock：改设置值、改哪个字段、
/// 关掉后放回什么值、三态的倒影怎么折算成布尔——这些是"点了到底发生了什么"的全部内容。
/// 把它们放在这里，单测就能钉住行为，而 WPF 那侧只剩渲染与事件接线（无法在 CI 里跑的部分）。
/// </para>
/// <para>
/// 【为什么所有 With* 都返回新记录而不落盘】AppSettings 是不可变记录，落盘与落地由调用方
/// （App 的 Set* 入口，与设置窗口同一条路径）决定，这样同一个纯函数既能被面板用、也能被 App 用，
/// 不会出现"面板写了一套规则、设置窗口写了另一套"。
/// </para>
/// </summary>
public static class QuickSettings
{
    // ---------- 砖块标识（面板与单测共用同一串，避免两边各写一份 switch） ----------
    public const string MagnificationId = "magnification";
    public const string ReflectionId = "reflection";
    public const string MediaCapsuleId = "mediaCapsule";
    public const string WallpaperAccentId = "wallpaperAccent";

    /// <summary>任务栏玻璃壳砖块（v3.2 #6）：只美化原生任务栏，不接管。</summary>
    public const string TaskbarGlassId = "taskbarGlass";

    /// <summary>主题分段（左→右）。鸿蒙放最后一位，与设置页的先后顺序一致；
    /// Windows 11 排第一——它是"不接管、只增强原生"的那条线，与其他三个"换一副面孔"不同类。</summary>
    public static IReadOnlyList<QuickOption> Themes { get; } =
    [
        new("windows", "Windows 11"),
        new("macos", "macOS"),
        new("linux", "Linux"),
        new("harmonyos", "鸿蒙")
    ];

    /// <summary>系统任务栏策略分段。中文措辞沿用设置页（隐藏/自动隐藏/保留），同一件事不出现两个名字。</summary>
    public static IReadOnlyList<QuickOption> TaskbarPolicies { get; } =
    [
        new("hide", "隐藏"),
        new("autohide", "自动隐藏"),
        new("keep", "保留")
    ];

    // ---------- 当前选择 ----------

    /// <summary>当前主题 Id。<b>手改过的配置</b>可能带着白名单外的值（未经 Normalized 的对象），
    /// 这时回落分段第一项而不是高亮一个不存在的项——界面永远有且只有一个选中。</summary>
    public static string SelectedThemeId(AppSettings s) => FirstKnown(s.Theme, Themes);

    /// <summary>当前任务栏策略 Id，取舍同上。</summary>
    public static string SelectedTaskbarPolicyId(AppSettings s) => FirstKnown(s.TaskbarPolicy, TaskbarPolicies);

    // ---------- 分段写入 ----------

    /// <summary>
    /// 切主题。<paramref name="id"/> 不在 <see cref="Themes"/> 内时<b>原样返回</b>：
    /// 控制中心不是设置的合法入口之外的通道，绝不因为一次误触/一个过期砖块 Id 就把脏值写进配置。
    /// </summary>
    public static AppSettings WithTheme(AppSettings s, string id) =>
        Themes.Any(o => o.Id == id) ? s with { Theme = id } : s;

    /// <summary>切任务栏策略，未知标识原样返回（理由同 <see cref="WithTheme"/>）。</summary>
    public static AppSettings WithTaskbarPolicy(AppSettings s, string id) =>
        TaskbarPolicies.Any(o => o.Id == id) ? s with { TaskbarPolicy = id } : s;

    // ---------- 开关：取值判定 ----------

    /// <summary>
    /// 放大是否打开。<b>用强度而不是布尔</b>：Dock 的放大强度本来就有一个"不放大"的合法下限
    /// （<see cref="AppSettings.MinMagnificationStrength"/> = 1.0），把它当关、把大于它的值当开，
    /// 控制中心的开关与设置页的滑杆就是同一个状态的两种画法，不会出现"开关开着、滑杆却在 ×1.0"。
    /// </summary>
    public static bool IsMagnificationOn(AppSettings s) => s.MagnificationStrength > AppSettings.MinMagnificationStrength;

    /// <summary>倒影是否打开：三态的 <see cref="AppSettings.ReflectionEnabled"/>（null = 跟随主题）先折算。</summary>
    public static bool IsReflectionOn(AppSettings s, ThemeDefinition theme) => s.ReflectionEnabledFor(theme.ReflectionDefault);

    /// <summary>媒体胶囊是否打开。</summary>
    public static bool IsMediaCapsuleOn(AppSettings s) => s.ShowMediaCapsule;

    /// <summary>壁纸取色是否打开（关时主题观感与取色功能上线前完全一致）。</summary>
    public static bool IsWallpaperAccentOn(AppSettings s) => s.UseWallpaperAccent;

    /// <summary>玻璃壳开关的现值。</summary>
    public static bool IsTaskbarGlassOn(AppSettings s) => s.TaskbarGlass;

    // ---------- 开关：写入 ----------

    /// <summary>
    /// 设放大。<b>开 = 落到默认倍率（若当前已在放大状态则保留现值），关 = 落到下限 ×1.0。</b>
    /// 关掉后再打开不会记住"上次是 ×2.4"——设置里放大强度只有这一个数，把 2.4 写成 1.0 那一刻
    /// 信息就没了；要记住就得再造一个字段（"上次强度"）并管它的钳制/迁移/与设置页滑杆的一致性，
    /// 为一个开关加一列状态不值。用户可以再拖一次滑杆，控制中心只保证"开起来一定看得见放大"。
    /// </summary>
    public static AppSettings WithMagnification(AppSettings s, bool on) => on
        ? s with { MagnificationStrength = IsMagnificationOn(s) ? s.MagnificationStrength : AppSettings.DefaultMagnificationStrength }
        : s with { MagnificationStrength = AppSettings.MinMagnificationStrength };

    /// <summary>
    /// 设倒影：一次点击就把三态<b>落成显式值</b>（true/false）。
    /// 这是既有语义（设置页勾一次同样写显式值，此后切主题不再把它冲掉），控制中心不另发明规则。
    /// </summary>
    public static AppSettings WithReflection(AppSettings s, bool on) => s with { ReflectionEnabled = on };

    /// <summary>设媒体胶囊显隐。</summary>
    public static AppSettings WithMediaCapsule(AppSettings s, bool on) => s with { ShowMediaCapsule = on };

    /// <summary>设壁纸取色强调色的开关（落盘后由 App 侧 WallpaperAccentService 立即重放一次）。</summary>
    public static AppSettings WithWallpaperAccent(AppSettings s, bool on) => s with { UseWallpaperAccent = on };

    /// <summary>写玻璃壳开关。</summary>
    public static AppSettings WithTaskbarGlass(AppSettings s, bool on) => s with { TaskbarGlass = on };

    // ---------- 砖块投影 ----------

    /// <summary>
    /// 把当前设置投影成界面上的四个开关砖块（数组顺序 = 界面顺序）。
    /// <para>
    /// 【放大为什么要置灰】放大是<b>主题能力</b>而不是用户偏好：<c>ThemeDefinition.MaxScale</c> 为 1
    /// 时（Linux/GNOME dash、鸿蒙）渲染器压根不做放大，勾上去不会有任何可见变化。按本项目的一贯裁定
    /// （没有能力支撑的控件必须置灰并写明原因，见 TopBarMenus 的置灰条目），这里给原因而不是留死开关。
    /// 原因文案不重复主题名——面板标题行已经写着"当前主题：xxx"，砖块只有 90 DIP 宽，
    /// 写全名会被截成省略号，反而看不出为什么灰着。
    /// </para>
    /// <para>
    /// 【倒影为什么<em>不</em>按主题置灰】倒影是渲染器按旗标增删镜像元素（DockSurface.SyncReflections），
    /// 三个主题都真画。设置页把它限制在 macOS 是"观感建议"，控制中心按能力口径放行——
    /// 关掉它确实会变、打开它确实会多一截镜像，不是死控件。
    /// </para>
    /// <para>
    /// 【壁纸取色按"读不读得到壁纸"置灰】<paramref name="wallpaperAccentAvailable"/> 由 App 侧探测
    /// （幻灯片 / 纯色桌面 / 权限受限都会让它为 false）。取不到色的开关必须灰并写明原因，
    /// 否则点上去只会得到一个"我开了但画面没变"的强调色——这正是本项目定级的死控件。
    /// </para>
    /// </summary>
    /// <param name="s">当前设置。</param>
    /// <param name="theme">当前主题定义（放大能力与倒影默认值都来自它）。</param>
    /// <param name="wallpaperAccentAvailable">当前能不能读到壁纸文件（见上）。</param>
    public static IReadOnlyList<QuickTile> ToggleTiles(AppSettings s, ThemeDefinition theme, bool wallpaperAccentAvailable) =>
    [
        new(MagnificationId, "放大", IsMagnificationOn(s),
            theme.ShowMagnification,
            theme.ShowMagnification ? null : "该主题不做悬停放大"),
        new(ReflectionId, "倒影", IsReflectionOn(s, theme), true, null),
        new(MediaCapsuleId, "媒体胶囊", IsMediaCapsuleOn(s), true, null),
        new(WallpaperAccentId, "壁纸取色", IsWallpaperAccentOn(s), wallpaperAccentAvailable,
            wallpaperAccentAvailable ? null : "读不到当前壁纸文件（幻灯片或纯色桌面）"),
        // 玻璃壳是"给原生任务栏化妆"，任务栏被隐藏时没有可化妆的对象——置灰并写明原因，
        // 而不是让砖块亮着却屏上什么也没变（那是"假状态"，本项目不接受）
        new(TaskbarGlassId, "任务栏玻璃壳", IsTaskbarGlassOn(s), NativeTaskbarVisible(s),
            NativeTaskbarVisible(s) ? null : "任务栏策略为隐藏，没有可装饰的任务栏"),
    ];

    /// <summary>原生任务栏当前是否可见（玻璃壳的前提）。autohide 算可见——它只是缩起来，仍然在那里。</summary>
    public static bool NativeTaskbarVisible(AppSettings s) =>
        !string.Equals(s.TaskbarPolicy, "hide", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>按 Id 找砖块投影（点击后刷新单项状态用）；未知 Id 返回 null。</summary>
    public static QuickTile? FindTile(IReadOnlyList<QuickTile> tiles, string id) =>
        tiles.FirstOrDefault(t => t.Id == id);

    // ---------- 文本 ----------

    /// <summary>策略的中文说明（砖块下方一行小字，与设置页 <c>UpdateTaskbarHint</c> 同一份措辞）。</summary>
    public static string PolicyHint(string policyId) => policyId switch
    {
        "autohide" => "自动隐藏：光标移到屏幕底边即可唤出原生任务栏。",
        "keep" => "保留：不改动原生任务栏，Dock 悬停在其上方。",
        _ => "隐藏：原生任务栏被移出屏幕，Dock 与屏幕真底边无缝衔接。"
    };

    /// <summary>把任意配置值收敛到分段里的第一项（白名单外/空值都不许让分段"一个都不选"）。</summary>
    private static string FirstKnown(string? value, IReadOnlyList<QuickOption> options) =>
        options.Any(o => o.Id == value) ? value! : options[0].Id;
}
