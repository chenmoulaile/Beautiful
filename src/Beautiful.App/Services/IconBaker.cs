using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.Core.Imaging;
using Beautiful.Core.Themes;
// UseWindowsForms=true 的隐式 using 带进 System.Drawing，与 WPF 的同名类型歧义（与 DockSurface 同一手法）：
// 烘焙画的是 WPF 的几何与画刷，全部显式指到 WPF 一侧。
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
namespace Beautiful.App.Services;

/// <summary>
/// 图标烘焙（v3 T6 B 段）：**每个 (图标源, 尺寸, 主题, DPI) 一次**，把
/// 超采样降采样 + 主题遮罩 + 顶部内高光 + **图标自身 alpha 轮廓**上的柔影全部烧进一张冻结位图。
/// <para>
/// 【为什么必须一次性】逐帧路径要**零分配、无 Effect**。WPF 的 <c>DropShadowEffect</c>/<c>BlurEffect</c>
/// 会把元素推进离屏滤镜管线，挂在每个条目的动画元素上，本仓库实测的 ~83µs/帧直接爆掉
/// （P95 门在 scripts/frametime.ps1）。所以投影/遮罩/高光都是**像素**，不是视觉树里的东西。
/// </para>
/// <para>
/// 【投影/高光的轮廓来自哪里 —— 4× 截图验收后改过一次】第一版用"遮罩几何"当剪影，结果圆形图标
/// （Edge / Chrome）外面套了一圈深色圆角方块，正是"图标劣质"的新造法。现在剪影取
/// **已裁形之后的可见像素自己的 alpha**（<see cref="BuildBody"/> 的输出）：Edge 的透明角落 alpha=0
/// → 没有影子，文件夹这种铺满图框的图标 → 影子是方的。顶部内高光同理只落在 alpha&gt;0 的像素上，
/// 不会再在图块的透明角落里画出一条白带。
/// </para>
/// <para>
/// 【像素域而不是 DrawingContext】高光与投影都要"按 alpha 调制"，在 Pbgra32 缓冲里做是一次线性遍历
/// （预乘域内加白 = 直接对 BGR 与 A 同加一个量），用 DrawingContext 就得再叠一层 OpacityMask 位图。
/// 模糊复用 Core 的 <see cref="AlphaBlur"/>（已经单测锁死），不引入 WPF Effect。
/// </para>
/// <para>
/// 【坐标系约定】两张 <see cref="RenderTargetBitmap"/> 都以 dpi=96 建立 → 画布单位 1 = 1 物理像素，
/// Core 的 <see cref="IconBakeSpec"/> 给的像素尺寸乘上超采样倍数就能直接当绘制矩形用。
/// 最终位图边长 == 图块物理像素 == round(IconSizeDip·DPI)，因此 <c>Image</c> 显示它是 1:1 搬运，
/// 运行时不再有任何重采样。
/// </para>
/// <para>
/// 【任何一步失败都回落原图】烘焙是装饰；宁可看到"没有遮罩但仍是 256px 的原图"，
/// 也不能让一个像素运算异常把整排图标变成空白（返回 null 会让视图回退成 ▣ 字形）。
/// </para>
/// </summary>
internal static class IconBaker
{
    /// <summary>投影模糊的盒式模糊趟数（三趟 ≈ 高斯，见 Core 的 AlphaBlur/BoxBlur 注释）。</summary>
    private const int ShadowBlurPasses = 3;

