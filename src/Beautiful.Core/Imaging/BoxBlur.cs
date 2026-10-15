namespace Beautiful.Core.Imaging;

/// <summary>
/// 分离式**三次盒式模糊**（≈ 高斯）与配套的整数倍盒式降采样，作用在 BGRA 字节缓冲上。
/// <para>
/// 【为什么在 Core（v3 T2 裁定）】v2 把"玻璃感"外包给 DWM 系统背板，真机证伪（非分层窗口的客户端
/// alpha 不被合成 → Alt+Tab 面板画成黑矩形）。v3 的模糊由我们自己合成：抓屏 → 降采样 → 模糊 →
/// 铺进自绘圆角卡。既然模糊回到了我们手里，它就必须是**可单测的纯函数**，而不是某个只存在于
/// 驱动/DWM 内部、本机截屏验证不了的东西。
/// </para>
/// <para>
/// 【为什么是"三次盒式"而不是直接高斯】三次不同窗口的盒式卷积的复合等价于一个近似高斯核
/// （Central Limit Theorem 的标准用法，Photoshop/Chromium 的快速模糊同此路径），代价是
/// O(w·h) 每趟、与半径无关：半径加大只增窗口不减帧时，这正是"背景模糊"要的形态。
/// </para>
/// <para>
/// 【边界处理（为什么是"窗口内取均值"而不是"边缘像素复制"）】窗口越出图像时收缩到有效像素再取均值，
/// 于是：① <c>radius ≥ 宽/高</c> 时结果恰为整行/整列均值，永不越界；② 常量图的均值恒等
/// （整图常量 → 每窗均值 = 该常量，逐像素精确复现，单测据此锁死）；③ 全局均值几乎守恒
/// （边缘不"吞掉"也不"重复计数"外部质量）。边缘复制法在 ①②③ 上都做不到。
/// </para>
/// <para>
/// 【alpha】只处理 BGR 三通道，第 4 通道逐字节原样搬运。调用方（抓屏路径）把 alpha 归一为 255
/// ——GDI <c>BitBlt</c> 取回的 32bpp 缓冲里 alpha 恒 0，而屏幕内容本就是不透明的，模糊绝不能把
/// 它当成"透明"来参与均值（否则整张背板会被 0 冲淡 → 越模糊越黑）。
/// </para>
/// <para>
/// 【非法入参 = no-op】本类服务于装饰路径（背板画刷）。抛异常只会让调用方在 catch 里放弃背板，
/// 结果与 no-op 相同，却多一条崩溃风险；把"宽度/半径为 0、缓冲长度不足"定义为静默不处理，
/// 降级链（背板不可用 → 实心卡底）因此是唯一需要测试的分支。
/// </para>
/// </summary>
public static class BoxBlur
{
    /// <summary>BGRA 每像素字节数。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// 默认迭代次数：三趟（每趟水平+垂直）盒式核复合 ≈ σ≈ 半径的高斯。少一趟会有可见的"方块"痕迹，
    /// 多一趟只是更糊，对 1/4 采样的背板而言三趟是观感/代价的拐点。
    /// </summary>
    public const int DefaultPasses = 3;

    /// <summary>
    /// 就地模糊一块 BGRA 缓冲（先水平后垂直，重复 <paramref name="passes"/> 趟）。
    /// </summary>
    /// <param name="bgra">行主序 BGRA 缓冲，长度须 ≥ width·height·4。</param>
    /// <param name="width">图像宽（像素）。</param>
    /// <param name="height">图像高（像素）。</param>
    /// <param name="radius">盒式核半径（单边像素数；窗口宽 = 2r+1）。</param>
    /// <param name="passes">迭代趟数。</param>
    public static void Apply(Span<byte> bgra, int width, int height, int radius, int passes = DefaultPasses)
    {
        if (!IsUsable(bgra, width, height)) return;
        if (radius <= 0 || passes <= 0) return;   // 半径 0 = 恒等，直接不做（省一趟拷贝）
        var n = width * height * BytesPerPixel;
        var scratch = new byte[n];
        // 前缀和数组按"较长的那一维"分配一次，两趟复用（避免每行 new 一次 → 每次抓屏上万次分配）。
        var prefix = new long[Math.Max(width, height) + 1];
        for (var p = 0; p < passes; p++)
        {
            Horizontal(bgra[..n], scratch, width, height, radius, prefix);
            Vertical(scratch, bgra[..n], width, height, radius, prefix);
        }
    }

