namespace Beautiful.Core.Layout;

/// <summary>
/// 覆盖层"要不要给全屏让位"所需的全部事实。<b>取值在 App 侧（Win32），判定在这一层</b>——
/// 于是负坐标副屏、最小化、自身窗口这些分支能在单测里穷举，不必每台机器实测一遍。
/// <para>
/// 【<see cref="Bounds"/> 必须是 DWM 扩展边框矩形，不是 <c>GetWindowRect</c>】
/// 有边框窗口的 <c>GetWindowRect</c> 比实际像素外扩一圈不可见缩放边（1920×1080 上是
/// <c>-8,-8,1928,1088</c>），拿它判定会把"最大化但根本没全屏"的窗口误读成盖住了屏幕；
/// 反过来 <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> 给的正是可见像素。取不到时（无框/工具窗口）
/// 再退回 <c>GetWindowRect</c>。
/// </para>
/// </summary>
/// <param name="Handle">前台窗口句柄；<see cref="IntPtr.Zero"/> 表示读不到。</param>
/// <param name="Bounds">前台窗口的可见矩形（物理 px）。</param>
/// <param name="IsVisible"><c>IsWindowVisible</c>。</param>
/// <param name="IsMinimized"><c>IsIconic</c>：最小化的窗口没盖住任何东西。</param>
/// <param name="IsCloaked"><c>DWMWA_CLOAKED</c> 非 0 = 窗口被 DWM 藏起来了，<b>一个像素都没画</b>，
/// 因此它不可能盖住屏幕。这一条不是理论担忧：本机实测 <c>TextInputHost.exe</c>（输入法/表情面板的
/// 常驻宿主）那棵树里就有一个 <c>Windows.UI.Core.CoreWindow</c>，<c>IsWindowVisible=true</c>、
/// 矩形正好铺满整块 2880×1920、而 <c>DWMWA_CLOAKED=2</c>。少了这一条，它只要抢到一次前台，
/// 装饰层就会凭空消失——而按类名把它拉黑又会连带误伤 UWP 全屏游戏（同一个类名）。</param>
/// <param name="IsOwnProcess">前台窗口属于本进程——不能让位给自己（会让装饰层自己把自己压下去）。</param>
/// <param name="IsShellOrDesktop">桌面/任务栏一类 shell 窗口。桌面壁纸窗口本来就铺满整屏，
/// 不能让位给它，否则"没有别的窗口"这种常态会让装饰层永远消失。</param>
public readonly record struct ForegroundWindow(
    IntPtr Handle,
    PhysicalRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    bool IsOwnProcess,
    bool IsShellOrDesktop)
{
    /// <summary>
    /// 能不能拿它当"让位对象"。任一条不成立就当作"屏幕没被挡住"：覆盖层继续浮着。
    /// 这个方向的偏心里是刻意的——误判成"该让位"的表现是装饰莫名消失（用户以为功能坏了），
    /// 误判成"该浮着"只是全屏游戏时任务栏边多一条线。后者远比前者糟。
    /// </summary>
    public bool IsEligible =>
        Handle != IntPtr.Zero &&
        IsVisible &&
        !IsMinimized &&
        !IsCloaked &&
        !IsOwnProcess &&
        !IsShellOrDesktop &&
        Bounds.Width > 0 &&
        Bounds.Height > 0;
}

/// <summary>
/// 让位裁定结果。
/// </summary>
/// <param name="KeepTopmost">覆盖层是否应保持置顶。</param>
/// <param name="PlaceAfter">让位时把覆盖层插到哪个句柄之后（即紧贴在该窗口之下）；
/// 不需要让位时为 <see cref="IntPtr.Zero"/>。</param>
public readonly record struct OverlayZOrder(bool KeepTopmost, IntPtr PlaceAfter);