    /// <summary>烘焙一张图。source/spec 非法或绘制失败时返回 null（调用方回落原图）。</summary>
    public static BitmapSource? Bake(BitmapSource? source, IconBakeSpec spec)
    {
        if (source is null || source.PixelWidth <= 0) return null;
        var tile = spec.TilePx;
        if (tile <= 0) return null;
        // 什么都不用烧（plain 且无形状/投影/高光）而且原图恰好就是渲染尺寸 → 零成本命中，直接给原图
        if (spec.GeometryOnly && source.PixelWidth == tile) return source;
        try
        {
            var ss = spec.SupersampleForSource(source.PixelWidth);   // 1..4，且不超过源能供给的倍率
            var super = spec.SuperPx(ss);
            var k = (double)ss;

            // artwork 在超采样画布里的矩形：Core 已算好"投影挤占后的正方形 + 水平居中 + 顶部留白"
            var art = new Rect(spec.ArtworkLeftPx * k, spec.ArtworkTopPx * k,
                              spec.ArtworkPx * k, spec.ArtworkPx * k);

            var body = BuildBody(source, spec, super, art, k);
            if (body is null) return null;

            var shadow = spec.ShadowFits ? BuildShadow(body, spec, super, k) : null;

            var final = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(final, BitmapScalingMode.HighQuality);
            using (var dc = final.RenderOpen())
            {
                // 先投影后主体：投影整体下移 dy 后画在主体之下，露出的那一截就是"图标浮起来"的距离
                if (shadow is not null)
                    dc.DrawImage(shadow, new Rect(0, spec.ShadowOffsetPx, tile, tile));
                dc.DrawImage(body, new Rect(0, 0, tile, tile));
            }

            var rtb = new RenderTargetBitmap(tile, tile, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(final);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException
                                      or NotSupportedException or ArgumentException)
        {
            // 只记一笔：调用方（IconBakeContext）拿到 null 会直接用原图，Dock 不会因此空一栏
            Logger.Warn($"图标烘焙失败（回落原图）: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 超采样画布上的"主体"：源图按 <see cref="IconMaskStyle"/> 裁形后降采样到这里，
    /// 再在像素域加顶部内高光（只落在可见像素上）。返回 Pbgra32 冻结位图（预乘，alpha 即轮廓）。
    /// </summary>
    private static BitmapSource? BuildBody(BitmapSource source, IconBakeSpec spec, int super, Rect art, double k)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var mask = MaskGeometry(spec, art, k);
            if (mask is null)
            {
                // 无遮罩：形状属主是 XAML 的 ThemeIconClip（T6 的"单一属主"裁定），位图这里只管像素
                dc.DrawImage(source, art);
            }
            else
            {
                dc.PushClip(mask);
                dc.DrawImage(source, art);
                dc.Pop();
            }
        }

        var rtb = Rasterize(visual, super, super);
        var stride = super * 4;
        var buffer = new byte[stride * super];
        rtb.CopyPixels(buffer, stride, 0);

        if (spec.Highlight && spec.HighlightPx > 0)
            AddTopHighlight(buffer, super, art, spec.HighlightPx * k);

        var body = BitmapSource.Create(super, super, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        body.Freeze();
        return body;
    }

    /// <summary>
    /// 顶部内高光（#1A 级，自上缘向下线性淡出）：在**预乘**缓冲里加白 = BGR 与 A 同加一个量，
    /// 并按该像素自己的 alpha 调制 —— 透明角落拿不到高光，圆图标不会在方图块顶上出现一条白带。
    /// </summary>
    private static void AddTopHighlight(byte[] buffer, int super, Rect art, double bandPx)
    {
        var y0 = (int)Math.Floor(art.Y);
        var y1 = Math.Min(super, (int)Math.Ceiling(art.Y + bandPx));
        var x0 = (int)Math.Floor(art.X);
        var x1 = Math.Min(super, (int)Math.Ceiling(art.X + art.Width));
        if (y1 <= y0 || x1 <= x0 || bandPx <= 0) return;

        for (var y = y0; y < y1; y++)
        {
            // 距上缘越远越淡；art.Y 之下第 t 像素
            var t = (y - art.Y) / bandPx;
            if (t < 0 || t >= 1) continue;
            var ramp = IconBakeSpec.HighlightAlpha * (1 - t);
            for (var x = x0; x < x1; x++)
            {
                var i = (y * super + x) * 4;
                var coverage = buffer[i + 3] / 255.0;            // 只给"看得见的像素"加高光
                if (coverage <= 0) continue;
                var add = (byte)(ramp * coverage);
                buffer[i] = SaturateAdd(buffer[i], add);         // B
                buffer[i + 1] = SaturateAdd(buffer[i + 1], add); // G
                buffer[i + 2] = SaturateAdd(buffer[i + 2], add); // R
                buffer[i + 3] = SaturateAdd(buffer[i + 3], add); // A（预乘域内加白必须同步抬 alpha）
            }
        }
    }

    private static byte SaturateAdd(byte value, int add)
        => (byte)Math.Min(byte.MaxValue, value + add);

    /// <summary>
    /// 投影 = 主体的 alpha 轮廓做三次盒式模糊（<see cref="AlphaBlur"/> 顺带把 BGR 清成纯黑），
    /// 再整体乘上 <see cref="IconBakeSpec.ShadowOpacity"/>（#4D 级）。
    /// 输入是 body 的像素，所以圆形图标得到的是环形影、铺满图框的图标得到的是方影。
    /// </summary>
    private static BitmapSource BuildShadow(BitmapSource body, IconBakeSpec spec, int super, double k)
    {
        var stride = super * 4;
        var buffer = new byte[stride * super];
        body.CopyPixels(buffer, stride, 0);

        AlphaBlur.Apply(buffer, super, super, Math.Max(1, spec.ShadowBlurPx * (int)k), ShadowBlurPasses);

        // 强度：#4D 级（Core 常量）——再深在暗色面板上就成了"图标掉进洞里"
        var opacity = (double)IconBakeSpec.ShadowOpacity / 255.0;
        for (var i = 3; i < buffer.Length; i += 4)
            buffer[i] = (byte)(buffer[i] * opacity);

        var shadow = BitmapSource.Create(super, super, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        shadow.Freeze();
        return shadow;
    }

    // ---------- 几何 ----------

    /// <summary>
    /// 遮罩几何（绘制坐标）：<see cref="IconMaskStyle.Squircle"/> 复用 Core 的 <see cref="Superellipse"/>
    /// 采样（与既有鸿蒙 ThemeIconClip 同一条曲线，不发明第二种形状）；圆角方形用 RectangleGeometry；
    /// <see cref="IconMaskStyle.None"/> 返回 null（调用方按"不裁形"处理）。
    /// </summary>
    private static Geometry? MaskGeometry(IconBakeSpec spec, Rect rect, double k) => spec.Mask switch
    {
        IconMaskStyle.None => null,
        IconMaskStyle.Squircle => SquircleGeometry(rect),
        // 半径由 Core 按 artwork 像素算，这里换算到当前画布坐标（乘超采样倍数 k）
        _ => Freeze(new RectangleGeometry(rect, spec.RadiusPx * k, spec.RadiusPx * k))
    };

    private static Geometry SquircleGeometry(Rect rect)
    {
        var pts = IconBake.ToPixels(IconBake.SquircleUnitPoints(), rect.X, rect.Y, rect.Width, rect.Height);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: true, isClosed: true);
            for (var i = 1; i < pts.Count; i++)
                ctx.LineTo(new Point(pts[i].X, pts[i].Y), isStroked: true, isSmoothJoin: true);
        }
        return Freeze(geo);
    }

    private static Geometry Freeze(Geometry geo)
    {
        geo.Freeze();
        return geo;
    }

    /// <summary>把视觉光栅成 w×h 的 Pbgra32 冻结位图（dpi=96 → 画布单位即物理像素）。</summary>
    private static BitmapSource Rasterize(DrawingVisual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
