namespace Beautiful.Core.Layout;

/// <summary>
/// 条带停靠边。**取值与 Win32 <c>ABE_TOP</c>/<c>ABE_BOTTOM</c> 完全一致**（1/3），
/// App 侧可直接把 <c>APPBARDATA.uEdge</c> 强转成本枚举，不需要再写一张映射表——
/// 少一层映射就少一处会写反的地方。<c>ABE_LEFT/ABE_RIGHT</c> 未支持（Dock 换边是 T7 的能力）。
/// </summary>
public enum StripEdge
{
    /// <summary>顶边（ABE_TOP=1）：预留带贴显示器**上**沿，向下生长。</summary>
    Top = 1,

    /// <summary>底边（ABE_BOTTOM=3）：预留带贴显示器**下**沿，向上生长。</summary>
    Bottom = 3
}

/// <summary>
/// 物理像素矩形，字段语义与 Win32 <c>RECT</c> 一致（Left/Top 含、Right/Bottom 不含，
/// 宽高 = <c>Right-Left</c>/<c>Bottom-Top</c>）。Core 不引 Win32 也不引 WPF：
/// <c>System.Windows.Rect</c> 是 DIP 且带 double，用它会把"缩放发生在哪一层"重新变成开放式问题。
/// </summary>
/// <param name="Left">左缘物理 px。</param>
/// <param name="Top">上缘物理 px。</param>
/// <param name="Right">右缘物理 px（不含）。</param>
/// <param name="Bottom">下缘物理 px（不含）。</param>
public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>宽（物理 px）。</summary>
    public int Width => Right - Left;

    /// <summary>高（物理 px）。</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// 条带几何的**唯一算术来源**：显示器矩形 + 可见面板高 + 越界余量 → AppBar 预留带矩形与
/// 真实窗口矩形。纯函数、无 Win32/WPF 依赖，因此多屏与各种 DPI 的组合可以在这层单测覆盖
/// （真机那些分支本 VM 截图证不了）。
/// <para>
/// 【裁定（v3 §1.C）：预留带 = 可见面板，放大余量不占工作区】macOS Dock 的原生语义是
/// "停靠区只预留 Dock 本体高度，放大后的图标越界生长到桌面之上"。v2 把"面板 + headroom"
/// 整条报给 <c>ABM_SETPOS</c>，于是那段什么都不装的透明带也被从工作区里划走（IconSize=44 时
/// 面板 76 DIP 却预留 ~98 DIP），这正是用户"我只需要 dock 的大小不需要额外的覆盖"的两层含义之一。
/// 本类把两者拆成两个矩形：<see cref="Reserve"/> 上报 shell（决定工作区），
/// <see cref="WindowRect"/> 交给 <c>SetWindowPos</c>（决定像素与命中范围）——窗口允许越出自己的
/// 预留带，预留带本来只决定工作区。
/// </para>
/// <para>
/// 【贴边不变量（恒成立，不依赖任何 shell 返回值）】
/// <list type="bullet">
/// <item><description>顶栏：<c>window.Top == rcMonitor.Top</c> 恒成立 ——
/// 预留带顶边取 <c>rcMonitor.Top</c>，顶栏窗口矩形又恒等于预留带（headroom 不参与顶边），
/// 所以条带与显示器上沿之间不存在任何"露壁纸"的空隙。</description></item>
/// <item><description>Dock：<c>window.Bottom == reserve.Bottom</c> 恒成立，而 reserve.Bottom 取
/// 调用方传入的"可用底边"（shell QUERYPOS 允许我们占到的底边；任务栏条带预留已释放时它
/// 就等于 <c>rcMonitor.Bottom</c>，Keep 策略时是任务栏上沿）。</description></item>
/// <item><description>两条带的宽度恒等于显示器全宽 <c>rcMonitor.Right - Left</c>（AppBar 语义要求
/// 沿边铺满），副屏的 <c>Left</c> 可为负数或 &gt;0，全部由矩形自身携带，不做任何"减去主屏"的假设。</description></item>
/// </list>
/// </para>
/// </summary>
public static class StripMath
{
    /// <summary>
    /// DIP → 物理 px：沿用 App 既有口径 <c>(int)Math.Round(dip * scale)</c>（默认
    /// <see cref="MidpointRounding.ToNearestEven"/>）。**不要**改成 AwayFromZero：
    /// 125%/175% 这类缩放会在半数取值上产生 1px 的系统性偏移，而"贴底无缝 / gap 0px"
    /// 的既有实测结论是按本口径打日志比对的，换了口径日志就对不上了。
    /// </summary>
    /// <param name="dip">设备无关长度或坐标。</param>
    /// <param name="scale">目标显示器的有效 DPI 缩放（= 有效 DPI / 96）。</param>
    /// <returns>四舍五入后的物理 px。</returns>
    public static int ToPhysical(double dip, double scale)
        => (int)Math.Round(dip * SafeScale(scale));

