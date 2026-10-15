using System;
using Beautiful.Core.Imaging;
using Xunit;

/// <summary>
/// 背板模糊核（v3 T2）的纯函数测试。
/// <para>
/// 这层之所以存在：v3 把"玻璃感"从 DWM 系统背板改回我们自己的抓屏+模糊，模糊从此是**可断言的算术**，
/// 不再是一个本机截屏证不了的驱动行为（v2 的 hr==0 教训：属性被接受 ≠ 真的画了）。
/// 因此这里锁住的是"一张背板在视觉上不会出错"的充要条件：常量不被模糊成渐变、边缘不会越界、
/// 亮度均值不被越模糊越黑、alpha 绝不参与运算（参与就会被 0 冲淡 → 黑卡）。
/// </para>
/// </summary>
public class BoxBlurTests
{
    private const int Bpp = BoxBlur.BytesPerPixel;

    /// <summary>造一块常量 BGRA 缓冲（alpha 钉 255，与抓屏路径同形）。</summary>
    private static byte[] Constant(int w, int h, byte b, byte g, byte r)
    {
        var buf = new byte[w * h * Bpp];
        for (var i = 0; i < buf.Length; i += Bpp)
        {
            buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
        }
        return buf;
    }

    private static int At(byte[] buf, int w, int x, int y, int c) => buf[(y * w + x) * Bpp + c];

    // ---------- 常量图 ----------

    [Fact]
    public void 常量图任意半径模糊后逐像素精确不变()
    {
        const int w = 37, h = 11;                       // 故意取非 2 的幂：任何按位移优化的错都会露出来
        var buf = Constant(w, h, b: 17, g: 128, r: 240);
        var before = (byte[])buf.Clone();

        BoxBlur.Apply(buf, w, h, radius: 5, passes: BoxBlur.DefaultPasses);

        Assert.Equal(before, buf);                      // 块均值的均值仍是该常量：无漂移、无 ±1 抖动
    }

    [Fact]
    public void 单趟与三趟对常量图都恒等()
    {
        var buf = Constant(16, 9, 3, 4, 5);
        BoxBlur.Apply(buf, 16, 9, radius: 1, passes: 1);
        BoxBlur.Apply(buf, 16, 9, radius: 64, passes: 3);   // 半径远超尺寸：同样必须恒等
        Assert.Equal(3, buf[0]);
        Assert.Equal(4, buf[1]);
        Assert.Equal(5, buf[2]);
    }

    // ---------- 硬边 → 梯度 ----------

    [Fact]
    public void 硬边模糊后变成单调梯度且保留两侧平台()
    {
        const int w = 40, h = 6;
        var buf = new byte[w * h * Bpp];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(x < w / 2 ? 10 : 210);
                var i = (y * w + x) * Bpp;
                buf[i] = buf[i + 1] = buf[i + 2] = v; buf[i + 3] = 255;
            }

        BoxBlur.Apply(buf, w, h, radius: 3);

