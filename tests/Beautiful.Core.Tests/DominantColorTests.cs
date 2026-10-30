using System;
using Beautiful.Core.Imaging;
using Xunit;

/// <summary>
/// 取色纯函数取证（v3 T7）：全部用合成位图，因为"什么图该出什么色"是这套功能的契约，
/// 而真实壁纸天天变——用文件当断言基准等于把契约交给运气。
/// </summary>
public class DominantColorTests
{
    /// <summary>按 (R,G,B,A) 造一张纯色图（BGRA 字节序，与 WPF 的 Bgra32 一致）。</summary>
    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var buffer = new byte[width * height * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = a;
        }
        return buffer;
    }

    /// <summary>把两种颜色按比例铺进同一张图（前 share 比例是 c1，其余是 c2）。</summary>
    private static byte[] TwoTone(int pixels, double shareOfFirst,
                                 (byte R, byte G, byte B) c1, (byte R, byte G, byte B) c2)
    {
        var buffer = new byte[pixels * 4];
        var firstCount = (int)(pixels * shareOfFirst);
        for (var p = 0; p < pixels; p++)
        {
            var c = p < firstCount ? c1 : c2;
            var i = p * 4;
            buffer[i] = c.B;
            buffer[i + 1] = c.G;
            buffer[i + 2] = c.R;
            buffer[i + 3] = 255;
        }
        return buffer;
    }

    // ---------- 两色图取到"有彩度的那一簇" ----------

    [Fact]
    public void 多数是暗灰时仍然取到强调色()
    {
        // 60% 深灰 + 40% 红：只看"最常见"会取灰（画出来就是一块脏黑），乘上饱和度后红赢
        var buffer = TwoTone(1000, 0.6, (40, 40, 40), (200, 60, 60));
        Assert.True(DominantColor.TryExtract(buffer, 50, 20, out var accent));
        Assert.Equal(new RgbColor(200, 60, 60), accent);
    }

    [Fact]
    public void 高饱和的少数能赢过低饱和的多数()
    {
        // 70% 灰蓝（饱和度只有 0.17，接近"蒙了层灰"）+ 30% 鲜红：
        // 打分 = 像素数 ×(0.25 + 饱和度)，于是 300×1.076 > 700×0.417 —— 彩度足以翻盘但不至于胡来。
        var buffer = TwoTone(1000, 0.7, (100, 110, 120), (230, 40, 60));
        Assert.True(DominantColor.TryExtract(buffer, 50, 20, out var accent));
        Assert.Equal(new RgbColor(230, 40, 60), accent);
    }

    [Fact]
    public void 两簇都有彩度时更常见的那簇赢()
    {
        // 这条钉的是打分公式的另一半：彩度权重不是"一票否决"，大面积天空蓝不该被一小块噪点翻掉。
        var buffer = TwoTone(4000, 0.75, (60, 120, 200), (255, 140, 0));
        Assert.True(DominantColor.TryExtract(buffer, 100, 40, out var accent));
        Assert.Equal(new RgbColor(60, 120, 200), accent);
    }

    [Fact]
    public void 绿黑壁纸取到绿()
    {
        var buffer = TwoTone(900, 0.55, (20, 20, 20), (30, 180, 90));
        Assert.True(DominantColor.TryExtract(buffer, 30, 30, out var accent));
        Assert.Equal(new RgbColor(30, 180, 90), accent);
    }

    // ---------- 灰阶 / 极端输入 → 不取色 ----------

    [Fact]
    public void 灰阶图一律判不可取而不是画个灰强调色()
    {
        foreach (var gray in new byte[] { 0, 40, 128, 200, 255 })
        {
            var buffer = Solid(40, 40, gray, gray, gray);
            Assert.False(DominantColor.TryExtract(buffer, 40, 40, out var accent), $"{gray} 灰不该出结果");
            Assert.Equal(default, accent);   // 失败时调用方必须走回落，不能拿这个值去画
        }
    }

    [Fact]
    public void 近白的壁纸也不取()
    {
        // 白底壁纸（250,250,250）：饱和度 0 → 回落主题强调色，否则 Dock 高亮会画成一片白
        Assert.False(DominantColor.TryExtract(Solid(40, 40, 250, 250, 250), 40, 40, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void 面积不足的输入判不可取(int width, int height)
    {
        var buffer = Solid(Math.Max(1, width * height), 1, 200, 60, 60);
        Assert.False(DominantColor.TryExtract(buffer, width, height, out var accent));
        Assert.Equal(default, accent);
    }

    [Fact]
    public void 缓冲长度不足时不越界也不取色()
    {
        var tooShort = Solid(4, 4, 200, 60, 60);   // 只有 16 像素，却声称 8×8
        Assert.False(DominantColor.TryExtract(tooShort, 8, 8, out _));
        Assert.False(DominantColor.TryExtract(ReadOnlySpan<byte>.Empty, 0, 0, out _));
    }

    [Fact]
    public void 全透明像素等于没有数据()
    {
        var buffer = Solid(20, 20, 200, 60, 60, a: 0);
        Assert.False(DominantColor.TryExtract(buffer, 20, 20, out _));
    }

    // ---------- 钳制与确定性 ----------

    [Fact]
    public void 极暗的彩色簇被抬到可读明度而色相不变()
    {
        // (60,10,10) 太暗，画在深色玻璃上就是一块黑；抬明度后仍必须是"红"
        Assert.True(DominantColor.TryExtract(Solid(40, 40, 60, 10, 10), 40, 40, out var accent));
        Assert.True(accent.R > accent.G && accent.R > accent.B, $"色相必须是红系，实际 {accent.ToHex()}");
        Assert.True(Math.Max(accent.R, Math.Max(accent.G, accent.B)) >= 80, $"明度不够，实际 {accent.ToHex()}");
    }

    [Fact]
    public void 取色结果可复现且大图中只抽稀()
    {
        // 4K 级输入也只在 MaxSamples² 内抽稀，因此同一张图恒得同一个颜色（写进界面的东西不许抖），
        // 而且取到的就是主色簇的原始均值 —— 没有被"猜成别的颜色"。
        var buffer = TwoTone(1920 * 1080, 0.85, (220, 90, 40), (30, 60, 120));
        Assert.True(DominantColor.TryExtract(buffer, 1920, 1080, out var first));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(DominantColor.TryExtract(buffer, 1920, 1080, out var again));
            Assert.Equal(first, again);
        }
        Assert.Equal("#DC5A28", first.ToHex());
    }

    [Fact]
    public void 强调色的Hex形式与主题定义同格式()
        => Assert.Equal("#0A84FF", new RgbColor(0x0A, 0x84, 0xFF).ToHex());
}