    /// <summary>
    /// 整数倍**盒式降采样**（每 <paramref name="factor"/>×<paramref name="factor"/> 块取均值）。
    /// 先降采样再模糊有两个硬理由：① 模糊代价按像素数算，1/4 尺寸 = 1/16 工作量；
    /// ② 降采样本身就是抗混叠预滤波器，直接对原分辨率模糊会漏出高频（图标边缘）而失去"磨砂"质感。
    /// </summary>
    /// <param name="src">源 BGRA 缓冲，长度须 ≥ width·height·4。</param>
    /// <param name="width">源宽（像素）。</param>
    /// <param name="height">源高（像素）。</param>
    /// <param name="factor">整数倍缩小系数（&lt;1 按 1 处理 = 原样复制）。</param>
    /// <param name="outWidth">输出宽：向上取整 <c>ceil(width/factor)</c>——**必须向上取整**，
    /// 调用方按"1 输出像素 = factor 个源像素"建立坐标映射，向上取整保证映射永不越出缓冲。</param>
    /// <param name="outHeight">输出高（同上）。</param>
    /// <returns>降采样后的新缓冲（四通道同做块均值）；非法入参返回空数组。</returns>
    public static byte[] Downscale(ReadOnlySpan<byte> src, int width, int height, int factor,
                                   out int outWidth, out int outHeight)
    {
        outWidth = outHeight = 0;
        if (width <= 0 || height <= 0 || src.Length < width * height * BytesPerPixel) return [];
        if (factor < 1) factor = 1;
        outWidth = (width + factor - 1) / factor;
        outHeight = (height + factor - 1) / factor;
        var dst = new byte[outWidth * outHeight * BytesPerPixel];
        for (var oy = 0; oy < outHeight; oy++)
        {
            // 尾块不足 factor 行/列时收缩到实际可用像素：宁可少几个像素参与均值，
            // 也不能读到 src 之外（源尺寸不被 factor 整除是常态）。
            var yEnd = Math.Min(height - 1, (oy + 1) * factor - 1);
            for (var ox = 0; ox < outWidth; ox++)
            {
                var xEnd = Math.Min(width - 1, (ox + 1) * factor - 1);
                var count = (yEnd - oy * factor + 1) * (xEnd - ox * factor + 1);
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = oy * factor; y <= yEnd; y++)
                {
                    var row = y * width * BytesPerPixel;
                    for (var x = ox * factor; x <= xEnd; x++)
                    {
                        var i = row + x * BytesPerPixel;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                    }
                }
                var o = (oy * outWidth + ox) * BytesPerPixel;
                dst[o] = Round(b, count);
                dst[o + 1] = Round(g, count);
                dst[o + 2] = Round(r, count);
                dst[o + 3] = Round(a, count);
            }
        }
        return dst;
    }

    /// <summary>缓冲与尺寸是否构成合法图像（非法 = 所有入口一律 no-op，见类型注释）。</summary>
    private static bool IsUsable(ReadOnlySpan<byte> bgra, int width, int height)
        => width > 0 && height > 0 && bgra.Length >= width * height * BytesPerPixel;

    /// <summary>四舍五入取整（<c>(sum + n/2)/n</c>）：整型域内完成，不引入浮点误差，
    /// 因而常量图的块均值精确等于该常量（单测断言的是**精确相等**，不是"接近"）。</summary>
    private static byte Round(long sum, int count) => (byte)((sum + count / 2) / count);

    /// <summary>水平一趟：读 <paramref name="src"/> 写 <paramref name="dst"/>（同一块缓冲不可重叠调用）。</summary>
    private static void Horizontal(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h,
                                   int radius, long[] prefix)
    {
        // 半径钳到 w-1：窗口左右各留 w-1 时已覆盖整行，再大只是把同一件事算更多遍。
        var rx = Math.Min(radius, w - 1);
        for (var y = 0; y < h; y++)
        {
            var row = y * w * BytesPerPixel;
            for (var c = 0; c < 3; c++)
            {
                prefix[0] = 0;
                for (var x = 0; x < w; x++) prefix[x + 1] = prefix[x] + src[row + x * BytesPerPixel + c];
                for (var x = 0; x < w; x++)
                {
                    var left = Math.Max(0, x - rx);
                    var right = Math.Min(w - 1, x + rx);
                    var count = right - left + 1;
                    dst[row + x * BytesPerPixel + c] = Round(prefix[right + 1] - prefix[left], count);
                }
            }
            // alpha：原样搬运（见类型注释——屏幕内容的 alpha 已被调用方钉在 255，绝不能参与均值）。
            for (var x = 0; x < w; x++) dst[row + x * BytesPerPixel + 3] = src[row + x * BytesPerPixel + 3];
        }
    }

    /// <summary>垂直一趟：读 <paramref name="src"/> 写 <paramref name="dst"/>。</summary>
    private static void Vertical(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h,
                                 int radius, long[] prefix)
    {
        var ry = Math.Min(radius, h - 1);
        for (var x = 0; x < w; x++)
        {
            for (var c = 0; c < 3; c++)
            {
                prefix[0] = 0;
                for (var y = 0; y < h; y++) prefix[y + 1] = prefix[y] + src[(y * w + x) * BytesPerPixel + c];
                for (var y = 0; y < h; y++)
                {
                    var top = Math.Max(0, y - ry);
                    var bottom = Math.Min(h - 1, y + ry);
                    var count = bottom - top + 1;
                    dst[(y * w + x) * BytesPerPixel + c] = Round(prefix[bottom + 1] - prefix[top], count);
                }
            }
            for (var y = 0; y < h; y++) dst[(y * w + x) * BytesPerPixel + 3] = src[(y * w + x) * BytesPerPixel + 3];
        }
    }
}
