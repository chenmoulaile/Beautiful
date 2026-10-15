using Beautiful.Core.Imaging;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;
using Xunit;

namespace Beautiful.Core.Tests;

/// <summary>
/// v3 T6 图标质量管线的可单测部分：遮罩形状、烘焙参数的钳制与派生算术、缓存键的区分度、
/// 设置项到实际参数的解释、alpha 模糊。像素绘制（WPF RenderTargetBitmap）只能在 App 层做，
/// 由截图验收；这里锁的是"画多大、画在哪、留多少投影空间、什么时候算同一张缓存"。
/// </summary>
public class IconBakeSpecTests
{
    // ---------- 钳制 ----------

    [Fact]
    public void 非法Dpi一律回落一倍缩放()
    {
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = 0 }.Scale, 6);
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = -2 }.Scale, 6);
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = double.NaN }.Scale, 6);
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = double.PositiveInfinity }.Scale, 6);
        // 超出 [0.25,4] 的荒谬值也回落（而不是钳到边界：4.5 会让缓存内存超出计划，0.1 会画成 4px）
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = 4.5 }.Scale, 6);
        Assert.Equal(1.0, new IconBakeSpec { IconSizeDip = 44, DpiScale = 0.1 }.Scale, 6);
        Assert.Equal(1.5, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.5 }.Scale, 6);
    }

    [Fact]
    public void 图块边长等于DIP乘缩放且恒不小于一()
    {
        Assert.Equal(44, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }.TilePx);
        Assert.Equal(66, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.5 }.TilePx);   // 150% 真实渲染需求
        Assert.Equal(88, new IconBakeSpec { IconSizeDip = 44, DpiScale = 2.0 }.TilePx);   // 200%
        Assert.Equal(144, new IconBakeSpec { IconSizeDip = 72, DpiScale = 2.0 }.TilePx);  // 设置项上限 @200%
        Assert.Equal(1, new IconBakeSpec { IconSizeDip = 0, DpiScale = 1.0 }.TilePx);     // 绝不产生 0 尺寸位图
        Assert.Equal(1, new IconBakeSpec { IconSizeDip = -9, DpiScale = 1.0 }.TilePx);
    }

    [Fact]
    public void 图块边长有天花板()
    {
        var spec = new IconBakeSpec { IconSizeDip = 4000, DpiScale = 4.0 };
        Assert.Equal(IconBakeSpec.MaxTilePx, spec.TilePx);
    }

    [Fact]
    public void 超采样倍数钳在一到四()
    {
        Assert.Equal(1, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Supersample = 0 }.SupersampleFactor);
        Assert.Equal(1, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Supersample = -5 }.SupersampleFactor);
        Assert.Equal(4, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Supersample = 99 }.SupersampleFactor);
        Assert.Equal(IconBakeSpec.DefaultSupersample,
                     new IconBakeSpec { IconSizeDip = 44, DpiScale = 1 }.SupersampleFactor);
    }

    [Fact]
    public void 超采样不超过源能供给的倍率()
    {
        var spec = new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.5 };   // 图块 66
        Assert.Equal(2, spec.SupersampleForSource(256));                    // 256/66=3 → 仍按期望的 2×
        Assert.Equal(1, spec.SupersampleForSource(32));                     // 末位兜底的小图：先放大再缩小只会更糊
        Assert.Equal(2, spec.SupersampleForSource(0));                      // 未知源尺寸 → 按期望倍数（不做无根据的降级）
        var big = new IconBakeSpec { IconSizeDip = 72, DpiScale = 2.0 };    // 图块 144
        Assert.Equal(1, big.SupersampleForSource(256));                     // 256/144=1 → 一步到位
    }

    // ---------- 投影空间 ----------

    [Theory]
    [InlineData(44, 1.0)]
    [InlineData(44, 1.5)]
    [InlineData(44, 2.0)]
    [InlineData(72, 2.0)]
    public void 投影装得下且artwork不小于下限(int dip, double dpi)
    {
        var spec = new IconBakeSpec { IconSizeDip = dip, DpiScale = dpi, Shadow = true, Highlight = true,
                                      Mask = IconMaskStyle.RoundedRectangle, MaskRadiusRatio = 0.2 };
        // 被下沿切掉的投影会变成一条直线，比没有投影更"劣质" → 这是本任务要守住的观感不变量
        Assert.True(spec.ShadowFits, $"图块 {spec.TilePx}px 时投影装不下：偏移{spec.ShadowOffsetPx} 余量{spec.ShadowBottomRoomPx}");
        Assert.True(spec.ArtworkPx >= Math.Ceiling(spec.TilePx * IconBakeSpec.MinArtworkRatio));
        // 水平居中（左右留白相等），垂直方向留白主要给下缘的投影
        Assert.Equal((spec.TilePx - spec.ArtworkPx) / 2.0, spec.ArtworkLeftPx, 6);
        Assert.True(spec.ArtworkTopPx < spec.ShadowBottomRoomPx);
    }

    [Fact]
    public void 关掉投影时artwork就是整个图块()
    {
        var spec = new IconBakeSpec { IconSizeDip = 44, DpiScale = 2.0, Shadow = false, Highlight = false };
        Assert.Equal(0, spec.ShadowOffsetPx);
        Assert.Equal(0, spec.ShadowBlurPx);
        Assert.Equal(spec.TilePx, spec.ArtworkPx);
        Assert.Equal(0, spec.ArtworkTopPx);
        Assert.False(spec.ShadowFits);
        Assert.True(spec.GeometryOnly);
    }

    [Fact]
    public void 有形状或有高光就不是纯几何()
    {
        Assert.False(new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Mask = IconMaskStyle.Squircle }.GeometryOnly);
        Assert.False(new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Highlight = true }.GeometryOnly);
        Assert.True(new IconBakeSpec { IconSizeDip = 44, DpiScale = 1 }.GeometryOnly);
    }

    [Fact]
    public void 高光厚度至少一像素且关高光时为零()
    {
        Assert.Equal(0, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Highlight = false }.HighlightPx);
        // 极小图块也要有可见的一行高光，否则"内高光"这个参数就成了空话
        Assert.Equal(1, new IconBakeSpec { IconSizeDip = 8, DpiScale = 1, Highlight = true }.HighlightPx);
        Assert.True(new IconBakeSpec { IconSizeDip = 72, DpiScale = 2, Highlight = true }.HighlightPx >= 3);
    }

    // ---------- 缓存键 ----------

    [Fact]
    public void 缓存键同参数同值()
    {
        var spec = new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.5 };
        var a = IconBakeKey.Create(@"C:\Edge\msedge.exe", spec, "MacOS", "auto");
        var b = IconBakeKey.Create(@"c:\edge\MsEdge.EXE", spec, "macos", "AUTO");
        Assert.Equal(a, b);   // Windows 路径大小写不敏感 + 标识规范形 → 必须是同一项，否则切一次主题就把缓存填两份
    }

    [Fact]
    public void 缓存键区分DPI_尺寸_主题_方式_源()
    {
        var baseKey = IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }, "MacOS", "auto");
        Assert.NotEqual(baseKey, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.5 }, "MacOS", "auto"));
        Assert.NotEqual(baseKey, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 64, DpiScale = 1.0 }, "MacOS", "auto"));
        Assert.NotEqual(baseKey, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }, "HarmonyOS", "auto"));
        Assert.NotEqual(baseKey, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }, "MacOS", "plain"));
        Assert.NotEqual(baseKey, IconBakeKey.Create("b.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }, "MacOS", "auto"));
    }

    [Fact]
    public void 缓存键用合法化后的DPI()
    {
        // 非法 DPI（0/NaN）在 Scale 里回落 1.0；若键里存原始值，同一屏会造出两个键、两份位图
        var ok = IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1.0 }, "MacOS", "auto");
        Assert.Equal(ok, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = 0 }, "MacOS", "auto"));
        Assert.Equal(ok, IconBakeKey.Create("a.exe", new IconBakeSpec { IconSizeDip = 44, DpiScale = double.NaN }, "MacOS", "auto"));
    }

    [Fact]
    public void 缓存键容忍null源并trim空白()
    {
        var k = IconBakeKey.Create(null, new IconBakeSpec { IconSizeDip = 44, DpiScale = 1 }, null, null);
        Assert.Equal(string.Empty, k.IconSource);
        Assert.Equal(k, IconBakeKey.Create("   ", new IconBakeSpec { IconSizeDip = 44, DpiScale = 1 }, "  ", " "));
    }
}

