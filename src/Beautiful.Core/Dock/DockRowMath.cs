namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 行的竖向几何（纯函数、单位 DIP）。
/// <para>
/// v3 T4 起这些余量**由主题给出**而不再是全局常量 20：用户"下方的 dock 栏有一段是黑框，我只需要 dock
/// 的大小"的观感来源就是那份统一余量——IconSize=44 时行高 64、面板 76，图标只占面板的一半多。
/// 原生 macOS Dock 是紧贴图标的（图标 + 约 10 DIP 呼吸），GNOME dash 更松且方角，鸿蒙胶囊最松。
/// </para>
/// <para>
/// <see cref="DockSurface.RowHeightDip"/> 与 <c>DockWindow.VisiblePanelDip()</c> 都必须走本类，
/// 避免出现第二条算术路径导致"AppBar 预留带 ≠ 面板实际高"（v3 §1.C 的裁定）。
/// </para>
/// </summary>
public static class DockRowMath
{
    /// <summary>行高 = 图标 + 主题行内余量（+ 开启时的倒影余量）。</summary>
    public static double RowHeightDip(
        double iconSize, double rowInset, bool reflectionEnabled, double reflectionGapDip, double reflectionHeightRatio)
        => iconSize + rowInset + ReflectionExtraDip(iconSize, rowInset, reflectionEnabled, reflectionGapDip, reflectionHeightRatio);

    /// <summary>
    /// 倒影几何在行底需要的额外高度（关闭时恒 0）：静止态"间隙 + 倒影高"完整落入行内。
    /// <para>
    /// 推导：图标在加高后的行里仍垂直居中，倒影底缘 = containerBottom + 间隙 + 倒影高，其中
    /// containerBottom = (IconSize + inset + X + IconSize)/2 = IconSize + (inset+X)/2。要求
    /// IconSize + (inset+X)/2 + gap + ratio·IconSize ≤ IconSize + inset + X
    /// ⇔ X ≥ 2·gap + 2·ratio·IconSize − inset。取等号 = 倒影恰好填满行底，不多占条带一个像素。
    /// 于是行高在倒影开启时与 inset 无关（inset 被抵掉）：IconSize=44/gap=2/ratio=0.4 → 恒 83.2，
    /// 与收紧前一致——**收紧只发生在倒影关闭时**，这正是"现代 macOS Dock 无倒影"的默认路径。
    /// </para>
    /// </summary>
    public static double ReflectionExtraDip(
        double iconSize, double rowInset, bool reflectionEnabled, double reflectionGapDip, double reflectionHeightRatio)
        => reflectionEnabled
            ? Math.Max(0.0, 2.0 * reflectionGapDip + 2.0 * reflectionHeightRatio * iconSize - rowInset)
            : 0.0;

    /// <summary>可见面板高 = 行高 + 上下内边距（**这是上报 shell 的 AppBar 预留带高度**）。</summary>
    public static double PanelHeightDip(double rowHeightDip, double panelPaddingYDip)
        => rowHeightDip + 2.0 * panelPaddingYDip;

    /// <summary>
    /// 窗口在面板之上需要的透明余量，供放大后的图标向上生长不被窗口上沿裁切。
    /// <para>
    /// 条目底边离行底 <c>rowInset/2</c>（行内垂直居中）、行底离面板底 <c>panelPaddingY</c>，图标缩放原点是
    /// 底边中心 → 越出面板上沿的量 = <c>iconSize·(maxScale−1) − rowInset/2 − panelPaddingY</c>。
    /// 旧实现写成常量 <c>−16</c>（= 旧统一余量 20/2 + 6），行内余量改成主题驱动 10 之后那 16 就不再成立：
    /// 面板收紧会把放大后的图标顶边切掉。恒不小于 <paramref name="minHeadroomDip"/>（无放大主题的实测值）。
    /// </para>
    /// </summary>
    public static double MagnificationHeadroomDip(
        double iconSize, double maxScale, double rowInset, double panelPaddingYDip, double minHeadroomDip)
    {
        var overflow = iconSize * (maxScale - 1.0) - rowInset / 2.0 - panelPaddingYDip;
        return Math.Max(minHeadroomDip, Math.Ceiling(overflow));
    }
}
