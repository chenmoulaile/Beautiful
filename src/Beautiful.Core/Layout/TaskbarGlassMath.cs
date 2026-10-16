namespace Beautiful.Core.Layout;

/// <summary>原生任务栏贴着屏幕的哪条边（决定装饰该往哪一侧收口）。</summary>
public enum GlassEdge
{
    /// <summary>贴底（绝大多数人的默认位置）。</summary>
    Bottom,

    /// <summary>贴顶。</summary>
    Top,

    /// <summary>贴左。</summary>
    Left,

    /// <summary>贴右。</summary>
    Right,

    /// <summary>认不出来（矩形不在任何一条边上，或尺寸非法）——调用方应当不画，而不是猜一条边。</summary>
    Unknown,
}

/// <summary>
/// "任务栏玻璃壳"的几何算术（纯函数）。
/// <para>
/// 这个功能的设计目标写死在裁定里：<b>只美化、不接管</b>。原生任务栏一个像素都不移动、不隐藏、
/// 不拦截点击，我们在它上面盖一层点击穿透的自绘装饰，把 Win11 那条"贴底的直角色带"读成
/// "浮在屏底的一条玻璃"。所以这里只算三样东西：圆角收口、面向屏幕内侧的高光亮边、以及同一条
/// 边上的强调色发丝线。
/// </para>
/// <para>
/// 【为什么不做"每个应用下方的运行指示条"】那需要持续跟踪原生按钮的几何——Win11 24H2 只在 UIA 里
/// 暴露这些矩形，且会随图标增减、通知角标、系统语言实时变动。为一个装饰去轮询别人的布局，
/// 既贵又会在按钮动画期间画歪。做不到稳定，就不如不做（项目裁定：做不到无缝接管就别宣称接管）。
/// </para>
/// </summary>
public static class TaskbarGlassMath
{
    /// <summary>期望圆角半径（物理 px，@100%）。与顶栏/Dock 的卡片圆角同一量级，读起来才是一家人。</summary>
    public const double PreferredCornerPx = 12;

    /// <summary>面向屏幕内侧那条 1px 高光亮边的期望厚度。</summary>
    public const double PreferredHighlightPx = 1;

    /// <summary>强调色发丝线的期望厚度。</summary>
    public const double PreferredHairlinePx = 2;

    /// <summary>
    /// 这个任务栏矩形值不值得画。挡掉三类脏数据：零/负尺寸、shell 哨兵值（最小化或已销毁的窗口
    /// 会被摆到 -32000 一带）、以及大得不可能是任务栏的矩形（多半是全屏应用的伪窗口）。
    /// </summary>
    public static bool IsDecoratable(in PhysicalRect bar, in PhysicalRect monitor)
    {
        var width = bar.Right - bar.Left;
        var height = bar.Bottom - bar.Top;
        if (width <= 0 || height <= 0) return false;
        if (monitor.Right - monitor.Left <= 0 || monitor.Bottom - monitor.Top <= 0) return false;
        // 必须与这块显示器有交集：shell 对"已销毁/已最小化"的窗口给的是 -32000 一带的哨兵坐标，
        // 它的宽高看起来完全"像"一条任务栏，只有位置能把它筛掉。
        if (bar.Right <= monitor.Left || bar.Left >= monitor.Right) return false;
        if (bar.Bottom <= monitor.Top || bar.Top >= monitor.Bottom) return false;
        if (width > (monitor.Right - monitor.Left) * 1.5 || height > (monitor.Bottom - monitor.Top) * 1.5) return false;
        // 任务栏再厚也不会超过短边的四成；超过说明拿到的不是它
        var shortSide = Math.Min(monitor.Right - monitor.Left, monitor.Bottom - monitor.Top);
        return width <= shortSide * 4 && height <= shortSide * 0.4;
    }

    /// <summary>
    /// 贴哪条边。判据是"任务栏的某条边与显示器对应边重合（容差 <paramref name="tolerance"/>）"，
    /// 并且它在另外两个方向上接近满幅。四条都不满足给 <see cref="GlassEdge.Unknown"/>。
    /// </summary>
    public static GlassEdge DetectEdge(in PhysicalRect bar, in PhysicalRect monitor, int tolerance = 4)
    {
        var width = bar.Right - bar.Left;
        var height = bar.Bottom - bar.Top;
        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        if (width <= 0 || height <= 0 || monitorWidth <= 0 || monitorHeight <= 0) return GlassEdge.Unknown;

        var tol = Math.Max(0, tolerance);
        var spansWidthHorizontally = Math.Abs(width - monitorWidth) <= tol * 2;
        var spansVertically = Math.Abs(height - monitorHeight) <= tol * 2;

        if (spansWidthHorizontally && Math.Abs(bar.Bottom - monitor.Bottom) <= tol) return GlassEdge.Bottom;
        if (spansWidthHorizontally && Math.Abs(bar.Top - monitor.Top) <= tol) return GlassEdge.Top;
        if (spansVertically && Math.Abs(bar.Left - monitor.Left) <= tol) return GlassEdge.Left;
        if (spansVertically && Math.Abs(bar.Right - monitor.Right) <= tol) return GlassEdge.Right;
        return GlassEdge.Unknown;
    }

    /// <summary>圆角半径：钳到 [0, 短边的一半]。任务栏很薄时圆角不能超过它自己的一半高，否则整条被啃没。</summary>
    public static double CornerRadius(int width, int height, double preferred = PreferredCornerPx)
    {
        if (width <= 0 || height <= 0) return 0;
        return Math.Clamp(preferred, 0, Math.Min(width, height) / 2.0);
    }

    /// <summary>
    /// 亮边/发丝线厚度：按 DPI 放大，并钳到短边的 1/4。
    /// 高 DPI 上不放大就会细到看不见；而极薄的任务栏（比如被第三方工具压到 24px）上
    /// 不封顶就会让线本身变成主角。
    /// </summary>
    public static double EdgeThickness(double scale, int barShortSide, double preferred)
    {
        if (barShortSide <= 0) return 0;
        var s = scale <= 0 ? 1 : scale;
        return Math.Clamp(preferred * s, 0, barShortSide / 4.0);
    }

    /// <summary>装饰该在哪一侧收口：面向屏幕内侧的那两条角是圆的，贴着屏幕边缘的那两条不是。</summary>
    public static (bool TopLeft, bool TopRight, bool BottomLeft, bool BottomRight) RoundedCorners(GlassEdge edge) => edge switch
    {
        GlassEdge.Bottom => (true, true, false, false),
        GlassEdge.Top => (false, false, true, true),
        GlassEdge.Left => (false, true, false, true),
        GlassEdge.Right => (true, false, true, false),
        _ => (false, false, false, false),
    };
}
