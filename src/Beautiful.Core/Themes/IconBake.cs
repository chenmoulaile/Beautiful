namespace Beautiful.Core.Themes;

/// <summary>
/// 图标遮罩形状（**主题数据**，v3 T6）。
/// <para>
/// 【为什么不是渲染器里的 if (theme == mac)】"这个主题的图标长什么形状/有没有阴影"和分隔符一样是主题
/// 参数：macOS 的图标被剪成圆角方块并浮在一道投影上、鸿蒙是 squircle 连续圆角 + 投影、GNOME dash 是
/// 平铺的原始图标（无遮罩无投影）。写死在烘焙代码里就必然出现"换主题还是鸿蒙形状"的漂移。
/// </para>
/// </summary>
public enum IconMaskStyle
{
    /// <summary>不遮罩：图标原样（位图不裁形，形状由 XAML 的 <c>ThemeIconClip</c> 负责——见 T6 的"形状唯一属主"裁定）。</summary>
    None,

    /// <summary>圆角方形：半径 = <see cref="IconBake.MaskRadiusRatio"/>·边长。</summary>
    RoundedRectangle,

    /// <summary>超椭圆（squircle）：复用 <see cref="Superellipse"/>，n=<see cref="SquircleExponent"/>。</summary>
    Squircle
}

/// <summary>
/// 图标遮罩/烘焙的形状参数（纯常量 + 纯函数，可单测）。像素工作留在 App 层（WPF），这里只回答
/// "什么形状、多大半径、留多少投影空间"。
/// </summary>
public static class IconBake
{
    /// <summary>鸿蒙 squircle 的超椭圆指数（n=2 退化为椭圆；4.4 ≈ 25% 连续圆角观感，与既有 ThemeIconClip 同源）。</summary>
    public const double SquircleExponent = 4.4;

    /// <summary>squircle 多边形采样点数（与 <see cref="Superellipse.Points"/> 默认一致）。</summary>
    public const int SquircleSamples = 96;

    /// <summary>圆角半径比例合法区间：0（直角）..0.5（内切圆，再大就是圆而不是圆角方块了）。</summary>
    public const double MaxMaskRadiusRatio = 0.5;

    /// <summary>
    /// 半径比例钳制：<see cref="IconMaskStyle.None"/> 恒 0（无遮罩就没有半径可言，别让"ratio=0.25 + None"
    /// 这种组合画出半个圆角）；其余主题钳到 [0, <see cref="MaxMaskRadiusRatio"/>]，NaN/负数归 0。
    /// </summary>
    public static double ClampRadiusRatio(IconMaskStyle style, double ratio)
    {
        if (style == IconMaskStyle.None) return 0;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return 0;
        return Math.Clamp(ratio, 0, MaxMaskRadiusRatio);
    }

    /// <summary>圆角方形半径（物理像素）：向下取整后至少 0；NaN/非法比例先经 <see cref="ClampRadiusRatio"/>。</summary>
    public static int RadiusPx(IconMaskStyle style, double ratio, int sidePx)
    {
        if (style != IconMaskStyle.RoundedRectangle || sidePx <= 0) return 0;
        return (int)Math.Floor(ClampRadiusRatio(style, ratio) * sidePx);
    }

    /// <summary>
    /// squircle 归一化轮廓（正方形 artwork 内接，坐标 0..1，中心 0.5,0.5）：由
    /// <see cref="Superellipse"/> 以半轴 0.5 采样后平移，App 层只需乘上像素尺寸即可成 Geometry。
    /// 【为什么在 Core 出点而不是 App 直接建几何】点位就是"形状属主"的定义本身，必须能被单测锁死
    /// （对称性、落在单位方框内、45° 比圆更饱满）——形状错了截图只能看出"歪了"，看不出为什么歪。
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> SquircleUnitPoints()
    {
        var pts = Superellipse.Points(0.5, 0.5, SquircleExponent, SquircleSamples);
        var unit = new List<(double, double)>(pts.Count);
        foreach (var (x, y) in pts) unit.Add((x + 0.5, y + 0.5));
        return unit;
    }

    /// <summary>
    /// 归一化轮廓点 → 指定矩形内的像素点（App 层构建几何的唯一入口）。
    /// 不做任何裁剪：点就落在 [x0, x0+w] × [y0, y0+h]。
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> ToPixels(
        IReadOnlyList<(double X, double Y)> unit, double x0, double y0, double w, double h)
    {
        var pts = new List<(double, double)>(unit.Count);
        foreach (var (x, y) in unit) pts.Add((x0 + x * w, y0 + y * h));
        return pts;
    }
}