/// <summary>
/// 全屏避让：盖住整块显示器的前台窗口（全屏游戏、F11、视频全屏）出现时，装饰性置顶层必须落下。
/// 算法移植自 <c>ANYNC/TaskbarLyrics</c> 的 <c>SmartTopmostPolicy</c>（MIT，出处见
/// THIRD-PARTY-NOTICES.md），只取这段无依赖的纯数学，不带它的 WebView2/EF Core/OpenCC 栈。
/// <para>
/// 【为什么"最大化窗口"不会触发放下——本机实测】2880×1920 屏 + 96px 常驻任务栏下，最大化的
/// Chrome 扩展边框矩形是 <c>0,0,2880,1824</c>（正好等于工作区，够不到 <c>Bottom=1920</c>），
/// 而它的 <c>GetWindowRect</c> 报 <c>-13,-13,2893,1837</c>（外扩一圈不可见缩放边）。
/// 两个数都说明：<b>最大化只铺到工作区边界</b>，任务栏那一条不属于它，于是
/// <see cref="CoversMonitor"/> 恒为假——不需要任何"是不是游戏"的启发式，屏幕覆盖率本身就是答案。
/// </para>
/// <para>
/// 【为什么这里不处理"任务栏自动隐藏时最大化=整屏"】那条推断成立时，任务栏矩形已经不在屏内，
/// 调用方的 <c>TaskbarGlassMath.IsDecoratable</c> 早就先返回假、把装饰层收掉了；两者互斥，
/// 不会同时出现"装饰层在画"与"最大化窗口盖住整屏"。剩下唯一的空隙是自动隐藏栏探出 1px 的
/// 瞬间——那时装饰层本来就只有一条 1px 的影子，让不让位看不出来。
/// </para>
/// <para>
/// 【逐屏判定，不是逐窗口】参数是"覆盖层所在的那块显示器"，副屏全屏不影响主屏装饰层，
/// 主屏全屏也不会把副屏的装饰层拖下去。
/// </para>
/// </summary>
public static class FullscreenAvoidance
{
    /// <summary>
    /// 判一次：该浮着，还是该落到 <paramref name="foreground"/> 之下。
    /// </summary>
    /// <param name="monitor">覆盖层所在显示器的矩形（物理 px，副屏可为负坐标）。</param>
    /// <param name="foreground">前台窗口；读不到传 null。</param>
    /// <returns>让位时带上那个窗口句柄，其余情况 <see cref="OverlayZOrder.KeepTopmost"/> 为真。</returns>
    public static OverlayZOrder Evaluate(PhysicalRect monitor, ForegroundWindow? foreground)
        => foreground is { IsEligible: true } w && CoversMonitor(w.Bounds, monitor)
            ? new OverlayZOrder(false, w.Handle)
            : new OverlayZOrder(true, IntPtr.Zero);

    /// <summary>
    /// 窗口是否整个盖住显示器（含"探出边缘"的情况——全屏窗口常有 1px 的外溢）。
    /// 显示器矩形自身非法（宽高 ≤ 0，取值失败）时一律判"没盖住"：拿不到屏幕边界就不该让位。
    /// </summary>
    /// <param name="window">窗口可见矩形（物理 px）。</param>
    /// <param name="monitor">显示器矩形（物理 px）。</param>
    /// <returns>四条边都把显示器包住则为真。</returns>
    public static bool CoversMonitor(PhysicalRect window, PhysicalRect monitor)
        => monitor.Width > 0 && monitor.Height > 0 &&
           window.Left <= monitor.Left &&
           window.Top <= monitor.Top &&
           window.Right >= monitor.Right &&
           window.Bottom >= monitor.Bottom;

    /// <summary>
    /// 这一轮的裁定要不要真的调 <c>SetWindowPos</c>。
    /// <para>
    /// 【为什么"该浮着"要每轮重复施加、"该让位"却只做一次】置顶是一次性的 Z 序状态，会被别人抢走
    /// （另一个 topmost 应用起来了、任务栏重建了、系统弹了个 topmost 对话框）。不重施就会悄悄掉下去，
    /// 而那正是用户报告"美化层不见了"的形态。反过来，让位状态下反复调 <c>SetWindowPos</c> 会跟全屏窗口
    /// 抢 Z 序、在部分驱动上表现为闪烁——让位是稳定状态，做够一次就停手。
    /// </para>
    /// </summary>
    /// <param name="previous">上一轮实际生效的裁定（还没生效过传 null）。</param>
    /// <param name="current"><see cref="Evaluate"/> 的本轮结果。</param>
    /// <returns>需要调用 <c>SetWindowPos</c> 时为真。</returns>
    public static bool NeedsReassert(OverlayZOrder? previous, OverlayZOrder current)
        => current.KeepTopmost || previous != current;
}