/// <summary>遮罩形状：主题数据 → 归一化点位 / 半径的钳制（形状属主唯一性由 IconTreatmentTests 锁）。</summary>
public class IconMaskGeometryTests
{
    [Fact]
    public void squircle单位点落在单位方框内且数量与采样一致()
    {
        var pts = IconBake.SquircleUnitPoints();
        Assert.Equal(IconBake.SquircleSamples, pts.Count);
        foreach (var (x, y) in pts)
        {
            Assert.InRange(x, -1e-9, 1 + 1e-9);
            Assert.InRange(y, -1e-9, 1 + 1e-9);
        }
    }

    [Fact]
    public void squircle四个轴向端点贴着单位方框()
    {
        var pts = IconBake.SquircleUnitPoints();
        Assert.Equal(1.0, pts[0].X, 6);      // θ=0
        Assert.Equal(0.5, pts[0].Y, 6);
        Assert.Equal(0.0, pts[48].X, 4);     // θ=π
        Assert.Equal(1.0, pts[24].Y, 6);     // θ=π/2
    }

    [Fact]
    public void squircle比圆更饱满_因此与既有ThemeIconClip同观感()
    {
        // n=4.4 > 2 ⇒ 45° 方向上比圆鼓出来；这正是"连续圆角"的判别式，也是它不同于 RectangleGeometry 的地方
        var pts = IconBake.SquircleUnitPoints();
        var x45 = pts[12].X;                       // θ=π/4
        Assert.True(x45 > 0.5 + 0.5 * Math.Sqrt(0.5), $"45° 点 x={x45:0.###} 应比圆的 0.8536 更靠外");
        Assert.True(x45 < 1.0);
    }

