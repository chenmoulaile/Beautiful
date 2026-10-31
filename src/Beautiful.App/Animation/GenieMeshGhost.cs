using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Beautiful.App.Interop;
using Beautiful.Core.Animation;
using static Beautiful.App.Interop.NativeMethods;
using Point = System.Windows.Point;      // UseWindowsForms 隐式 using 与 System.Drawing.Point 冲突

namespace Beautiful.App.Animation;

/// <summary>
/// genie 的渲染表面：一个分层窗口里放一张 <b>纹理三角网</b>，顶点由
/// <see cref="GenieLamp"/> 逐帧算出。
/// <para>
/// 【为什么用 WPF 3D 而不是 2D 条带】条带只能整体缩放，画不出"脖子"——而脖子（同一进度下
/// 越靠下的行收得越窄）正是 macOS genie 的全部特征。灯罩曲线给的是每个顶点的 2D 屏幕坐标，
/// 一个 <see cref="MeshGeometry3D"/> + 正交相机就能把它当成纹理网画出来，且
/// <c>System.Windows.Media.Media3D</c> 属于 WindowsDesktop 框架，不破坏"src 侧零第三方包"。
/// </para>
/// <para>
/// 【为什么材质走 Ambient】<c>DiffuseMaterial</c> 没有灯光时几乎全黑；把图放进
/// <see cref="MaterialGroup.Ambient"/> 就跟光照无关，也不会因为忘了挂光源而渲染成黑卡
/// （v2 那种"黑框"事故的又一形态）。
/// </para>
/// <para>
/// 每帧只改顶点坐标：拓扑（索引 + UV）在构造时一次算好，动画期间不重建集合——
/// 分配压力会直接变成掉帧，而本项目的动画判据是帧率无关。
/// </para>
/// </summary>
internal sealed class GenieMeshGhost : IDisposable
{
    /// <summary>
    /// 纵向行数。alpha.15：24→16——mesh 引擎真机 P95 80–146ms，500ms 只剩 10-19 帧（幻灯片感）；
    /// 顶点/三角形数直接决定每帧重排成本，16 行在"脖子平滑"与帧预算之间取折中（参考实现区间 8-96）。
    /// </summary>
    private const int Rows = 16;

    /// <summary>横向列数：脖子是竖向变化的，横向 6 列就够，省顶点（alpha.15：8→6，同上）。</summary>
    private const int Columns = 6;

    private readonly Window _window = new();
    private readonly MeshGeometry3D _mesh = new();
    private readonly int _vertexCount;
    private Rect _canvas;          // 屏幕物理像素：本窗口覆盖的区域
    private double _scale = 1.0;   // 本窗口 DPI 缩放（顶点是物理像素，WPF 用 DIP）

    public GenieMeshGhost(BitmapSource image)
    {
        _vertexCount = GenieMesh.VertexCount(Rows, Columns);

        _window.WindowStyle = WindowStyle.None;
        _window.AllowsTransparency = true;
        _window.Background = null;
        _window.ShowInTaskbar = false;
        _window.ShowActivated = false;
        _window.Topmost = true;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.Focusable = false;

        var camera = new OrthographicCamera { Position = new Point3D(0, 0, -1), LookDirection = new Vector3D(0, 0, 1), UpDirection = new Vector3D(0, -1, 0) };
        var viewport = new Viewport3D { Camera = camera };
        // EmissiveMaterial = 自发光，不参与光照计算：纹理按原样画出来，不需要挂灯、也不需要法线。
        // 用 DiffuseMaterial 的话，没有合适光源就是近黑一片——那是 v2"黑卡"事故的 3D 版本。
        var model = new GeometryModel3D
        {
            Geometry = _mesh,
            Material = new EmissiveMaterial(new ImageBrush(image)),
            // 双面：genie 收拢过程中有些格子会翻过来，只画正面会露出空洞
            BackMaterial = new EmissiveMaterial(new ImageBrush(image)),
        };
        viewport.Children.Add(new ModelVisual3D { Content = model });
        _window.Content = viewport;

        _mesh.TextureCoordinates = new PointCollection(GenieMesh.BuildTextureCoordinates(Rows, Columns)
            .Select(uv => new Point(uv.U, uv.V)));
        _mesh.TriangleIndices = new Int32Collection(GenieMesh.BuildTriangleIndices(Rows, Columns));
        var positions = new Point3DCollection();
        for (var i = 0; i < _vertexCount; i++) positions.Add(new Point3D());
        _mesh.Positions = positions;

        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            _scale = GetDpiForWindow(hwnd) / 96.0;
            var ex = GetWindowLongPtr(hwnd, GwlpExStyle);
            // 替身期间不许抢焦点：点穿到它下面的桌面/窗口，否则动画会把用户正在点的东西吞掉
            SetWindowLongPtr(hwnd, GwlpExStyle, new IntPtr(ex | WsExTransparent | WsExToolwindow));
        };
    }

    /// <summary>
    /// 按进度刷新一次网格。
    /// </summary>
    /// <param name="progress">0=完整窗口，1=塌进图标；还原方向由调用方倒着传。</param>
    /// <param name="windowRect">源窗口矩形（物理像素）。</param>
    /// <param name="iconRect">目标图标矩形（物理像素）。</param>
    /// <param name="split">两阶段分界（参数表 LampSplit；省略 = 参考实现默认 0.30）。</param>
    public void Apply(double progress, LampGeometry windowRect, LampGeometry iconRect, double split = GenieLamp.Split)
    {
        // 画布 = 源矩形与图标列的并集（再加一点余量给正弦摆动），每帧重算一次就够
        var minX = Math.Min(windowRect.X, iconRect.X) - windowRect.Width * 0.2;
        var maxX = Math.Max(windowRect.X + windowRect.Width, iconRect.X + iconRect.Width) + windowRect.Width * 0.2;
        var minY = Math.Min(windowRect.Y, iconRect.Y);
        var maxY = Math.Max(windowRect.Y + windowRect.Height, iconRect.Y) + 8;
        _canvas = new Rect(minX, minY, maxX - minX, maxY - minY);

        var positions = _mesh.Positions;
        for (var r = 0; r <= Rows; r++)
        {
            for (var c = 0; c <= Columns; c++)
            {
                var (tx, ty) = GenieMesh.VertexParameter(r, c, Rows, Columns);
                var (x, y) = GenieLamp.Vertex(tx, ty, progress, windowRect, iconRect, split);
                // 屏幕物理像素 → 本窗口内的 DIP（窗口已经摆到 _canvas 的原点上）
                positions[GenieMesh.VertexIndex(r, c, Columns)] = new Point3D(
                    (x - _canvas.X) / _scale, (y - _canvas.Y) / _scale, 0);
            }
        }

        var camera = (OrthographicCamera?)((Viewport3D)_window.Content).Camera;
        if (camera is not null)
        {
            camera.Width = _canvas.Width / _scale;
            camera.NearPlaneDistance = 0.1;
            camera.FarPlaneDistance = 100;
        }
    }

    public void Show()
    {
        _window.Show();
        Place();
    }

    /// <summary>把窗口摆到画布上（物理像素，绕开 WPF 的 DIP 取整误差）。</summary>
    public void Place()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(_canvas.X), (int)Math.Round(_canvas.Y),
            Math.Max(1, (int)Math.Round(_canvas.Width)), Math.Max(1, (int)Math.Round(_canvas.Height)),
            SwpNozorder | SwpNoactivate);
    }

    public void Dispose()
    {
        try { _window.Close(); }
        catch (InvalidOperationException) { /* 还没 Show 过：无窗口可关 */ }
    }
}
