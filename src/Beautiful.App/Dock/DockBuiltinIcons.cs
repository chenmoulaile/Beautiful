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
/// 随便找个系统 DLL 的图标又会撞上"Windows 味"。矢量自绘一次到位，不依赖外部资源
/// （Apple 资产不进公开仓库分发——视觉按 macOS 官方图标样式同构还原）。
/// </para>
/// <para>
/// 【alpha.25 重画的依据】几何与配色直接从 macOS 实拍图标逐像素测量
/// （用户提供的 MyDockFinder 实机截图里的官方复刻版 Finder 图标）：
/// 访达 = 白底圆角方 + 右侧蓝色半脸剪影（鼻梁轮廓是脸的左边界，被上下边裁切）
///       + 两枚深色竖胶囊眼（左眼落在白底上、右眼落在蓝脸上）+ 全宽深色笑弧（右嘴角略高）。
/// 启动台 = 浅银底 + 45° 指向右上的火箭（深灰鼻锥、白机身、舷窗、后掠侧翼、尾焰、三道速度线）。
/// 注意 WPF RotateTransform 正角 = 顺时针：机头朝上画、转 +45° 才指向右上（alpha.24 之前误用 -45°，
/// 火箭指向左上——真机对比一眼假的直接原因之一）。
/// 渲染验证管线：scripts/draw.py 用同一几何走 SVG 渲染，与实拍参考并排对比后再定稿。
/// </para>
/// </summary>
internal static class DockBuiltinIcons
{
    private const double Origin = 8;      // 240×240 内容区在 256 画布里的边距
    private const double Content = 240;   // 图标内容区边长
    private const double CornerRadius = 54;

