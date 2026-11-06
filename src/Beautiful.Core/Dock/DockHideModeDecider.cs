using Beautiful.Core.Layout;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 隐藏模式（对标 MyDockFinder 的隐藏方式集，共 9 档）。枚举只是内存里的判定形态，
/// 持久化载体是 <c>AppSettings.DockHideMode</c> 的字符串（<see cref="DockHideModes.Known"/> 白名单）。
/// </summary>
public enum DockHideModeKind
{
    /// <summary>总是显示（历史默认 off）。</summary>
    Always,

    /// <summary>自动隐藏：光标离开满可调延迟滑下、触边唤回（off 家族之外的原始档，auto）。
    /// 延迟与唤出方式（touch/dwell/double）由 <c>AppSettings.DockAutoHideDelayMs</c>/
    /// <c>DockAutoHideReveal</c> 承载——"悬停触发延迟（延迟可调）"落在这一档。</summary>
    AutoHide,

    /// <summary>智能隐藏：有窗口盖住 Dock 才让位，移开即回（smart）。</summary>
    Smart,

    /// <summary>前台为真全屏应用时隐藏（fullscreen）。</summary>
    Fullscreen,

    /// <summary>前台窗口最大化（且在 Dock 所在屏）时隐藏（maximized）。</summary>
    Maximized,

    /// <summary>双击边缘切换：平时只留缝，双击 Dock 残留的边缘条带切换显示/隐藏（edge）。</summary>
    EdgeToggle,

    /// <summary>按住修饰键临时显示：平时藏住，按住 <c>DockModifierKey</c> 时临时滑出（modifier）。</summary>
    ModifierHold,

    /// <summary>仅桌面时显示：前台是桌面/外壳（或没有合格前台）才显示（desktop）。</summary>
    DesktopOnly,

    /// <summary>半透明：光标不在 Dock 上时整条降到 45%（dim，历史档保留）。</summary>
    Dim
}

/// <summary>
/// 隐藏模式标识的唯一来源：白名单、常量与串→枚举映射。设置规范化（AppSettings.Normalized）、
/// 设置窗口下拉与 DockWindow 的模式切换都从这里取值，不再各写一份 switch。
/// </summary>
public static class DockHideModes
{
    public const string Off = "off";
    public const string Auto = "auto";
    public const string Smart = "smart";
    public const string Fullscreen = "fullscreen";
    public const string Maximized = "maximized";
    public const string Edge = "edge";
    public const string Modifier = "modifier";
    public const string Desktop = "desktop";
    public const string Dim = "dim";

    /// <summary>合法隐藏模式标识（小写）。顺序即设置窗口下拉的呈现顺序。</summary>
    public static IReadOnlyList<string> Known { get; } =
        [Off, Auto, Smart, Fullscreen, Maximized, Edge, Modifier, Desktop, Dim];

    /// <summary>模式串 → 枚举（null/空白/未知兜 Always，= 历史默认行为）。</summary>
    public static DockHideModeKind KindOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        Auto => DockHideModeKind.AutoHide,
        Smart => DockHideModeKind.Smart,
        Fullscreen => DockHideModeKind.Fullscreen,
        Maximized => DockHideModeKind.Maximized,
        Edge => DockHideModeKind.EdgeToggle,
        Modifier => DockHideModeKind.ModifierHold,
        Desktop => DockHideModeKind.DesktopOnly,
        Dim => DockHideModeKind.Dim,
        _ => DockHideModeKind.Always
    };

    /// <summary>
    /// 模式串 → 规范化串（白名单内的小写标识；null/空白/未知兜 <see cref="Off"/>）。
    /// App 侧 ApplyHideMode 落盘与日志用它，不再各写一份 switch。
    /// </summary>
    public static string CanonicalOf(string? raw) => KindOf(raw) switch
    {
        DockHideModeKind.AutoHide => Auto,
        DockHideModeKind.Smart => Smart,
        DockHideModeKind.Fullscreen => Fullscreen,
        DockHideModeKind.Maximized => Maximized,
        DockHideModeKind.EdgeToggle => Edge,
        DockHideModeKind.ModifierHold => Modifier,
        DockHideModeKind.DesktopOnly => Desktop,
        DockHideModeKind.Dim => Dim,
        _ => Off
    };
}

