namespace Beautiful.Core.Animation;

/// <summary>归一化矩形（相对源窗口：左上角 (0,0)、右下角 (1,1)）。渲染侧乘源宽高即得像素。</summary>
public readonly record struct GenieRect(double X, double Y, double Width, double Height);

/// <summary>
/// macOS "genie" 最小化动画的几何算术（纯函数）。
/// <para>
/// 模型：把源窗口沿垂直方向切成 <b>N 条水平带</b>，每条带各自把自己的上下边向目标点收拢，
/// 同时把宽度收到目标图标的宽度。因为每条带独立收拢，带与带之间自然叠压，
/// 看上去就是"纸被吸进洞里"的那股弯曲感——这是不引入 3D 网格与着色器的前提下最接近原味的做法。
/// </para>
/// <para>
/// 【为什么要"滞后"】所有带同时动会像一个矩形在缩放，完全没有 genie 的味道。
/// macOS 的观感来自<b>靠近目标的带先动、远离的带后动</b>：底边先被吸进去，顶边才跟着折下来。
/// 于是引入 <paramref name="lag"/>：越靠近目标（下缘）的带起步越早。
/// </para>
/// <para>
/// 【为什么放 Core】这套映射的正确性（端点、单调、不交叉）与"跑起来好不好看"是两件事，
/// 前者可以锁在单测里；渲染侧只负责每帧把归一化矩形乘回像素，不参与判定。
/// 帧率无关由调用方保证：它每帧取真实经过的时间算 <c>t</c>，本函数只认 <c>t</c>。
/// </para>
/// </summary>
public static class GenieMath
{
    /// <summary>默认切带数。太少会有明显的台阶，太多每帧的布局开销白涨；22 在 4K 上肉眼无台阶。</summary>
    public const int DefaultSlices = 22;

    /// <summary>默认滞后：顶带比底带晚 45% 的时长起步。</summary>
    public const double DefaultLag = 0.45;

    /// <summary>把任意输入钳到 [0,1]（动画计时可能因卡顿略微越界，越界不该改变形状）。</summary>
    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// 第 <paramref name="index"/> 条带在总进度 <paramref name="t"/> 上的<b>局部</b>进度。
    /// 带号从顶往下数（0 = 顶，<paramref name="slices"/>-1 = 底），底带离目标最近所以先动。
    /// </summary>
    public static double SliceProgress(double t, int index, int slices, double lag = DefaultLag)
    {
        if (slices <= 0) return Clamp01(t);
        var clampedLag = Clamp01(lag);
        // near = 1 → 底带（最先动）；near = 1/slices → 顶带（最晚动）
        var near = (index + 1.0) / slices;
        var start = (1.0 - near) * clampedLag;
        var span = 1.0 - clampedLag;
        if (span <= 0.0) return Clamp01(t);
        return Clamp01((Clamp01(t) - start) / span);
    }

    /// <summary>
    /// 采样一次动画：<paramref name="t"/>=0 时每条带就是它在源窗口里的原始位置，
    /// t=1 时全部塌成目标点上的一条零高度线。
    /// </summary>
    /// <param name="targetX">目标中心的归一化 X（可以越出 0..1，Dock 在窗口左右侧时就是这种情况）。</param>
    /// <param name="targetY">目标线的归一化 Y（最小化到 Dock 时通常 &gt; 1，即源窗口下缘之外）。</param>
    /// <param name="endWidth">收拢后每条带的宽度（相对源宽，通常 = Dock 图标宽 / 窗口宽）。</param>
    public static List<(int Index, GenieRect Rect, double Opacity)> Sample(
        double t,
        int slices = DefaultSlices,
        double lag = DefaultLag,
        double targetX = 0.5,
        double targetY = 1.0,
        double endWidth = 0.12)
    {
        var count = Math.Max(1, slices);
        var global = Clamp01(t);
        var result = new List<(int, GenieRect, double)>(count);
        var bandHeight = 1.0 / count;

        for (var i = 0; i < count; i++)
        {
            var p = SliceProgress(global, i, count, lag);
            var top = i * bandHeight;
            var bottom = top + bandHeight;

            // 上下边各自向目标线收：p=1 时重合 ⇒ 高度归零，画面塌成一个点
            var y = Lerp(top, targetY, p);
            var h = Lerp(bottom, targetY, p) - y;
            var w = Lerp(1.0, endWidth, p);
            var x = Lerp(0.0, targetX - endWidth / 2.0, p);

            // 收尾淡出：塌到很窄时硬边会闪一下，最后 25% 把不透明度交出去
            var opacity = p <= 0.75 ? 1.0 : 1.0 - (p - 0.75) / 0.25;
            result.Add((i, new GenieRect(x, Math.Max(0, y), Math.Max(0, w), Math.Max(0, h)), opacity));
        }
        return result;
    }

    /// <summary>最大化/还原的"缩放"动画：整窗从源矩形线性插值到目标矩形（单条带，无滞后）。</summary>
    public static GenieRect ZoomLerp(GenieRect from, GenieRect to, double t)
    {
        var p = EaseOut(Clamp01(t));
        return new GenieRect(
            Lerp(from.X, to.X, p),
            Lerp(from.Y, to.Y, p),
            Lerp(from.Width, to.Width, p),
            Lerp(from.Height, to.Height, p));
    }

    /// <summary>ease-out cubic：起步快、收尾慢，与 macOS 的窗口缩放手感一致。</summary>
    public static double EaseOut(double t)
    {
        var c = Clamp01(t);
        var inv = 1.0 - c;
        return 1.0 - inv * inv * inv;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