        var row = new int[w];
        for (var x = 0; x < w; x++) row[x] = At(buf, w, x, 0, 0);
        for (var x = 1; x < w; x++)
            Assert.True(row[x] >= row[x - 1], $"第 {x} 列亮度回落：{row[x - 1]} → {row[x]}（梯度必须单调）");
        // 平台端点精确保持：收缩窗口只把"图内"像素算进均值，不引入暗角/亮角，
        // 这正是选它而不选"边缘像素复制"的理由（复制法会把边缘值重复计数）。
        Assert.Equal(10, row[0]);
        Assert.Equal(210, row[w - 1]);
        Assert.True(row[w / 2] > 10 && row[w / 2] < 210);  // 原不连续点现在落在两平台之间
        Assert.True(row[w / 2] > row[w / 4]);              // 远离边缘处仍在向平台值收敛
    }

    // ---------- 均值 / 能量 ----------

    [Fact]
    public void 随机图模糊后全局均值几乎守恒()
    {
        const int w = 64, h = 48;
        var buf = Random(w, h, seed: 20261015);
        var before = (byte[])buf.Clone();
        var innerMean = Mean(buf, w, h, border: 0);

        BoxBlur.Apply(buf, w, h, radius: 4);

        // 整图均值：收缩窗口不吞也不重复计数外部质量 → 容差按"1/255 级"给，实测远小于此。
        Assert.True(Math.Abs(Mean(buf, w, h, 0) - innerMean) <= 1.0,
            $"全局均值漂移 {Mean(buf, w, h, 0) - innerMean:F4}");
        // 内部区域（去掉半径×趟数的边缘带）更严：中心区均值不受边界处理影响。
        Assert.True(Math.Abs(Mean(buf, w, h, 14) - Mean(before, w, h, 14)) <= 1.0);
        // 能量（方差意义下的平方和）必须**下降**：模糊是低通，能量不降就说明核写反了。
        Assert.True(SquaredSum(buf, w, h) < SquaredSum(before, w, h));
    }

    [Fact]
    public void 模糊只作用于蓝绿红_alpha通道逐字节不动()
    {
        const int w = 13, h = 7;
        var buf = Random(w, h, seed: 7);
        // 把 alpha 写成"看着就该被冲淡"的 0：若核误把第 4 通道当颜色平均，输出必变。
        for (var i = 3; i < buf.Length; i += Bpp) buf[i] = 0;
        var before = (byte[])buf.Clone();

        BoxBlur.Apply(buf, w, h, radius: 6);

        for (var i = 3; i < buf.Length; i += Bpp) Assert.Equal(before[i], buf[i]);
        // 半径 6 覆盖 13×7 的整张图，颜色通道必然被重算（若缓冲原样返回就是"核写成了恒等"）。
        Assert.NotEqual(before, buf);
    }

    // ---------- 越界防护 ----------

    [Fact]
    public void 半径大于宽高时不越界并退化为全局均值()
    {
        const int w = 7, h = 3;
        var buf = Random(w, h, seed: 99);
        // 半径 ≥ max(w,h) → 每行先塌成行均值、再塌成列均值；行长相等 ⇒ 全图收敛到同一个值 = 各通道均值。
        double[] expected = [Mean(buf, w, h, 0, 0), Mean(buf, w, h, 0, 1), Mean(buf, w, h, 0, 2)];

        BoxBlur.Apply(buf, w, h, radius: 1000, passes: 3);

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                    Assert.True(Math.Abs(At(buf, w, x, y, c) - expected[c]) <= 1.0,
                        $"({x},{y}) 通道 {c} = {At(buf, w, x, y, c)}，应≈全图均值 {expected[c]:F2}");
    }

    [Theory]
    [InlineData(0, 4, 4)]      [InlineData(4, 0, 4)]      [InlineData(-1, 4, 4)]
    [InlineData(4, 4, -2)]     [InlineData(4, 4, 0)]
    public void 非法尺寸或半径是no_op_不抛异常也不改字节(int r, int w, int h)
    {
        var buf = Constant(Math.Max(w, 1), Math.Max(h, 1), 1, 2, 3);
        var before = (byte[])buf.Clone();
        BoxBlur.Apply(buf, w, h, radius: r);          // 任何非法组合都必须安静返回
        Assert.Equal(before, buf);
    }

    [Fact]
    public void 缓冲长度不足时是no_op()
    {
        var buf = new byte[10 * 10 * Bpp - 5];        // 短于一张 10×10 图
        var before = (byte[])buf.Clone();
        BoxBlur.Apply(buf, 10, 10, radius: 3);
        Assert.Equal(before, buf);
    }

    // ---------- 降采样 ----------

    [Fact]
    public void 降采样常量图保持常量且输出尺寸向上取整()
    {
        const int w = 33, h = 19;                      // 不被 4 整除：尾块收缩路径必须走到
        var src = Constant(w, h, 11, 22, 33);
        var dst = BoxBlur.Downscale(src, w, h, 4, out var ow, out var oh);
        Assert.Equal(9, ow);                           // ceil(33/4)：向上取整保证映射不越出缓冲
        Assert.Equal(5, oh);
        Assert.Equal(9 * 5 * Bpp, dst.Length);
        for (var i = 0; i < dst.Length; i += Bpp)
        {
            Assert.Equal(11, dst[i]); Assert.Equal(22, dst[i + 1]);
            Assert.Equal(33, dst[i + 2]); Assert.Equal(255, dst[i + 3]);
        }
    }

    [Fact]
    public void 降采样保持硬边位置不移()
    {
        const int w = 32, h = 4;
        const int edge = 14;                    // 故意不与 factor(4) 对齐：必有一块跨在边上
        var src = new byte[w * h * Bpp];
        for (var i = 0; i < src.Length; i += Bpp) src[i + 3] = 255;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(x < edge ? 0 : 200);
                var j = (y * w + x) * Bpp;
                src[j] = src[j + 1] = src[j + 2] = v;
            }

        var dst = BoxBlur.Downscale(src, w, h, 4, out var ow, out _);

        Assert.Equal(8, ow);
        Assert.Equal(0, At(dst, ow, 0, 0, 0));         // 左平台
        Assert.Equal(200, At(dst, ow, ow - 1, 0, 0));  // 右平台
        Assert.Equal(0, At(dst, ow, 2, 0, 0));         // 第 2 块覆盖源列 8..11，全在边左
        Assert.Equal(100, At(dst, ow, 3, 0, 0));       // 第 3 块覆盖 12..15 = 两个 0 + 两个 200
        Assert.Equal(200, At(dst, ow, 4, 0, 0));       // 第 4 块覆盖 16..19，全在边右
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1)]
    public void 降采样对非法系数不退化为空图(int factor)
    {
        var src = Constant(6, 6, 1, 2, 3);
        var dst = BoxBlur.Downscale(src, 6, 6, factor, out var ow, out var oh);
        // factor<1 按 1 处理（原样复制）；factor=1 ⇒ 尺寸恒等，绝不是 0 尺寸（0 尺寸会把画刷炸成空白）。
        Assert.Equal(6, ow);
        Assert.Equal(6, oh);
        Assert.Equal(36 * Bpp, dst.Length);
    }

    [Fact]
    public void 降采样缓冲不足时返回空数组且不抛()
    {
        var dst = BoxBlur.Downscale(new byte[100], 100, 100, 4, out var ow, out var oh);
        Assert.Empty(dst);
        Assert.Equal(0, ow);
        Assert.Equal(0, oh);
    }

    // ---------- 工具 ----------

    private static byte[] Random(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var buf = new byte[w * h * Bpp];
        for (var i = 0; i < buf.Length; i += Bpp)
        {
            buf[i] = (byte)rnd.Next(256); buf[i + 1] = (byte)rnd.Next(256); buf[i + 2] = (byte)rnd.Next(256);
            buf[i + 3] = 255;
        }
        return buf;
    }

    /// <summary>指定通道在"去掉四周 border 圈"区域上的均值。</summary>
    private static double Mean(byte[] buf, int w, int h, int border, int channel = 0)
    {
        long sum = 0; long n = 0;
        for (var y = border; y < h - border; y++)
            for (var x = border; x < w - border; x++)
            { sum += At(buf, w, x, y, channel); n++; }
        return n == 0 ? 0 : (double)sum / n;
    }

    private static long SquaredSum(byte[] buf, int w, int h)
    {
        long s = 0;
        for (var i = 0; i < buf.Length; i += Bpp) s += (long)buf[i] * buf[i];
        return s;
    }
}