/// <summary>一轮判定的落地动作。Dimmed 仅半透明档产生（整条 45%，不挪窗口矩形）。</summary>
public enum DockHideDecision
{
    /// <summary>常显/当前应可见。</summary>
    Visible,

    /// <summary>半透明（窗口不动，只降不透明度）。</summary>
    Dimmed,

    /// <summary>滑下去只留 <c>AutoHideSliverPixels</c> 的缝（与自动隐藏同一几何）。</summary>
    Hidden
}

/// <summary>
/// 一轮隐藏判定所需的全部事实。<b>取值在 App 侧（Win32），判定在本层</b>——与
/// <see cref="Layout.ForegroundWindow"/>（全屏避让）同一条分工纪律，于是每个模式的每个分支
/// 都能在单测里穷举，不必每台机器实测。
/// </summary>
/// <param name="Mode">当前隐藏模式。</param>
/// <param name="DockBand">Dock 可见面板带（物理 px；窗口矩形减去顶部放大余量）。智能隐藏用它判"窗口盖住了 Dock"。</param>
/// <param name="MonitorRect">Dock 所在显示器的 <c>rcMonitor</c>（物理 px）。全屏档用它判"前台盖住整块屏"。</param>
/// <param name="Foreground">前台窗口事实（App 侧 <c>FullscreenProbe</c> 同一来源）；读不到传 null。</param>
/// <param name="ForegroundMaximizedOnDockMonitor">前台窗口处于最大化（IsZoomed）且就在 Dock 所在这块屏上。</param>
/// <param name="ForegroundIsDesktopOrNone">前台是桌面/外壳窗口，或没有合格前台（仅桌面档的判据）。</param>
/// <param name="ModifierHeld">临时显示修饰键（<c>DockModifierKey</c>）此刻按住。</param>
/// <param name="EdgeToggleVisible">双击边缘档的切换状态（App 侧保存，双击一次翻转一次）。</param>
/// <param name="CursorInsideDock">光标在 Dock 窗口矩形内。</param>
/// <param name="CursorAtEdge">光标在触底命中区（与自动隐藏唤出同一口径，留 2px 容差）。</param>
public readonly record struct DockHideFacts(
    DockHideModeKind Mode,
    PhysicalRect DockBand,
    PhysicalRect MonitorRect,
    ForegroundWindow? Foreground,
    bool ForegroundMaximizedOnDockMonitor,
    bool ForegroundIsDesktopOrNone,
    bool ModifierHeld,
    bool EdgeToggleVisible,
    bool CursorInsideDock,
    bool CursorAtEdge)
{
    /// <summary>给定模式构造一份"什么都没发生"的事实（单测起步值）。</summary>
    public static DockHideFacts ForMode(DockHideModeKind mode) => new(
        mode, default, default, null,
        ForegroundMaximizedOnDockMonitor: false,
        ForegroundIsDesktopOrNone: true,
        ModifierHeld: false,
        EdgeToggleVisible: false,
        CursorInsideDock: false,
        CursorAtEdge: false);
}

