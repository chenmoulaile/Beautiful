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
        Assert.False(new IconBakeSpec { IconSizeDip = 44, DpiScale = 1, Mask = IconMaskStyle.RoundedRectangle }.GeometryOnly);
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

/// <summary>设置项 IconTreatment → 实际烘焙参数（含"形状单一属主"不变量）。

/// <summary>设置项 IconTreatment → 实际烘焙参数（含"形状单一属主"不变量）。</summary>
public class IconTreatmentTests
{
    [Fact]
    public void auto_按macOS主题给形状与投影()
    {
        var mac = IconTreatment.Resolve("auto", ThemeDefinition.MacOs().IconMask,
                                         ThemeDefinition.MacOs().IconMaskRadiusRatio, ThemeDefinition.MacOs().IconShadow);
        Assert.Equal(IconMaskStyle.RoundedRectangle, mac.Mask);
        Assert.True(mac.Shadow);
        Assert.True(mac.Highlight);
        Assert.True(mac.BakesMask);     // macOS 的形状归烘焙位图
    }

    [Fact]
    public void mask_主题没给形状时也给通用圆角()
    {
        var forced = IconTreatment.Resolve("mask", IconMaskStyle.None, 0, false);
        Assert.Equal(IconMaskStyle.RoundedRectangle, forced.Mask);
        Assert.Equal(IconTreatment.FallbackRadiusRatio, forced.RadiusRatio, 6);
        Assert.True(forced.Shadow);
        Assert.True(forced.BakesMask);
        // 主题已有形状时不覆盖它（macOS 的圆角方块不被换掉）
        Assert.Equal(IconMaskStyle.RoundedRectangle, IconTreatment.Resolve("mask", IconMaskStyle.RoundedRectangle, 0.2, false).Mask);
    }

    [Fact]
    public void plain_什么都不烧_形状交回XAML()
    {
        var plain = IconTreatment.Resolve("plain", IconMaskStyle.RoundedRectangle, 0.2, true);
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
        Assert.True(IconTreatment.Resolve("乱写", IconMaskStyle.RoundedRectangle, 0, true).BakesMask);
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

/// <summary>macOS 图标遮罩数据（形状/投影都是主题参数）。。</summary>
public class ThemeIconMaskTests
{
    [Fact]
    public void macOS图标为圆角方形带投影_遮罩口径为零()
    {
        var t = ThemeDefinition.MacOs();
        Assert.Equal(IconMaskStyle.RoundedRectangle, t.IconMask);      // 圆角方块
        Assert.True(t.IconShadow);                                     // 贴近柔影
        Assert.Equal(0.2, t.IconMaskRadiusRatio, 6);                   // Big Sur ≈ 边长 20%
        // IconCornerRadiusRatio 是 ThemeIconClip（XAML 侧）的数据源，macOS=0 ⇒
        // 形状归烘焙位图独有，XAML 不注入 clip（T6 单属主不变量）
        Assert.Equal(0, t.IconCornerRadiusRatio, 6);
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