    [Fact]
    public void squircle关于中心对称()
    {
        var pts = IconBake.SquircleUnitPoints();
        for (var i = 0; i < pts.Count; i++)
        {
            var mirror = pts[(i + pts.Count / 2) % pts.Count];   // θ → θ+π
            Assert.Equal(1 - pts[i].X, mirror.X, 6);
            Assert.Equal(1 - pts[i].Y, mirror.Y, 6);
        }
    }

    [Fact]
    public void 像素映射按矩形缩放不平移错()
    {
        var px = IconBake.ToPixels(IconBake.SquircleUnitPoints(), 10, 20, 100, 100);
        Assert.Equal(110, px[0].X, 6);   // 单位点 (1,0.5) → (10+100, 20+50)
        Assert.Equal(70, px[0].Y, 6);
    }

    [Fact]
    public void 半径比例钳到零到半点边()
    {
        Assert.Equal(0.2, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.2), 6);
        Assert.Equal(IconBake.MaxMaskRadiusRatio, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, 0.9), 6);
        Assert.Equal(0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, -1), 6);
        Assert.Equal(0, IconBake.ClampRadiusRatio(IconMaskStyle.RoundedRectangle, double.NaN), 6);
        // None 就是"没有形状"，别让残留的 ratio 生效
        Assert.Equal(0, IconBake.ClampRadiusRatio(IconMaskStyle.None, 0.25), 6);
        // Squircle 的"圆角"由超椭圆指数决定：ClampRadiusRatio 只管数值域，"哪种形状用半径"是 RadiusPx 的决定
        Assert.Equal(0.25, IconBake.ClampRadiusRatio(IconMaskStyle.Squircle, 0.25), 6);
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.Squircle, 0.25, 66));
    }

    [Fact]
    public void 半径像素按artwork边长取整()
    {
        Assert.Equal(13, IconBake.RadiusPx(IconMaskStyle.RoundedRectangle, 0.2, 66));    // floor(13.2)
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.Squircle, 0.2, 66));
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.None, 0.2, 66));
        Assert.Equal(0, IconBake.RadiusPx(IconMaskStyle.RoundedRectangle, 0.2, 0));      // 退化尺寸不炸
    }

    [Fact]
    public void 烘焙半径随图块增大()
    {
        var small = new IconBakeSpec { IconSizeDip = 32, DpiScale = 1, Mask = IconMaskStyle.RoundedRectangle, MaskRadiusRatio = 0.2, Shadow = true };
        var large = new IconBakeSpec { IconSizeDip = 72, DpiScale = 2, Mask = IconMaskStyle.RoundedRectangle, MaskRadiusRatio = 0.2, Shadow = true };
        Assert.True(large.RadiusPx > small.RadiusPx);
        Assert.Equal((int)Math.Floor(small.ArtworkPx * 0.2), small.RadiusPx);
    }
}

