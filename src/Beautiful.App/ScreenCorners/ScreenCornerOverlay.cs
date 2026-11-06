using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes;   // UseWindowsForms 隐式 using 与 System.Drawing.Brushes 同名
using Point = System.Windows.Point;             // 同上，System.Drawing.Point
using Color = System.Windows.Media.Color;       // 同上，System.Drawing.Color

namespace Beautiful.App.ScreenCorners;

/// <summary>
/// 屏幕圆角（对标 MyDockFinder ScreenRound）的<b>单角遮罩窗</b>：一块 r×r 物理像素的小窗，
/// 画"正方形 − 内切四分之一圆"，其余像素全透明。整窗 topmost + 输入全穿透
/// （WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE，再叠加 WS_EX_TOOLWINDOW 不进 Alt+Tab）。
/// <para>
/// 【为什么每角一只小窗而不是整屏一只】全屏分层窗要在 GPU 上合成一整块透明表面
/// （4K 上一张 800 万像素表面），而四角遮罩只需 4 张 r×r 小表面；置顶与输入穿透
/// 语义两种方案完全一致，代价差一个数量级。
/// </para>
/// <para>
/// 【形状用几何布尔而不是 ArcSegment】四种角的弧向各不相同（左上角往右下鼓、右上角往左下鼓…），
/// Exclude(矩形, 椭圆) 把"弧往哪边鼓"交给几何求差，四种角只有圆心不同——圆心一律取
/// <see cref="ScreenCornerPlan.ArcCenter"/>（Core 单一来源，本类不重写角映射）。
/// </para>
/// <para>
/// 【几何口径】WPF 的 Width/Height 只求初值（DIP = 物理px / 本屏缩放），物理矩形由
/// <see cref="Reassert"/> 经 SetWindowPos 最后说了算——与 DockWindow.PositionAsAppBar
/// 同一条"先 DIP 后物理"的顺序纪律。
/// </para>
/// </summary>
public sealed class ScreenCornerOverlay : Window
{
    private readonly Path _mask = new();   // 遮罩形状（其余像素全透明，形状内为遮罩色）
    private IntPtr _hwnd;
    private double _scale = 1.0;
    private string _maskColorSetting = ScreenCornerPlan.DefaultMaskColor;   // 设置原文（未规范化），色变对账按它比对

    /// <summary>本遮罩的角。</summary>
    public ScreenCorner Corner { get; }

    /// <summary>所属显示器的全屏矩形（物理像素）。Service 对账时按它识别"该屏的遮罩"。</summary>
    public PhysicalRect MonitorRect { get; private set; }

    /// <summary>当前半径（物理像素）。</summary>
    public int RadiusPx { get; private set; }

    public ScreenCornerOverlay(ScreenCorner corner, PhysicalRect monitorRect, int radiusPx, double dpiScale, string maskColor)
    {
        Corner = corner;
        MonitorRect = monitorRect;
        RadiusPx = radiusPx;
        _scale = dpiScale > 0 ? dpiScale : 1.0;
        _maskColorSetting = maskColor;

        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Background = Brushes.Transparent;      // 窗体底全透明：只有 Path 形状里有像素
        _mask.Fill = MaskBrush(maskColor);     // 遮罩色可配（默认纯黑，macOS 真机圆角处即熄屏边）；frozen 供渲染线程共享
        Content = _mask;
        RebuildMask();
        ReapplyLayout();
    }

    /// <summary>
    /// 重摆遮罩（显示器矩形 / 半径 / 缩放 / 遮罩色任一变化才动手；1s 对账里数值没变就是零开销路径）。
    /// </summary>
    public void Place(PhysicalRect monitorRect, int radiusPx, double dpiScale, string maskColor)
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        if (monitorRect == MonitorRect && radiusPx == RadiusPx && scale == _scale && maskColor == _maskColorSetting) return;
        MonitorRect = monitorRect;
        RadiusPx = radiusPx;
        _scale = scale;
        _maskColorSetting = maskColor;
        RebuildMask();
        ReapplyLayout();
        Reassert();
    }

    /// <summary>
    /// 对账轮询的重申：一次 SetWindowPos 同时钉住物理矩形与置顶。z 序会被别的 topmost 窗口抢走、
    /// 位置会被 DPI 变化/系统挪动——每轮重复施加（与全屏避让 NeedsReassert / DockWindow.AssertWindowRect
    /// 同一条自愈哲学：浮着是易失态，必须反复申明）。
    /// </summary>
    public void Reassert()
    {
        if (_hwnd == IntPtr.Zero) return;
        var square = ScreenCornerPlan.CornerSquare(MonitorRect, Corner, RadiusPx);
        if (square.Width <= 0 || square.Height <= 0) return;
        SetWindowPos(_hwnd, HwndTopmost, square.Left, square.Top, square.Width, square.Height,
            SwpNoactivate | SwpShowWindow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // WPF AllowsTransparency 自己会置 WS_EX_LAYERED；这里把穿透/不激活/工具窗三位补齐（幂等）。
        var ex = GetWindowLongPtr(_hwnd, GwlpExStyle);
        SetWindowLongPtr(_hwnd, GwlpExStyle, ex | WsExLayered | WsExTransparent | WsExNoactivate | WsExToolwindow);
        Reassert();
    }

    /// <summary>遮罩形状：正方形 − 内切四分之一圆。圆心与半径都从 Core 的物理几何换算成 DIP。</summary>
    private void RebuildMask()
    {
        _mask.Fill = MaskBrush(_maskColorSetting);   // 颜色与形状同走一条重建路径：改色对账即换刷
        var square = ScreenCornerPlan.CornerSquare(MonitorRect, Corner, RadiusPx);
        if (square.Width <= 0 || square.Height <= 0)
        {
            _mask.Data = Geometry.Empty;
            return;
        }
        var arc = ScreenCornerPlan.ArcCenter(square, Corner);
        var side = square.Width / _scale;
        var center = new Point((arc.X - square.Left) / _scale, (arc.Y - square.Top) / _scale);
        _mask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, side, side)),
            new EllipseGeometry(center, side, side));
    }

    /// <summary>设置原文 → frozen 纯色刷：Core 解析（非法回落 <see cref="ScreenCornerPlan.DefaultMaskColor"/>）。</summary>
    private static SolidColorBrush MaskBrush(string colorSetting)
    {
        // 防御性回落：设置走 Update/UpdateAndSave 必经 Normalized，但 Load 直读文件不保证——
        // 手写 settings.json 拼错颜色只丢配色，不丢遮罩（回落纯黑而不是透明）。
        var argb = ScreenCornerPlan.TryParseMaskColor(colorSetting, out var parsed) ? parsed : 0xFF000000u;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    /// <summary>写 WPF 侧 DIP 尺寸与初值位置（物理 SetWindowPos 在 SourceInitialized / Place 里收口）。</summary>
    private void ReapplyLayout()
    {
        var square = ScreenCornerPlan.CornerSquare(MonitorRect, Corner, RadiusPx);
        if (square.Width <= 0 || square.Height <= 0) return;
        Width = square.Width / _scale;
        Height = square.Height / _scale;
        Left = StripMath.ToDip(square.Left, _scale);
        Top = StripMath.ToDip(square.Top, _scale);
    }
}
