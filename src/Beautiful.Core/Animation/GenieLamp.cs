namespace Beautiful.Core.Animation;

/// <summary>灯罩曲线用到的一个矩形（物理像素）。图标矩形只需要 X/Y/Width，Height 给 0 即可。</summary>
public readonly record struct LampGeometry(double X, double Y, double Width, double Height);

/// <summary>
/// macOS genie（"灯罩"）曲线的网格顶点映射。
/// <para>
/// 数学口径来自 Windhawk 的 <c>macos-minimize-animation</c> mod（MIT）里那套 Direct2D 引擎的
/// <c>CalculateLampVertexMacOS</c>：把源窗口切成 <c>rows × cols</c> 的网格，每个顶点
/// <c>(tx, ty) ∈ [0,1]²</c> 按进度 <c>p</c> 映到屏幕坐标。这里只做**同一件事的 C# 实现**，
/// 输入输出全是物理像素，不依赖任何渲染后端，所以能脱离屏幕单测。
/// </para>
/// <para>
/// 【为什么是两阶段】<c>p ≤ Split</c> 时窗口先"拉长"到下缘触到图标那一行（<c>k</c> 从 0 涨到 1），
/// <c>p &gt; Split</c> 时整体再收进图标（<c>j</c> 从 0 涨到 1）。弯曲感来自一个事实：
/// <b>同一进度下，越靠下的行收得越窄</b>——于是窗口自然形成一条"脖子"，而不是一块在缩放的矩形。
/// 另有一项正弦摆动 <c>effectX</c> 只在第一阶段起作用，给"甩进去"的弹性收尾。
/// </para>
/// </summary>
public static class GenieLamp
{
    /// <summary>两个阶段的分界点（与参考实现一致：前 30% 拉长，后 70% 收拢）。</summary>
    public const double Split = 0.3;

    /// <summary>正弦摆动的幅度分母（7 = 参考实现的取值；改大就更"硬"，改小会甩过头）。</summary>
    private const double SwayDivisor = 7.0;

    private const double Pi = System.Math.PI;

    /// <summary>
    /// 单个顶点映射。
    /// </summary>
    /// <param name="tx">顶点在源窗口内的水平归一坐标（0=左缘，1=右缘）。</param>
    /// <param name="ty">垂直归一坐标（0=上缘，1=下缘，即离图标最近的那一行）。</param>
    /// <param name="progress">动画进度，任意实数都会被钳到 [0,1]。</param>
    /// <param name="window">源窗口矩形（物理像素）。</param>
    /// <param name="icon">目标图标矩形；<see cref="LampGeometry.Height"/> 不参与运算。</param>
    public static (double X, double Y) Vertex(
        double tx, double ty, double progress, LampGeometry window, LampGeometry icon)
    {
        var p = Clamp01(progress);

        var k = p <= Split ? p / Split : 1.0;
        var j = p > Split ? (p - Split) / (1.0 - Split) : 0.0;

        var expandHeight = icon.Y - window.Y - window.Height;
        var fullHeight = (icon.Y - window.Y) - expandHeight * (1.0 - k);
        var height = fullHeight - j * fullHeight;

        var y = ty * height;
        var x = tx * icon.Width
                + tx * (window.Width - icon.Width) * (1.0 - j) * (1.0 - ty)
                + tx * (window.Width - icon.Width) * (1.0 - k) * ty;

        var offsetX = (icon.X - window.X) * (y / (fullHeight + 0.1)) * k
                      + (icon.X - window.X) * j;
        var offsetY = icon.Y - window.Y - height - expandHeight * (1.0 - k);

        // 正弦摆动：幅度与"该行离目标还有多远"成正比，且只在前一阶段起作用
        var sway = System.Math.Sin(((height - y) / fullHeight) * 2.0 * Pi + Pi)
                   * (window.X + window.Width * tx - (icon.X + icon.Width * tx)) / SwayDivisor * k;

        return (window.X + x + offsetX + sway, window.Y + y + offsetY);
    }

    /// <summary>
    /// 一行的可见宽度（同一 <c>ty</c> 上 <c>tx=0</c> 到 <c>tx=1</c> 的水平距离）。
    /// 单独暴露是因为"下缘比上缘窄"正是 genie 的脖子，这条不变量必须能被断言。
    /// </summary>
    public static double RowWidth(double ty, double progress, LampGeometry window, LampGeometry icon)
    {
        var right = Vertex(1.0, ty, progress, window, icon).X;
        var left = Vertex(0.0, ty, progress, window, icon).X;
        return right - left;
    }

    /// <summary>钳到 [0,1]：动画计时可能因卡顿略微越界，越界不该改变形状。</summary>
    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
