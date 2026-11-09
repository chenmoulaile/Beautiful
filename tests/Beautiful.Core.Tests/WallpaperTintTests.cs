using System;
using Beautiful.Core.Imaging;
using Xunit;

/// <summary>
/// 墙纸取色管线的纯函数取证：全部用合成位图（与 DominantColorTests 同一理由——
/// "什么图该出什么色"是这套功能的契约，真实壁纸天天变，文件当断言基准等于把契约交给运气）。
/// <para>
/// 期望值由算法的逐行转写复算钉死（见工作包记录）：tint 的具体 hex 由
/// ToHsl/FromHsl/银行家舍入共同决定，手算到不了字节级，断言只锁复算结果——
/// 复算与实现一旦分叉（谁改了公式没改另一边），这里第一时间红。
/// </para>
/// </summary>
public class WallpaperTintTests
{
    /// <summary>Aurora 暗色玻璃基色（AuroraTokens.Dark.GlassBase = "#1E2027"）。</summary>
    private static readonly RgbColor AuroraGlass = new(0x1E, 0x20, 0x27);

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

    // ---------- 完整管线：均匀彩图 ----------

    [Fact]
    public void 均匀红壁纸派生出暗红tint且强度入alpha()
    {
        // 期望值由转写复算钉死：主色 (200,60,60) 过饱和度闸，tint 明度贴玻璃基色 #1E2027，
        // 饱和度 = glass(0.130) + HSL主色(0.560)×0.35 ≈ 0.326 → FromHsl(0°, 0.326, 0.135) = #2E1717。
        Assert.True(WallpaperTint.TryCompute(Solid(40, 40, 200, 60, 60), 40, 40, AuroraGlass, 60, out var result));
        Assert.Equal(new RgbColor(200, 60, 60), result.Primary);
        Assert.Equal(result.Primary, result.Secondary);          // 单色图只有一个簇 → 辅色同主色
        Assert.Equal(153, result.Alpha);                          // 60/100 × 255 = 153
        Assert.Equal("#992E1717", result.TintHex);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0, "#002E1717")]
    [InlineData(50, "#802E1717")]   // 127.5 → 银行家舍入 128
    [InlineData(100, "#FF2E1717")]
    public void 强度滑杆线性映射进tint的alpha通道(int strength, string expectedHex)
    {
        Assert.True(WallpaperTint.TryCompute(Solid(40, 40, 200, 60, 60), 40, 40, AuroraGlass, strength, out var result));
        Assert.Equal(expectedHex, result.TintHex);
        Assert.Equal(strength != 0, result.IsValid);   // alpha=0 即"算出了色也等于关"
    }

    [Fact]
    public void 强度越界值先钳制再映射()
    {
        Assert.Equal(0, WallpaperTint.ClampStrength(-5));
        Assert.Equal(100, WallpaperTint.ClampStrength(140));
        Assert.Equal(42, WallpaperTint.ClampStrength(42));
    }

    // ---------- 完整管线：k-means 主/辅色 ----------

    [Fact]
    public void 双色图主辅色各归其簇()
    {
        // 75% 蓝 + 25% 橙：蓝的"像素数×(0.25+饱和度)"得分远高于橙 → 蓝 = 主色、橙 = 辅色。
        Assert.True(WallpaperTint.TryCompute(TwoTone(64 * 64, 0.75, (60, 120, 200), (255, 140, 0)), 64, 64,
            AuroraGlass, 60, out var result));
        Assert.Equal(new RgbColor(60, 120, 200), result.Primary);
        Assert.Equal(new RgbColor(255, 140, 0), result.Secondary);
    }

    [Fact]
    public void 五五开的红青并列时取桶号小的青作主色()
    {
        // 红青得分完全相同（数量与饱和度对称）→ 打分并列取桶号小者（青 #28C8DC 的量化桶号小于红）。
        // 这条钉的是"并列必须可复现"：换个遍历方向结果就得一样。
        Assert.True(WallpaperTint.TryCompute(TwoTone(64 * 64, 0.5, (220, 40, 40), (40, 200, 220)), 64, 64,
            AuroraGlass, 60, out var result));
        Assert.Equal(new RgbColor(40, 200, 220), result.Primary);
        Assert.Equal(new RgbColor(220, 40, 40), result.Secondary);
    }

    [Fact]
    public void 取色结果可复现且跨尺寸一致()
    {
        // 同一张 4K 图跑四遍（两遍原尺寸 + 两遍降采样路径）逐字节相同——tint 写进界面的东西不许抖。
        var buffer = TwoTone(1920 * 1080, 0.85, (220, 90, 40), (30, 60, 120));
        Assert.True(WallpaperTint.TryCompute(buffer, 1920, 1080, AuroraGlass, 60, out var first));
        Assert.Equal(new RgbColor(220, 90, 40), first.Primary);   // 与 DominantColorTests 同图同主色（#DC5A28）
        for (var i = 0; i < 3; i++)
        {
            Assert.True(WallpaperTint.TryCompute(buffer, 1920, 1080, AuroraGlass, 60, out var again));
            Assert.Equal(first, again);
        }

        // 降采样到 64 边以内后仍取同一主色（框平均不改变"哪簇是主色"）
        Assert.True(WallpaperTint.TryCompute(buffer, 1920, 1080, AuroraGlass, 60, out var small));
        Assert.Equal(first.Primary, small.Primary);
        Assert.Equal(first.TintHex, small.TintHex);
    }

    [Fact]
    public void kmeans聚类内部直接调用也确定()
    {
        var buffer = TwoTone(64 * 64, 0.6, (180, 60, 60), (40, 90, 160));
        Assert.True(WallpaperTint.ExtractPalette(buffer, 64, 64, out var primary1, out var secondary1));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(WallpaperTint.ExtractPalette(buffer, 64, 64, out var primary2, out var secondary2));
            Assert.Equal(primary1, primary2);
            Assert.Equal(secondary1, secondary2);
        }

        Assert.Equal(new RgbColor(180, 60, 60), primary1);    // 60% 的高饱和红簇得分更高
        Assert.Equal(new RgbColor(40, 90, 160), secondary1);
    }

    // ---------- 灰阶 / 极端输入 → 不取色 ----------

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(128)]
    [InlineData(250)]
    public void 灰阶壁纸判不可取而不是给玻璃上灰tint(byte gray)
    {
        Assert.False(WallpaperTint.TryCompute(Solid(40, 40, gray, gray, gray), 40, 40, AuroraGlass, 60, out var result));
        Assert.Equal(default, result);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void 低彩度蓝灰壁纸过闸但tint只轻微着色()
    {
        // (100,110,120) HSV 饱和度 ≈ 0.167，刚过 0.15 的闸（大面积混色的闸比 DominantColor 的
        // 强调色闸紧）：派生 tint 的饱和度 = glass + 主色 HSL 饱和度×0.35 ≈ 0.16 → 只是蓝灰微调，
        // 绝不是把玻璃刷成蓝色（期望值由转写复算钉死）。
        Assert.True(WallpaperTint.TryCompute(Solid(40, 40, 100, 110, 120), 40, 40, AuroraGlass, 60, out var result));
        Assert.Equal(new RgbColor(100, 110, 120), result.Primary);
        Assert.Equal("#991D2228", result.TintHex);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void 面积不足的输入判不可取(int width, int height)
    {
        var buffer = Solid(Math.Max(1, width * height), 1, 200, 60, 60);
        Assert.False(WallpaperTint.TryCompute(buffer, width, height, AuroraGlass, 60, out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void 缓冲长度不足时不越界也不取色()
    {
        var tooShort = Solid(4, 4, 200, 60, 60);   // 只有 16 像素，却声称 8×8
        Assert.False(WallpaperTint.TryCompute(tooShort, 8, 8, AuroraGlass, 60, out _));
        Assert.False(WallpaperTint.TryCompute(ReadOnlySpan<byte>.Empty, 0, 0, AuroraGlass, 60, out _));
    }

    [Fact]
    public void 全透明像素等于没有数据()
    {
        Assert.False(WallpaperTint.TryCompute(Solid(20, 20, 200, 60, 60, a: 0), 20, 20, AuroraGlass, 60, out _));
    }

    // ---------- ① 降采样 ----------

    [Theory]
    [InlineData(1920, 1080, 64, 36)]
    [InlineData(1080, 1920, 36, 64)]
    [InlineData(64, 64, 64, 64)]
    [InlineData(50, 30, 50, 30)]     // 源不超限不放大
    [InlineData(65, 30, 64, 30)]
    [InlineData(100, 50, 64, 32)]
    public void 降采样目标尺寸按比例钳到最长边(int w, int h, int ew, int eh)
        => Assert.Equal((ew, eh), WallpaperTint.DownsampledSize(w, h));

    [Fact]
    public void 框平均取块内不透明像素的通道均值()
    {
        // 4×2 → 2×1：左块 4 px 全 (100,0,0)、右块 4 px 全 (200,0,0) → 每块均值精确等于原色。
        var source = new byte[4 * 2 * 4];
        FillBlock(source, 4, 0, 0, 2, 2, 100, 0, 0);
        FillBlock(source, 4, 2, 0, 2, 2, 200, 0, 0);
        var destination = new byte[2 * 1 * 4];
        WallpaperTint.Downsample(source, 4, 2, destination, 2, 1);
        Assert.Equal(100, destination[2]); Assert.Equal(0, destination[1]); Assert.Equal(0, destination[0]);
        Assert.Equal(200, destination[6]); Assert.Equal(0, destination[5]); Assert.Equal(0, destination[4]);
        Assert.Equal(255, destination[3]); Assert.Equal(255, destination[7]);
    }

    [Fact]
    public void 框平均混色块取整除均值且透明像素不参与()
    {
        // 2×2：三格 (10,20,30) + 右下一格 (20,40,60)。BGRA 字节序下整除截断：
        // B=(30×3+60)/4=37、G=(20×3+40)/4=25、R=(10×3+20)/4=12。
        var source = new byte[2 * 2 * 4];
        FillBlock(source, 2, 0, 0, 2, 2, 10, 20, 30);
        FillBlock(source, 2, 1, 1, 1, 1, 20, 40, 60);
        var destination = new byte[1 * 1 * 4];
        WallpaperTint.Downsample(source, 2, 2, destination, 1, 1);
        Assert.Equal(37, destination[0]);   // B
        Assert.Equal(25, destination[1]);   // G
        Assert.Equal(12, destination[2]);   // R
        Assert.Equal(255, destination[3]);
    }

    [Fact]
    public void 全透明块降采样后输出透明()
    {
        var source = Solid(4, 4, 200, 60, 60, a: 0);
        var destination = new byte[1 * 1 * 4];
        WallpaperTint.Downsample(source, 4, 4, destination, 1, 1);
        Assert.Equal(0, destination[3]);   // A=0：提色阶段按透明跳过
    }

    /// <summary>把 (R,G,B) 实心填进源的 [x0,x0+w)×[y0,y0+h) 矩形（BGRA 字节序）。</summary>
    private static void FillBlock(byte[] buffer, int stride, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
            {
                var i = (y * stride + x) * 4;
                buffer[i] = b;
                buffer[i + 1] = g;
                buffer[i + 2] = r;
                buffer[i + 3] = 255;
            }
    }

    // ---------- ③ tint 派生（HSL 调玻璃底色相/饱和度） ----------

    [Fact]
    public void 派生tint的明度贴玻璃基色而饱和度被壁纸顶起()
    {
        var tint = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(200, 60, 60), new RgbColor(200, 60, 60));
        var (_, _, glassLightness) = WallpaperTint.ToHsl(AuroraGlass);
        var (_, tintSaturation, tintLightness) = WallpaperTint.ToHsl(tint);
        Assert.True(Math.Abs(tintLightness - glassLightness) < 0.02,
            $"明度必须贴玻璃基色（往返误差 ≤ byte 量化），glass {glassLightness:0.###} vs tint {tintLightness:0.###}");
        Assert.True(tintSaturation > WallpaperTint.ToHsl(AuroraGlass).S, "壁纸饱和度必须把玻璃基色往上顶");
        Assert.True(tint.R > tint.G && tint.R > tint.B, $"色相必须是红系，实际 {tint.ToHex()}");
    }

    [Fact]
    public void 派生tint的饱和度封顶在玻璃可承受的范围()
    {
        // 荧光绿 (0,255,0)：主色 HSL 饱和度 1.0 → glass + 0.35 直冲上限，必须被钳到 0.45。
        var tint = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(0, 255, 0), new RgbColor(0, 255, 0));
        var (_, saturation, _) = WallpaperTint.ToHsl(tint);
        Assert.True(saturation <= WallpaperTint.MaxTintSaturation + 1e-9, $"饱和度必须 ≤ 0.45，实际 {saturation:0.###}");
        Assert.True(tint.G > tint.R && tint.G > tint.B, $"色相必须是绿系，实际 {tint.ToHex()}");
    }

    [Fact]
    public void 辅色只在色相差足够大时参与且偏移有封顶()
    {
        // 主色红 0°、辅色蓝 240°：Δ=−120（最短方向）×0.30=−36，不触 40° 上限 → hue ≈ 324°。
        var tint = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(220, 30, 30), new RgbColor(30, 30, 220));
        Assert.Equal(324, WallpaperTint.ToHsl(tint).H, 0);

        // 主色红 0°、辅色青 180°：Δ=+180（半开区间 (−180,180] 的右端）×0.30=+54 → 触顶钳到 +40。
        var clamped = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(220, 30, 30), new RgbColor(30, 220, 220));
        Assert.Equal(40, WallpaperTint.ToHsl(clamped).H, 0);

        // 辅色黄绿 72.6°：Δ=+72.6 ≥ 30 参与，×0.30=+21.8 → 主色被拉到 ≈ 22°。
        var blended = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(220, 30, 30), new RgbColor(180, 220, 30));
        Assert.Equal(22, WallpaperTint.ToHsl(blended).H, 0);

        // 辅色橙 28.4°：Δ=+28.4 < 30 → 不参与（近色相辅色混进来只是噪声），hue 保持主色 0°。
        var untouched = WallpaperTint.DeriveTint(AuroraGlass, new RgbColor(220, 30, 30), new RgbColor(220, 120, 30));
        Assert.Equal(0, WallpaperTint.ToHsl(untouched).H, 0);
    }

    [Fact]
    public void 色相最短方向差归一化到半开区间()
    {
        Assert.Equal(140, WallpaperTint.HueDelta(220, 0), 6);      // 220° → 0°：顺时针 140°
        Assert.Equal(-140, WallpaperTint.HueDelta(0, 220), 6);     // 0° → 220°：逆时针 140°
        Assert.Equal(20, WallpaperTint.HueDelta(350, 10), 6);      // 跨 0° 边界
        Assert.Equal(-20, WallpaperTint.HueDelta(10, 350), 6);
        Assert.Equal(180, WallpaperTint.HueDelta(0, 180), 6);      // 正对面：归一化到 +180（区间右端）
        Assert.Equal(0, WallpaperTint.HueDelta(90, 90), 6);
    }

    // ---------- ④ hex 工具 ----------

    [Theory]
    [InlineData("#FF0A84FF", 0x0A, 0x84, 0xFF, 0xFF)]
    [InlineData("#0A84FF", 0x0A, 0x84, 0xFF, 0xFF)]
    [InlineData("#00d4693b", 0xD4, 0x69, 0x3B, 0x00)]
    public void 十六进制颜色串按AARRGGBB与RRGGBB两形态解析(string hex, byte r, byte g, byte b, byte a)
    {
        Assert.True(WallpaperTint.TryParseHex(hex, out var pr, out var pg, out var pb, out var pa));
        Assert.Equal((r, g, b, a), (pr, pg, pb, pa));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0A84FF")]     // 缺 #
    [InlineData("#0A84F")]     // 位数不对
    [InlineData("#0A84FF0")]   // 位数不对
    [InlineData("#0A84ZZ")]
    public void 非法颜色串一律解析失败(string? hex)
        => Assert.False(WallpaperTint.TryParseHex(hex, out _, out _, out _, out _));

    [Fact]
    public void 混色权重为零时逐字节恒等于基色()
    {
        Assert.Equal("#1E2027", WallpaperTint.MixHex("#1E2027", "#D4693B", 0));
        Assert.Equal("#C838383A", WallpaperTint.MixHex("#C838383A", "#2E1717", 0));   // macOS 三停关态恒等（含 alpha）
        Assert.Equal("#B8242426", WallpaperTint.MixHex("#B8242426", "#2E1717", 0));
        Assert.Equal("#A8161618", WallpaperTint.MixHex("#A8161618", "#2E1717", 0));
    }

    [Fact]
    public void 混色保留基色的alpha而权重显式传入()
    {
        // 半权混白入黑：RGB (128,128,128)，基色 alpha 0xC8 原样保留。
        Assert.Equal("#C8808080", WallpaperTint.MixHex("#C8000000", "#FFFFFF", 0.5));
        // 全权混入 = tint 的 RGB + 基色的 alpha。
        Assert.Equal("#C8D4693B", WallpaperTint.MixHex("#C8000000", "#FFD4693B", 1.0));
        // tint 自己的 alpha 不参与（权重是调用方的事，tint 只出 RGB）。
        Assert.Equal("#808080", WallpaperTint.MixHex("#000000", "#80FFFFFF", 0.5));
    }

    [Fact]
    public void 混色权重越界钳制与解析失败回落()
    {
        Assert.Equal("#1E2027", WallpaperTint.MixHex("#1E2027", "not-a-color", 0.5));   // tint 解析失败 → 基色原样
        Assert.Equal("#FFFFFF", WallpaperTint.MixHex("#1E2027", "#FFFFFF", 1.5));       // 权重越界钳到 1 = 全量混白
        Assert.Equal("#C838383A", WallpaperTint.MixHex("#C838383A", "#2E1717", -0.1));  // 负权重钳到 0 → 恒等
    }
}
