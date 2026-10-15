namespace Beautiful.Core.Imaging;

/// <summary>
/// **alpha 通道**的三次盒式模糊（图标投影用）。
/// <para>
/// 【为什么不能直接用 <see cref="BoxBlur"/>】BoxBlur 的纪律是"只处理 BGR、alpha 逐字节原样搬运"
/// （屏幕抓屏的 alpha 恒 0 且内容本就不透明，参与均值会把背板冲黑）。而图标的投影恰好相反：
/// **形状信息全在 alpha 里**（剪影 = 图标的非透明区域），BGR 是无意义的黑色。
/// 于是这里做三步通道搬运：alpha → BGR（交给 BoxBlur 处理）→ 模糊结果写回 alpha 并把 BGR 清零，
/// 得到一张"纯黑 + 模糊 alpha"的投影位图。复用 BoxBlur 而不是再写一遍卷积，是为了让
/// "边缘收缩取均值"这条已经单测锁死的边界纪律只存在一份。
/// </para>
/// </summary>
public static class AlphaBlur
{
    /// <summary>
    /// 就地模糊 BGRA 缓冲的 alpha 通道，结果写回 alpha 且 BGR 清零（投影是纯黑）。
    /// 半径/趟数非法时 no-op（与 <see cref="BoxBlur"/> 同一纪律：装饰路径不抛异常）。
    /// </summary>
    public static void Apply(Span<byte> bgra, int width, int height, int radius,
                             int passes = BoxBlur.DefaultPasses)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * BoxBlur.BytesPerPixel) return;
        if (radius <= 0 || passes <= 0) return;

        var n = width * height * BoxBlur.BytesPerPixel;
        for (var i = 0; i < n; i += BoxBlur.BytesPerPixel)
        {
            var a = bgra[i + 3];
            bgra[i] = a; bgra[i + 1] = a; bgra[i + 2] = a;   // alpha 搬进 BGR 让 BoxBlur 有东西可算
            bgra[i + 3] = byte.MaxValue;                       // 钉成不透明：模糊期间 alpha 不能再参与
        }

        BoxBlur.Apply(bgra[..n], width, height, radius, passes);

        for (var i = 0; i < n; i += BoxBlur.BytesPerPixel)
        {
            bgra[i + 3] = bgra[i];   // 模糊后的亮度就是模糊后的 alpha
            bgra[i] = 0; bgra[i + 1] = 0; bgra[i + 2] = 0;   // 纯黑投影
        }
    }
}