/// <summary>设置项 IconTreatment → 实际烘焙参数（含"形状单一属主"不变量）。</summary>
public class IconTreatmentTests
{
    [Fact]
    public void auto_按主题给形状与投影()
    {
        var mac = IconTreatment.Resolve("auto", ThemeDefinition.MacOs().IconMask,
                                         ThemeDefinition.MacOs().IconMaskRadiusRatio, ThemeDefinition.MacOs().IconShadow);
        Assert.Equal(IconMaskStyle.RoundedRectangle, mac.Mask);
        Assert.True(mac.Shadow);
        Assert.True(mac.Highlight);
        Assert.True(mac.BakesMask);     // macOS 的形状归烘焙位图

        var harmony = IconTreatment.Resolve("auto", ThemeDefinition.HarmonyOs().IconMask,
                                            ThemeDefinition.HarmonyOs().IconMaskRadiusRatio, ThemeDefinition.HarmonyOs().IconShadow);
        Assert.Equal(IconMaskStyle.Squircle, harmony.Mask);
        Assert.True(harmony.Shadow);
        Assert.True(harmony.BakesMask);

        // GNOME dash 平铺：无遮罩、无投影、形状属主留在 XAML 的 ThemeIconClip
        var linux = IconTreatment.Resolve("auto", ThemeDefinition.Linux().IconMask,
                                          ThemeDefinition.Linux().IconMaskRadiusRatio, ThemeDefinition.Linux().IconShadow);
        Assert.Equal(IconMaskStyle.None, linux.Mask);
        Assert.False(linux.Shadow);
        Assert.False(linux.Highlight);
        Assert.False(linux.BakesMask);
    }

    [Fact]
    public void mask_主题没给形状时也给通用圆角()
    {
        var forced = IconTreatment.Resolve("mask", IconMaskStyle.None, 0, false);
        Assert.Equal(IconMaskStyle.RoundedRectangle, forced.Mask);
        Assert.Equal(IconTreatment.FallbackRadiusRatio, forced.RadiusRatio, 6);
        Assert.True(forced.Shadow);
        Assert.True(forced.BakesMask);
        // 主题已经有形状时不覆盖它（鸿蒙仍应是 squircle，不是被换成圆角方块）
        Assert.Equal(IconMaskStyle.Squircle, IconTreatment.Resolve("mask", IconMaskStyle.Squircle, 0, false).Mask);
    }

    [Fact]
    public void plain_什么都不烧_形状交回XAML()
    {
        var plain = IconTreatment.Resolve("plain", IconMaskStyle.Squircle, 0.25, true);
        Assert.Equal(IconMaskStyle.None, plain.Mask);
        Assert.False(plain.Shadow);
        Assert.False(plain.Highlight);
        Assert.False(plain.BakesMask);
    }

    [Fact]
    public void 未知或大小写写法都归一()
    {
        Assert.Equal("auto", IconTreatment.Canonical(null));
        Assert.Equal("auto", IconTreatment.Canonical("  "));
        Assert.Equal("auto", IconTreatment.Canonical("MASK2"));
        Assert.Equal("mask", IconTreatment.Canonical("  MaSk "));
        Assert.Equal("plain", IconTreatment.Canonical("PLAIN"));
        // 未知值走 auto 而不是报错（与 AppSettings 的其它枚举串同一纪律）
        Assert.True(IconTreatment.Resolve("乱写", IconMaskStyle.Squircle, 0, true).BakesMask);
    }

    [Fact]
    public void 设置项规范化只接受白名单()
    {
        Assert.Equal("mask", new AppSettings { IconTreatment = "mask" }.Normalized().IconTreatment);
        Assert.Equal("mask", new AppSettings { IconTreatment = "MASK" }.Normalized().IconTreatment);
        Assert.Equal("plain", new AppSettings { IconTreatment = " plain " }.Normalized().IconTreatment);
        Assert.Equal("auto", new AppSettings { IconTreatment = "squircle" }.Normalized().IconTreatment);
        Assert.Equal("auto", new AppSettings { IconTreatment = null! }.Normalized().IconTreatment);
        Assert.Equal("auto", new AppSettings().IconTreatment);
        Assert.Contains("plain", AppSettings.KnownIconTreatments);
    }
}

