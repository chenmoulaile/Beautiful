namespace Beautiful.Core.Themes;

/// <summary>
/// 超椭圆（squircle）几何采样：|x/a|^n + |y/b|^n = 1 的参数化采样。
/// n≈4.4 ≈ 鸿蒙 25% 连续圆角观感（n=2 退化为普通椭圆）。
/// </summary>
public static class Superellipse
{
    /// <summary>
    /// 按参数方程采样：x = a·sign(cosθ)·|cosθ|^(2/n)，y = b·sign(sinθ)·|sinθ|^(2/n)。
    /// 返回点以原点为中心，k=0 对应 θ=0 即 (a, 0)。
    /// </summary>
    public static List<(double X, double Y)> Points(double a, double b, double nExp, int samples = 96)
    {
        var pts = new List<(double, double)>(samples);
        for (var k = 0; k < samples; k++)
        {
            var th = 2 * Math.PI * k / samples;
            var c = Math.Cos(th); var s = Math.Sin(th);
            pts.Add((a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2.0 / nExp),
                     b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2.0 / nExp)));
        }
        return pts;
    }
}