/// <summary>
/// Dock 隐藏模式的**唯一判定入口**（纯函数，不读时钟、不碰 Win32）。
/// <para>
/// 【与自动隐藏状态机的分工】<see cref="DockHideModeKind.AutoHide"/> 不经由本判定器——
/// "离开满延迟才滑下、滑下后按 touch/dwell/double 唤回"是有记忆的状态机，住在
/// <c>Layout.AutoHidePlan</c>（Core，已有单测）。App 侧 tick 先分流：auto 走状态机，
/// 其余模式走 <see cref="Evaluate"/>。Evaluate 对 auto 返回 Visible 只是兜底（调用方不该问）。
/// </para>
/// <para>
/// 【让位档的唤出口径】智能/全屏/最大化/仅桌面四档"该藏"时，光标在 Dock 上或贴着触底
/// 命中区仍然可见——否则一旦藏了就再也没机会碰它（触边唤回与自动隐藏同一条手感）。
/// 双击边缘档只认双击（状态在调用方），修饰键档只认修饰键——这两档若也放行触边，
/// "隐藏"就名存实亡。
/// </para>
/// </summary>
public static class DockHideModeDecider
{
    /// <summary>判一轮：该可见、该半透明、还是该滑下去留缝。</summary>
    public static DockHideDecision Evaluate(in DockHideFacts f) => f.Mode switch
    {
        DockHideModeKind.Always => DockHideDecision.Visible,

        // auto 由 AutoHideDelayState/AutoHideRevealState 状态机裁定（见类型注释），这里只兜底。
        DockHideModeKind.AutoHide => DockHideDecision.Visible,

        DockHideModeKind.Dim => f.CursorInsideDock ? DockHideDecision.Visible : DockHideDecision.Dimmed,

        // 双击边缘档：状态完全由调用方的双击翻转驱动，光标位置不参与（触边放行会让它退化成 auto）。
        DockHideModeKind.EdgeToggle => f.EdgeToggleVisible ? DockHideDecision.Visible : DockHideDecision.Hidden,

        // 修饰键档：按住即显示；显示期间光标还在 Dock 上就保持（松键后光标离开才藏），否则藏。
        DockHideModeKind.ModifierHold => f.ModifierHeld || f.CursorInsideDock
            ? DockHideDecision.Visible
            : DockHideDecision.Hidden,

        // 让位四档：条件成立才藏，且光标在 Dock 上/贴边时保持可见（唤出口径见类型注释）。
        DockHideModeKind.Smart => RevealOrHide(f, WindowOverlapsDock(f)),
        DockHideModeKind.Fullscreen => RevealOrHide(f, ForegroundCoversMonitor(f)),
        DockHideModeKind.Maximized => RevealOrHide(f, f.ForegroundMaximizedOnDockMonitor),
        DockHideModeKind.DesktopOnly => RevealOrHide(f, !f.ForegroundIsDesktopOrNone),

        _ => DockHideDecision.Visible
    };

    /// <summary>让位四档共用：不该藏 → 可见；该藏 → 光标在 Dock 上或贴边仍可见，否则滑下去留缝。</summary>
    private static DockHideDecision RevealOrHide(in DockHideFacts f, bool shouldHide)
        => !shouldHide || f.CursorInsideDock || f.CursorAtEdge
            ? DockHideDecision.Visible
            : DockHideDecision.Hidden;

    /// <summary>
    /// 智能隐藏的遮挡判据：前台窗口（最顶上、用户正对着的那个）是合格窗口且与 Dock 可见面板带相交。
    /// 以**前台**为遮挡样本是刻意的近似——被 Dock 挡住的诉求来自"它在用面前这扇窗时 Dock 多余"，
    /// 非前台的背景窗口重叠交给触边唤回兜底；要枚举全部顶层窗口的每 80ms 扫描不属于这一档。
    /// </summary>
    public static bool WindowOverlapsDock(in DockHideFacts f)
        => f.Foreground is { IsEligible: true } w && RectsOverlap(w.Bounds, f.DockBand);

    /// <summary>全屏档：前台窗口把 Dock 所在屏整个盖住（复用全屏避让的同一条几何，最大化不会误触发）。</summary>
    public static bool ForegroundCoversMonitor(in DockHideFacts f)
        => f.Foreground is { IsEligible: true } w && FullscreenAvoidance.CoversMonitor(w.Bounds, f.MonitorRect);

    /// <summary>
    /// 两矩形是否相交（边界相触不算：恰好共线时"盖住 Dock"不成立）。任一矩形宽或高 ≤ 0 恒为否。
    /// </summary>
    public static bool RectsOverlap(in PhysicalRect a, in PhysicalRect b)
        => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 &&
           a.Left < b.Right && b.Left < a.Right &&
           a.Top < b.Bottom && b.Top < a.Bottom;
}