    /// <summary>
    /// 物理 px → DIP：与 <see cref="ToPhysical"/> 严格互逆的连续逆变换（不取整）。
    /// 写给 WPF 的 <c>Left/Top/Width/Height</c> 用这个——WPF 接受 double，先取整反而会在
    /// 125%/150% 下引入 1 物理 px 的累积偏差（再由 WPF 换算回物理时放大）。
    /// </summary>
    /// <param name="physical">物理 px 坐标或长度。</param>
    /// <param name="scale">目标显示器的有效 DPI 缩放。</param>
    /// <returns>对应的 DIP。</returns>
    public static double ToDip(int physical, double scale) => physical / SafeScale(scale);

    /// <summary>
    /// AppBar 预留带矩形（上报 <c>ABM_QUERYPOS</c>/<c>ABM_SETPOS</c>，决定 shell 工作区）：
    /// 沿边铺满显示器全宽，高度 = 可见面板物理高，并**贴死**所在边的显示器边缘。
    /// </summary>
    /// <param name="monitor">
    /// 显示器矩形（物理 px，通常传 <c>MONITORINFO.rcMonitor</c>）。也可以传
    /// <c>ABM_QUERYPOS</c> 返回的"可用矩形"——顶边只用它的 <c>Top/Left/Right</c>、
    /// 底边只用它的 <c>Bottom/Left/Right</c>，这正好是 shell 允许我们落位的那条边。
    /// </param>
    /// <param name="panelPhysicalH">可见面板物理高（负值按 0 处理，见备注）。</param>
    /// <param name="edge">停靠边。</param>
    /// <returns>预留带矩形。<c>Top</c> 边满足 <c>Top == monitor.Top</c>；<c>Bottom</c> 边满足
    /// <c>Bottom == monitor.Bottom</c>。</returns>
    public static PhysicalRect Reserve(PhysicalRect monitor, int panelPhysicalH, StripEdge edge)
    {
        // 面板高为负只会来自上游算错（行高/边距被改成负数）：钳到 0 = "不预留任何工作区"，
        // 比让 Bottom < Top 生成倒挂矩形（SetWindowPos 收到负高度即失败、条带整体失联）更可恢复。
        var h = Math.Max(0, panelPhysicalH);
        return edge == StripEdge.Top
            ? new PhysicalRect(monitor.Left, monitor.Top, monitor.Right, monitor.Top + h)
            : new PhysicalRect(monitor.Left, monitor.Bottom - h, monitor.Right, monitor.Bottom);
    }

    /// <summary>
    /// 真实窗口矩形（交给 <c>SetWindowPos</c>，决定像素覆盖范围与光标命中范围）：
    /// 在预留带之上再多出 <paramref name="headroomPhysical"/> 的透明余量。
    /// </summary>
    /// <param name="reserve"><see cref="Reserve"/> 的结果（或 shell 最终落地的预留带）。</param>
    /// <param name="headroomPhysical">越界生长的透明余量物理 px（负值按 0 处理）。</param>
    /// <param name="edge">停靠边。</param>
    /// <returns>
    /// 顶边：<c>window == reserve</c>（顶栏没有越界生长的内容，二者恒等 → 贴边不变量成立）；
    /// 底边：底边与预留带同底、顶边上抬 headroom（放大后的图标生长在桌面上，不占工作区）。
    /// </returns>
    public static PhysicalRect WindowRect(PhysicalRect reserve, int headroomPhysical, StripEdge edge)
    {
        var h = Math.Max(0, headroomPhysical);
        return edge == StripEdge.Top
            ? reserve
            : new PhysicalRect(reserve.Left, reserve.Top - h, reserve.Right, reserve.Bottom);
    }

    /// <summary>
    /// 一次算清"预留带 + 窗口"两个矩形（DIP 入参，内部按 <paramref name="scale"/> 换算）。
    /// MonitorGeometry 只做 Win32 取值，算术全部收口到这里，便于单测覆盖 DPI/多屏组合。
    /// </summary>
    /// <param name="monitor">显示器矩形（物理 px）。</param>
    /// <param name="panelDip">可见面板高（DIP）。</param>
    /// <param name="headroomDip">越界生长余量（DIP）；顶栏传 0。</param>
    /// <param name="scale">该显示器的有效 DPI 缩放。</param>
    /// <param name="edge">停靠边。</param>
    /// <returns>预留带与窗口矩形。</returns>
    public static (PhysicalRect Reserve, PhysicalRect Window) Band(
        PhysicalRect monitor, double panelDip, double headroomDip, double scale, StripEdge edge)
    {
        var panelPhysical = ToPhysical(panelDip, scale);
        var headroomPhysical = ToPhysical(headroomDip, scale);
        var r = Reserve(monitor, panelPhysical, edge);
        return (r, WindowRect(r, headroomPhysical, edge));
    }

    /// <summary>
    /// 缩放兜底：<c>scale &lt;= 0</c> 或非有限值只可能来自 Win32 取 DPI 失败
    /// （<c>GetDpiForMonitor</c> 非 S_OK、返回 0），按 1:1 处理。
    /// 让它穿过会把矩形炸成 NaN/负数，<c>SetWindowPos</c> 收到非法尺寸后整条定位链路失效。
    /// </summary>
    private static double SafeScale(double scale)
        => double.IsFinite(scale) && scale > 0 ? scale : 1.0;
}