    /// <summary>访达图标（macOS Finder 同构：白底 + 右侧蓝色半脸剪影 + 深色五官）。</summary>
    public static BitmapSource Finder() => Render(dc =>
    {
        var clip = new RectangleGeometry(
            new Rect(Origin, Origin, Content, Content), CornerRadius, CornerRadius);
        dc.PushClip(clip);

        // 底：白 → 浅灰的垂直渐变（实测 #FEFEFE → #E6E7E9）
        dc.DrawRoundedRectangle(Fill("#FEFEFE", "#E6E7E9", vertical: true), null,
            new Rect(Origin, Origin, Content, Content), CornerRadius, CornerRadius);

        // 蓝色半脸剪影：左边界 = 鼻梁轮廓（额头→鼻尖→人中→唇→下巴），上下边被图标裁切。
        // 坐标单位 = 240 内容区（o(x) = Origin + x），来自实拍测量。
        static double o(double v) => Origin + v;
        var face = new StreamGeometry();
        using (var g = face.Open())
        {
            g.BeginFigure(new Point(o(120), o(-8)), true, true);
            g.BezierTo(new Point(o(109), o(48)), new Point(o(100), o(92)), new Point(o(89), o(128)), true, false);
            g.BezierTo(new Point(o(86), o(138)), new Point(o(94), o(144)), new Point(o(106), o(147)), true, false);
            g.BezierTo(new Point(o(112), o(148.5)), new Point(o(109), o(152)), new Point(o(107), o(157)), true, false);
            g.BezierTo(new Point(o(113), o(187)), new Point(o(119), o(218)), new Point(o(120), o(248)), true, false);
            g.LineTo(new Point(o(153), o(248)), true, false);
            g.BezierTo(new Point(o(181), o(214)), new Point(o(213), o(198)), new Point(o(213), o(128)), true, false);
            g.LineTo(new Point(o(213), o(38)), true, false);
            g.BezierTo(new Point(o(213), o(10)), new Point(o(195), o(-8)), new Point(o(166), o(-8)), true, false);
        }
        face.Freeze();
        var faceBrush = new LinearGradientBrush(
            Color.FromRgb(0x5C, 0xC6, 0xF9), Color.FromRgb(0x5B, 0x8C, 0xF3), 90);
        faceBrush.GradientStops.Insert(1, new GradientStop(Color.FromRgb(0x48, 0xA2, 0xF6), 0.5));
        faceBrush.StartPoint = new Point(0, 0);
        faceBrush.EndPoint = new Point(0.12, 1);
        faceBrush.MappingMode = BrushMappingMode.RelativeToBoundingBox;
        dc.DrawGeometry(faceBrush, null, face);

        // 双眼：深色竖胶囊（左眼在白底上，右眼在蓝脸上），实测中心 (68,80) / (163,83)
        DrawEye(dc, o(68), o(80));
        DrawEye(dc, o(163), o(83));

        // 笑弧：全宽深色圆头弧线，右嘴角略高（实测两端 (46,160)→(184,153)，垂点 ~(115,224) 控制点）
        var smile = new StreamGeometry();
        using (var g = smile.Open())
        {
            g.BeginFigure(new Point(o(46), o(160)), false, false);
            g.QuadraticBezierTo(new Point(o(115), o(224)), new Point(o(184), o(153)), true, false);
        }
        smile.Freeze();
        var smilePen = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x28, 0x2C)), 11)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, smilePen, smile);

        dc.Pop();
    });

    /// <summary>启动台图标（macOS Launchpad 同构：浅银底 + 45° 指向右上的火箭）。</summary>
    public static BitmapSource Launchpad() => Render(dc =>
    {
        var clip = new RectangleGeometry(
            new Rect(Origin, Origin, Content, Content), CornerRadius, CornerRadius);
        dc.PushClip(clip);

        // 底：浅银垂直渐变（#FBFBFC → #D9DCDF）
        dc.DrawRoundedRectangle(Fill("#FBFBFC", "#D9DCDF", vertical: true), null,
            new Rect(Origin, Origin, Content, Content), CornerRadius, CornerRadius);

        // 火箭整体 45° 指向右上：机头朝上画，绕画布中心顺时针转 45°
        dc.PushTransform(new RotateTransform(45, 128, 128));

        // 侧翼（后掠，左右各一片；右翼更深一档体现背光）
        dc.DrawGeometry(Fill("#5F6C79", "#5F6C79", vertical: false), null, Fin(mirror: false));
        dc.DrawGeometry(Fill("#4E5A66", "#4E5A66", vertical: false), null, Fin(mirror: true));

        // 机体：白→浅银（左亮右暗）
        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(128, 28), true, true);
            g.BezierTo(new Point(112, 46), new Point(100, 76), new Point(100, 112), true, false);
            g.BezierTo(new Point(100, 144), new Point(108, 168), new Point(120, 180), true, false);
            g.LineTo(new Point(136, 180), true, false);
            g.BezierTo(new Point(148, 168), new Point(156, 144), new Point(156, 112), true, false);
            g.BezierTo(new Point(156, 76), new Point(144, 46), new Point(128, 28), true, false);
        }
        body.Freeze();
        dc.DrawGeometry(Fill("#FFFFFF", "#D3DAE1", vertical: false), null, body);

        // 鼻锥：深灰（盖在机体上部）
        var nose = new StreamGeometry();
        using (var g = nose.Open())
        {
            g.BeginFigure(new Point(128, 28), true, true);
            g.BezierTo(new Point(118, 39), new Point(110, 54), new Point(105, 72), true, false);
            g.BezierTo(new Point(112, 78), new Point(120, 81), new Point(128, 81), true, false);
            g.BezierTo(new Point(136, 78), new Point(144, 78), new Point(151, 72), true, false);
            g.BezierTo(new Point(146, 54), new Point(138, 39), new Point(128, 28), true, false);
        }
        nose.Freeze();
        dc.DrawGeometry(Fill("#6A7683", "#4A5661", vertical: false), null, nose);

        // 尾喷口
        dc.DrawGeometry(Fill("#7C8894", "#7C8894", vertical: false), null, Nozzle());

        // 舷窗：深灰环 + 浅蓝芯
        dc.DrawEllipse(Fill("#55636F", "#55636F", vertical: false), null, new Point(128, 106), 16, 16);
        dc.DrawEllipse(Fill("#D6E5F2", "#D6E5F2", vertical: false), null, new Point(128, 106), 10.5, 10.5);

        // 尾焰：小泪滴（橙黄渐变）
        var flame = new StreamGeometry();
        using (var g = flame.Open())
        {
            g.BeginFigure(new Point(123, 203), true, true);
            g.BezierTo(new Point(118, 213), new Point(121, 226), new Point(128, 236), true, false);
            g.BezierTo(new Point(135, 226), new Point(138, 213), new Point(133, 203), true, false);
        }
        flame.Freeze();
        dc.DrawGeometry(Fill("#FFC94D", "#FF7A33", vertical: true), null, flame);

        // 速度线：三道平行于轴线的短划（旋转后落在左下）
        var speedPen = new Pen(Fill("#AEB6BF", "#AEB6BF", vertical: false), 7.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawLine(speedPen, new Point(58, 186), new Point(58, 206));
        dc.DrawLine(speedPen, new Point(80, 202), new Point(80, 222));
        dc.DrawLine(speedPen, new Point(102, 216), new Point(102, 236));

        dc.Pop();   // RotateTransform
        dc.Pop();   // clip
    });

    /// <summary>眼：深色竖胶囊（宽 10 高 23，圆角 5）。</summary>
    private static void DrawEye(DrawingContext dc, double cx, double cy)
        => dc.DrawRoundedRectangle(
            Fill("#1B2430", "#1B2430", vertical: false), null,
            new Rect(cx - 5, cy - 11.5, 10, 23), 5, 5);

    /// <summary>后掠侧翼：从机侧 (103/153,128) 掠到翼尖 (72/184,196)。</summary>
    private static Geometry Fin(bool mirror)
    {
        var rootX = mirror ? 153 : 103;
        var tipX = mirror ? 184 : 72;
        var midX = mirror ? 142 : 114;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(rootX, 128), true, true);
            ctx.BezierTo(
                new Point(mirror ? 164 : 92, 148), new Point(mirror ? 174 : 82, 168),
                new Point(tipX, 196), true, false);
            ctx.BezierTo(
                new Point(mirror ? 168 : 88, 188), new Point(mirror ? 152 : 104, 182),
                new Point(midX, 180), true, false);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Nozzle()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(119, 180), true, true);
            ctx.LineTo(new Point(137, 180), true, false);
            ctx.LineTo(new Point(133, 199), true, false);
            ctx.LineTo(new Point(123, 199), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>两端色渐变（vertical=true 自上而下；false 自左而右）。</summary>
    private static Brush Fill(string fromHex, string toHex, bool vertical)
    {
        var (a, b) = (Parse(fromHex), Parse(toHex));
        return vertical
            ? new LinearGradientBrush(a, b, 90)
            : new LinearGradientBrush(a, b, 0);
    }

    private static Color Parse(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

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