/// <summary>主题的图标数据（形状/投影都是主题参数，不是渲染器里的 if）。</summary>
public class ThemeIconMaskTests
{
    [Fact]
    public void 三主题的图标形状各按原生()
    {
        Assert.Equal(IconMaskStyle.RoundedRectangle, ThemeDefinition.MacOs().IconMask);
        Assert.Equal(IconMaskStyle.Squircle, ThemeDefinition.HarmonyOs().IconMask);
        Assert.Equal(IconMaskStyle.None, ThemeDefinition.Linux().IconMask);

        Assert.True(ThemeDefinition.MacOs().IconShadow);
        Assert.True(ThemeDefinition.HarmonyOs().IconShadow);
        Assert.False(ThemeDefinition.Linux().IconShadow);

        // 只有圆角方形主题需要半径
        Assert.Equal(0.2, ThemeDefinition.MacOs().IconMaskRadiusRatio, 6);
        Assert.Equal(0, ThemeDefinition.HarmonyOs().IconMaskRadiusRatio, 6);
    }

    [Fact]
    public void 既有的XAML遮罩口径未被改动()
    {
        // IconCornerRadiusRatio 是 ThemeIconClip（XAML 侧）的数据源，T6 不动它：
        // plain 方式与 GNOME 仍然完全沿用 v3 T4 的观感。
        Assert.Equal(0, ThemeDefinition.MacOs().IconCornerRadiusRatio, 6);
        Assert.Equal(0.25, ThemeDefinition.HarmonyOs().IconCornerRadiusRatio, 6);
        Assert.Equal(8.0 / 44.0, ThemeDefinition.Linux().IconCornerRadiusRatio, 6);
    }
}

/// <summary>alpha 域模糊（投影本体）：Core 里唯一碰像素的部分，必须能自证。</summary>
public class AlphaBlurTests
{
    [Fact]
    public void 常量alpha模糊后仍是同一常量()
    {
        var w = 8;
        var buf = new byte[w * w * 4];
        for (var i = 0; i < buf.Length; i += 4) { buf[i + 3] = 200; buf[i] = 7; }   // BGR 是脏值，必须被清掉
        AlphaBlur.Apply(buf, w, w, 2);
        for (var i = 0; i < buf.Length; i += 4)
        {
            Assert.Equal(200, buf[i + 3]);      // 整片同值 → 每窗均值 = 该值（精确）
            Assert.Equal(0, buf[i]);            // 纯黑投影
            Assert.Equal(0, buf[i + 1]);
            Assert.Equal(0, buf[i + 2]);
        }
    }

    [Fact]
    public void 孤立像素模糊后向四周散开()
    {
        var w = 16;
        var buf = new byte[w * w * 4];
        var c = (8 * w + 8) * 4;
        buf[c + 3] = 255;
        AlphaBlur.Apply(buf, w, w, 2);
        Assert.True(buf[c + 3] < 255, "中心应当被摊薄");
        Assert.True(buf[(8 * w + 9) * 4 + 3] > 0, "邻居应当拿到一些 alpha");
        Assert.True(buf[c + 3] > buf[(8 * w + 9) * 4 + 3], "离得越远越淡");
        // 三趟半径 2 的支撑是 ±6 列：第 15 列（距离 7）必须已经归零，否则"模糊"变成"整幅发灰"
        Assert.Equal(0, buf[(8 * w + 15) * 4 + 3]);
        // 形状信息全在 alpha 里 → BGR 必须为 0，否则投影会带一圈彩色
        for (var i = 0; i < buf.Length; i += 4)
            Assert.Equal(0, buf[i] | buf[i + 1] | buf[i + 2]);
    }

    [Fact]
    public void 非法入参与零半径是no_op()
    {
        var buf = new byte[4 * 4 * 4];
        buf[3] = 255;
        AlphaBlur.Apply(buf, 0, 0, 2);                       // 尺寸非法
        Assert.Equal(255, buf[3]);
        AlphaBlur.Apply(buf, 4, 4, 0);                       // 半径 0 = 恒等
        Assert.Equal(255, buf[3]);
        AlphaBlur.Apply(stackalloc byte[8], 4, 4, 2);        // 缓冲过短
    }

    [Fact]
    public void 半径覆盖整行时退化为均值不越界()
    {
        var w = 6;
        var buf = new byte[w * w * 4];
        buf[3] = 255;                                        // 只有一个不透明点
        AlphaBlur.Apply(buf, w, w, 20, 1);                   // 半径 >> 宽
        foreach (var a in AlphaOf(buf, w)) Assert.InRange(a, 0, 255);
    }

    private static IEnumerable<int> AlphaOf(byte[] buf, int w)
    {
        for (var i = 3; i < buf.Length; i += 4) yield return buf[i];
        _ = w;
    }
}
