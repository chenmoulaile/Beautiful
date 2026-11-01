using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 常驻格（访达 / 启动台）的内置图标：矢量绘制 → 256px 位图（冻结），启动时画一次。
/// <para>
/// 【为什么不用图标提取】这两格没有可执行的"源文件"可取图标（访达是我们的窗口、启动台是功能格），
/// 随便找个系统 DLL 的图标又会撞上"Windows 味"。macOS 的两枚图标特征明确：
/// 访达=双色蓝脸+右嘴角上扬的笑弧，启动台=银底+45° 斜指右上的火箭。矢量自绘一次到位，
/// 不依赖外部资源（Apple 资产不进公开仓库分发——同 SF Pro 字体的既定红线，视觉按
/// macOS 官方图标样式同构还原）。
/// </para>
/// </summary>
internal static class DockBuiltinIcons
{
    /// <summary>访达图标（macOS Finder 同构：左浅右深双色蓝脸，白线脸，右嘴角上扬的笑弧）。</summary>
    public static BitmapSource Finder() => Render(dc =>
    {
        // 左半：亮蓝垂直渐变（上亮下微沉）——底板先整张画，右半再覆盖
        DrawRoundedBase(dc, new LinearGradientBrush(
            Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0x25, 0x9B, 0xF2), 90));
        // 右半：更深的蓝（clip 到右半，保持同圆角；分界线即脸的中线）
        dc.PushClip(new RectangleGeometry(new Rect(128, 0, 128, 256)));
        dc.DrawRoundedRectangle(
            new LinearGradientBrush(Color.FromRgb(0x1F, 0x8B, 0xEC), Color.FromRgb(0x14, 0x62, 0xD9), 90),
            null, new Rect(0, 8, 240, 240), 56, 56);
        dc.Pop();

        // 脸（白色圆头线）：双眼在中线两侧对称位置，笑弧"左低右高"横贯双色分界。
        var pen = new Pen(Brushes.White, 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        // 左眼（浅蓝半边的竖线）
        dc.DrawLine(pen, new Point(92, 88), new Point(92, 124));
        // 右眼（深蓝半边的竖线，与左眼等高）
        dc.DrawLine(pen, new Point(164, 88), new Point(164, 124));
        // 笑弧：从左脸颊 (58,158) 经下颚收束点 (122,208) 挑到右脸颊 (196,132)——
        // 右嘴角显著上扬是 macOS Finder 的签名特征，对称 U 形一眼假。
        var smile = new StreamGeometry();
        using (var g = smile.Open())
        {
            g.BeginFigure(new Point(58, 158), false, false);
            g.QuadraticBezierTo(new Point(122, 208), new Point(196, 132), true, false);
        }
        smile.Freeze();
        dc.DrawGeometry(null, pen, smile);
    });

    /// <summary>启动台图标（macOS Launchpad 同构：银灰渐变底 + 45° 斜指右上的火箭）。</summary>
    public static BitmapSource Launchpad() => Render(dc =>
    {
        DrawRoundedBase(dc, new LinearGradientBrush(
            Color.FromRgb(0xF4, 0xF6, 0xF8), Color.FromRgb(0xC7, 0xCC, 0xD4), 90));

        // 火箭整体 45° 斜指右上：垂直坐标系里画完绕画布中心旋转 -45°，
        // 机头朝上 → 旋转后朝右上；尾焰/背鳍跟着同一变换，轴线自然连续。
        dc.PushTransform(new RotateTransform(-45, 128, 128));

        // 背鳍（深银灰，左右各一片，斜切后掠）
        var finBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x99, 0xA8));
        dc.DrawGeometry(finBrush, null, FinGeometry(mirror: false));
        dc.DrawGeometry(finBrush, null, FinGeometry(mirror: true));

        // 机体：白→浅银的纺锤（尖头圆底）
        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(128, 38), true, true);
            g.BezierTo(new Point(96, 66), new Point(84, 108), new Point(88, 152), true, false);
            g.BezierTo(new Point(90, 172), new Point(106, 188), new Point(128, 196), true, false);
            g.BezierTo(new Point(150, 188), new Point(166, 172), new Point(168, 152), true, false);
            g.BezierTo(new Point(172, 108), new Point(160, 66), new Point(128, 38), true, false);
        }
        body.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD5, 0xDA, 0xE2), 90), null, body);

        // 舷窗：深灰环 + 浅蓝芯（沿中轴偏上）
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x51, 0x63, 0x78)), null, new Point(128, 108), 17, 17);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xA8, 0xDC, 0xFB)), null, new Point(128, 108), 11, 11);

        // 尾焰：沿轴线（旋转后指向左下）的橙黄泪滴
        var flame = new StreamGeometry();
        using (var g = flame.Open())
        {
            g.BeginFigure(new Point(128, 198), true, true);
            g.BezierTo(new Point(112, 214), new Point(116, 236), new Point(128, 250), true, false);
            g.BezierTo(new Point(140, 236), new Point(144, 214), new Point(128, 198), true, false);
        }
        flame.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(
            Color.FromRgb(0xFF, 0xD1, 0x66), Color.FromRgb(0xFF, 0x7A, 0x33), 90), null, flame);

        dc.Pop();
    });

    /// <summary>背鳍：从机腹斜向外掠的翼片（垂直坐标系画，随整体 -45° 旋转）。</summary>
    private static Geometry FinGeometry(bool mirror)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            // 左鳍：从机侧 (96,130) 后掠到 (74,186)；右鳍镜像
            var rootX = mirror ? 160 : 96;
            var tipX = mirror ? 182 : 74;
            ctx.BeginFigure(new Point(rootX, 130), true, true);
            ctx.LineTo(new Point(tipX, 186), true, false);
            ctx.LineTo(new Point(rootX, 178), true, false);
        }
        g.Freeze();
        return g;
    }

    private static void DrawRoundedBase(DrawingContext dc, Brush fill)
        => dc.DrawRoundedRectangle(fill, null, new Rect(8, 8, 240, 240), 56, 56);

    private static BitmapSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
