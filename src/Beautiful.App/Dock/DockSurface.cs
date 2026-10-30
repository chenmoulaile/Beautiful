using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Beautiful.App.Services;
using Beautiful.Core.Dock;
// UseWindowsForms=true + ImplicitUsings 引入了 System.Drawing 全局命名空间，
// 与 WPF 的 Point/Size/Rect 同名 → 显式指到 WPF 版本（与 DockWindow.xaml.cs 同一手法）。
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using Brush = System.Windows.Media.Brush;   // 同上消歧（倒影遮罩字段类型），System.Drawing.Brush 全局歧义
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;         // 拖放指示条；消歧 System.Drawing.Rectangle
using MouseEventArgs = System.Windows.Input.MouseEventArgs; // 拖拽 MouseMove；消歧 System.Windows.Forms.MouseEventArgs

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 自绘面板（Task 6）：<see cref="Canvas"/> 承载条目容器，动画由
/// <see cref="CompositionTarget.Rendering"/>（vsync 对齐）驱动。
/// <para>
/// 与 v1（ItemsControl + 逐帧 Margin 覆写 + 15ms DispatcherTimer）的四点差异：
/// <list type="number">
/// <item><description>目标 scale 来自 <see cref="MagnificationField"/> 的**连续光标坐标**，
/// 不再是"哪个条目被悬停"的离散索引 → 悬停边界切换时整排图标不会突跳。</description></item>
/// <item><description>每帧只写 <see cref="Canvas.SetLeft"/> / <see cref="Canvas.SetTop"/> 与
/// <see cref="UIElement.RenderTransform"/>（ScaleTransform，原点 0.5,1），
/// 绝不改 Margin / LayoutTransform → 无逐项布局重排。左右两缘由 <see cref="MagnificationGeometry"/>
/// 一次算清：**绘制**左缘保证相邻像素间距恒为 Spacing，<c>Canvas.Left</c> 写的是绕自身中心放大前的布局左缘。</description></item>
/// <item><description>逐帧步长 <c>dt = (now-last)/Frequency</c> 交给
/// <see cref="AnimationMath.Damp"/>（指数阻尼，帧率无关），而非固定 0.35 系数 lerp。</description></item>
/// <item><description>容器由 <see cref="DataTemplateSelector"/> 一次性生成并缓存在
/// <see cref="_containers"/>，逐帧按引用直接取用，不再调 ItemContainerGenerator；
/// 场计算与行几何都写进复用缓冲区（<see cref="MagnificationField.ComputeScalesInto"/> /
/// <see cref="MagnificationGeometry.ComputeRowLefts"/>），动画循环每帧零分配。</description></item>
/// </list>
/// 空闲判定看的是**条目行**而非窗口矩形：承载窗口是 AppBar 全宽条带（含两侧透明区与顶部余量），
/// 光标只有在行的水平影响范围（行左右各一个图标间距）内才算"有悬停目标"；
/// 范围外一律回落，全部条目收敛到目标后立刻摘掉 Rendering 钩子 → 光标停在任务栏/托盘一带不空转。
/// </para>
/// <para>
/// 【面板宽度跟随】<see cref="AutoWidth"/>（默认开）时，底板宽度随放大后的总宽生长——实现方式是
/// <see cref="MeasureOverride"/> 返回变化后的 DesiredSize（本控件自身的一次 InvalidateMeasure），
/// 而不是像 v1 那样逐帧改写每个容器的 Margin 让整排重排；条目几何仍然只有 Left/Top + RenderTransform。
/// </para>
/// </summary>
public sealed class DockSurface : Canvas
{
    /// <summary>指数阻尼收敛速率（约 14/s，Task 5 建议值：≈0.4s 收敛到视觉稳定）。</summary>
    private const double DampLambda = 14.0;

    /// <summary>单帧最大步长（秒）：超过视为卡帧（睡眠唤醒/调试断点/VM 掉帧），直接吸附目标不追帧。</summary>
    private const double MaxFrameStepSeconds = 0.25;

    /// <summary>底板宽度跟随场变化时的最小失效阈值（DIP），避免亚像素抖动引发无谓 measure。</summary>
    private const double WidthFollowStepDip = 0.25;

    /// <summary>
    /// 行高在图标尺寸之上的**主题**余量默认值（DIP，见 <see cref="RowInsetDipProperty"/>）：
    /// 图标在行内垂直居中，故上下各得一半。v3 T4 前它是写死的 20——IconSize=44 时行高 64、面板 76，
    /// 面板里约一半是不装东西的深色带，正是用户"下方的 dock 栏有一段黑框，我只需要 dock 的大小"的来源。
    /// 现在由主题给出（macOS 10 / 鸿蒙 12 / GNOME 16），DockWindow.ApplySurfaceLayoutParams 写入。
    /// 顶部那半同时是放大图标在面板内的生长起点，底部那半容纳运行指示点（不压内描边）。
    /// </summary>
    private const double DefaultRowInsetDip = 20;

    /// <summary>图标基准边长默认值（DIP，与 IconSizeProperty 默认一致，仅用于 _rowHeight 初值）。</summary>
    private const double DefaultIconSizeDip = 44;

    /// <summary>倒影带间隙（DIP）：镜像顶边与图标底边的距离（macOS Dock 倒影留一道细缝）。</summary>
    private const double ReflectionGapDip = 2;

    /// <summary>倒影高度占图标尺寸比例。</summary>
    private const double ReflectionHeightRatio = 0.4;

    /// <summary>启动弹跳时长（秒）与纵向峰值幅度（占图标尺寸比例）。</summary>
    private const double BounceDurationSeconds = 0.6;
    private const double BounceAmplitudeRatio = 0.5;

    /// <summary>
    /// 弹跳幅度上限（DIP）：BounceOffset 峰值 ≈1.194A（见 Core 注释），A=20 时峰值上移 23.9 DIP，
    /// 小于最小条带顶部余量场景的可用高度（MinStripHeadroomDip 22 + Surface 上边距 6 + 行内居中余量 ≥10），
    /// 大图标（72）按比例会顶出窗口，故按比例 + 绝对值双限。
    /// </summary>
    private const double BounceMaxAmplitudeDip = 20;

    /// <summary>可见性判定的最小宽度（DIP）：媒体胶囊收起（Width=0）时整槽不参与排布，不留空隙。</summary>
    private const double MinVisibleWidthDip = 0.5;

    private static readonly int[] EmptyInts = Array.Empty<int>();

    /// <summary>
    /// 帧间隔采样开关：环境变量 <c>BEAUTIFUL_FRAMETIME=1</c> 时，动画循环每真正干活的一帧写
    /// <c>BEAUTIFUL_FRAME &lt;Stopwatch.GetTimestamp()&gt;</c> 日志（scripts/frametime.ps1 解析）。
    /// 关闭时零 IO——Logger 每次写入都是 open/append/close，逐帧写盘会自己成为掉帧源，故必须默认关。
    /// </summary>
    private static readonly bool FrameLogEnabled =
        Environment.GetEnvironmentVariable("BEAUTIFUL_FRAMETIME") == "1";

    private readonly List<FrameworkElement?> _containers = new();
    private readonly List<DockItem?> _items = new();

    /// <summary>倒影元素（与 _containers 同下标；null = 该项无倒影：媒体槽/无图标回退项/倒影关闭/分隔符）。</summary>
    private readonly List<FrameworkElement?> _reflections = new();

    /// <summary>角标元素（按条目 Id 索引）：只覆盖状态，不改 DockItem 本身（record 不被 UI 状态污染）。</summary>
    private readonly Dictionary<string, FrameworkElement> _badgeElements = new(StringComparer.Ordinal);

    /// <summary>当前角标文字（按条目 Id）：条目重建/换源后据此把角标重新挂回新容器。</summary>
    private readonly Dictionary<string, string> _badgeTexts = new(StringComparer.Ordinal);

    /// <summary>进度条轨道（按条目 Id）与其填充元素：只覆盖状态，不进 DockItem。</summary>
    private readonly Dictionary<string, Border> _progressTracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _progressFills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Fraction, DockProgressState State)> _progressValues =
        new(StringComparer.Ordinal);

    /// <summary>逐帧几何状态（下标 = 条目下标，与 _containers 对齐）。</summary>
    private double[] _cur = Array.Empty<double>();     // 当前（阻尼后）scale
    private double[] _tgt = Array.Empty<double>();     // 目标 scale（来自放大场）
    private double[] _baseLefts = Array.Empty<double>(); // 未放大左缘
    private double[] _lefts = Array.Empty<double>();     // 本帧**绘制**（像素）左缘：命中测试/媒体面板定位读它
    private double[] _layoutLefts = Array.Empty<double>(); // 本帧写给 Canvas.Left 的布局左缘（= drawn + extra/2）
    private double[] _widths = Array.Empty<double>();  // 未放大宽（模板 DesiredSize，胶囊 124 / 图标 44）
    private double[] _heights = Array.Empty<double>();
    private double[] _bounceRemaining = Array.Empty<double>(); // 启动弹跳剩余时长（秒；0=空闲）
    private double[] _bounceY = Array.Empty<double>();         // 本帧弹跳纵向偏移（DIP，负=上移）

    /// <summary>
    /// 放大场输入：仅**可见且可放大**的项参与（隐藏胶囊不占位也不产生"幽灵鼓包"；分隔符占位宽但
    /// 中心不进场 → 缩放恒 1，规则见 Core 的 <c>DockSeparatorPlan.IsMagnifiable</c>）。长度按条目数
    /// 预分配，实际有效前 <see cref="_fieldCount"/> 项，交给场函数时用 AsSpan 精确截断（不分配、不装箱）。
    /// </summary>
    private double[] _fieldCenters = Array.Empty<double>();
    private int[] _fieldToItem = EmptyInts;
    private bool[] _magnifiable = Array.Empty<bool>();  // 基准排布的"是否参与放大场"掩码（与 _widths 同长度）
    private double[] _fieldScales = Array.Empty<double>();   // 场输出缓冲区（逐帧零分配，见 MagnificationField.ComputeScalesInto）
    private int _fieldCount;

    private double _naturalWidth;    // 未放大总宽
    private double _rowHeight = DockRowMath.RowHeightDip(DefaultIconSizeDip, DefaultRowInsetDip, false,
                                                          ReflectionGapDip, ReflectionHeightRatio);

    /// <summary>
    /// 倒影垂直遮罩（冻结共享）：渐变**在镜像变换后的局部坐标**里定义——倒影元素的 RenderTransform
    /// ScaleY=-1 把画布上下翻转，故"局部底=最终顶（贴近图标）"取 45% 不透明、"局部顶=最终底"取全透明，
    /// 得到 macOS 式"上实下虚"的垂直渐隐。冻结后跨条目/跨线程（渲染线程）安全共享，零逐帧分配。
    /// </summary>
    private static readonly Brush ReflectionMask = CreateReflectionMask();

    private static Brush CreateReflectionMask()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),   // 局部顶 → 翻转后是倒影底缘：全透明
            Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF),   // 局部底 → 翻转后贴图标：45% 不透明
            new Point(0.5, 0), new Point(0.5, 1));
        brush.Freeze();
        return brush;
    }

    private double _lastReportedTotal = double.NaN;
    private double _lastCursorX = double.NaN;

    private bool _layoutDirty = true;
    private bool _structureDirty = true;   // 可视树重建（区别于仅尺寸变化）：只有它才重置逐帧缩放状态
    private bool _settled;
    private bool _hooked;
    private long _lastTick;

    // ---------- 拖放（固定 / 排序 / 拖出取消固定）----------
    // 指示条是**唯一一个常驻元素**：建一次、逐帧只改 Canvas.Left/Width/Top + Visibility，绝不在
    // DragOver/MouseMove 里新建（那些事件是连续高频的，新建 Freezable/visual 会污染帧预算并触发排布）。
    // 它的画法对齐分隔符：一条竖细线，accent 色取主题字典（DynamicResource 随主题换）。
    private readonly Rectangle _dropBar = new()
    {
        Width = 2.0,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        SnapsToDevicePixels = true,
    };

    // 指示条当前落点（-1 = 隐藏）。逐帧 ApplyGeometry 在拖放期间按最新绘制左缘复算它的 X，
    // 空闲时只多一个整数比较即跳过（不进入扫描循环）。
    private int _dropSlot = -1;

    private const double DropBarHeightRatio = 0.62;   // 竖线高占行高比（对齐分隔符的"明显短于图标行"）

    /// <summary>按下到进入拖拽的最小位移（DIP）：小于它视作点击（走激活），不启动拖放。</summary>
    private const double DragStartThresholdDip = 5.0;

    private DockItem? _dragCandidate;   // 左键按下的可拖条目（Pinned/Window）；未达阈值前只是候选
    private Point _dragStartPoint;
    private bool _dragging;

    // ---------- 拖放协作点（由 DockWindow 注入策略；渲染器不决定"该转成固定还是取消"）----------
    /// <summary>进入拖拽态：DockWindow 收起悬停预览（拖动的图标不该同时弹预览）。</summary>
    public Action<DockItem>? DragStarted { get; set; }
    /// <summary>拖拽中释放仍在条带内：给出被拖条目 + 落点探针（固定块槽位 / 是否压在回收站上）。</summary>
    public Action<DockItem, DropProbe>? ItemDroppedInside { get; set; }
    /// <summary>拖拽中释放越出条带（"甩出去"）：DockWindow 据此取消固定（仅对固定项有意义）。</summary>
    public Action<DockItem>? ItemDraggedOut { get; set; }

    /// <summary>是否正在内部拖拽：DockWindow 的预览 MouseMove 据此早退，避免拖动时缩略图乱弹。</summary>
    public bool IsDragging => _dragging;

    /// <summary>
    /// 一次落点探测的结果（值类型，扫描 _items 一趟即得，零堆分配）。
    /// </summary>
    /// <param name="PinnedSlot">固定块插入槽（0..固定项数，光标越界即钳到端点）。</param>
    /// <param name="OverTrash">光标是否正压在回收站格上（任何落此的拖放都是"删除？不"的 no-op）。</param>
    /// <param name="WithinPinnedRegion">光标水平位置是否落在固定块区间内（决定窗口项拖入是否转固定）。</param>
    public readonly record struct DropProbe(int PinnedSlot, bool OverTrash, bool WithinPinnedRegion);

    public DockSurface()
    {
        ClipToBounds = false;                       // 放大后允许画出自身 bounds（顶部余量区）
        Background = System.Windows.Media.Brushes.Transparent;  // 面板区域内空白也参与命中（MouseMove 唤醒循环）
        IsHitTestVisible = true;
        SnapsToDevicePixels = false;

        // 指示条一次性入可视树（不参与逐帧排布、不命中）；accent 随主题字典动态解析。
        _dropBar.SetResourceReference(Shape.FillProperty, "ThemeAccent");
        Children.Add(_dropBar);

        MouseMove += (_, _) => Wake();              // 仅作"醒来"信号：真实位置每帧由 CursorProvider 取
        MouseEnter += (_, _) => Wake();
        MouseLeftButtonDown += OnDragMouseDown;
        MouseMove += OnDragMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        // 【终审修正】捕获被外力剥夺（系统弹模态框/另一窗口强制捕获/Alt+Tab 切走）时 WPF **不会**再发
        // MouseLeftButtonUp：没有这一句，_dragging 永久为真（DockWindow 的悬停预览 MouseMove 据此早退，
        // 预览功能当场死掉直到下次点击），插入指示条也会一直画在行里 = 分层窗口上一道除点击外无法消失的
        // accent 竖线。释放捕获即等价于"这次拖拽取消了"。
        LostMouseCapture += (_, _) => { if (_dragging) FinishDrag(); };
        PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        Unloaded += (_, _) => StopLoop();
    }

    // ---------- 依赖属性 ----------

    /// <summary>条目源（ObservableCollection{DockItem}；集合变更自动重建可视树）。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList), typeof(DockSurface),
        new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>图标基准边长（DIP，主题 IconSize）。</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(44.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnGeometryParameterChanged));

    /// <summary>条目间距（DIP，主题 ItemSpacing）。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnGeometryParameterChanged));

    /// <summary>
    /// 行高在图标之上的余量（DIP，主题 DockRowInsetDip；图标行内垂直居中 → 上下各一半）。
    /// 【v3 T4】它原本是写死的常量 20，于是三种主题的"面板厚度"完全一样且远超图标本身
    /// （IconSize=44 → 行 64 / 面板 76，用户看到的黑框）。主题参数才是正解：macOS 原生 Dock 紧贴图标
    /// （10），GNOME dash 稍松（16），鸿蒙胶囊最松（12）。变更走 AffectsMeasure，行高随之变 →
    /// DockWindow 的可见面板高/预留带/放大余量全部读同一来源（<see cref="RowHeightDip"/>）。
    /// </summary>
    public static readonly DependencyProperty RowInsetDipProperty = DependencyProperty.Register(
        nameof(RowInsetDip), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(DefaultRowInsetDip, FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnGeometryParameterChanged));

    /// <summary>正对光标项的最大倍率（1 = 等效关闭放大）。</summary>
    public static readonly DependencyProperty MaxScaleProperty = DependencyProperty.Register(
        nameof(MaxScale), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(1.0, OnFieldParameterChanged));

    /// <summary>
    /// 高斯衰减半径，单位 = "一个图标间距"（IconSize+Spacing）。
    /// 【与 v1 的关系（评审发现：注释撒谎）】v1 的 <c>Magnifier</c> 用的是 <c>exp(-(d/σ)²)</c>，
    /// Core 的 <see cref="MagnificationField"/> 是标准差形式 <c>exp(-d²/(2σ²))</c>：同一个数值 2.0，
    /// 本实现的实际影响半径比 v1 **宽 √2 倍（约 41%）**，落到面板上就是底板鼓包（AutoWidth 生长）
    /// 约宽 26%。这里刻意保留 2.0：半径更宽 → 相邻项倍率差更小 → 斜率更缓，正好服务于"别看起来粗糙"；
    /// 若日后要 1:1 复刻 v1 视觉尺度，把 DockWindow 的 SigmaInPitches 改成 2.0/√2 ≈ 1.4142 即可。
    /// </summary>
    public static readonly DependencyProperty SigmaProperty = DependencyProperty.Register(
        nameof(Sigma), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(2.0, OnFieldParameterChanged));

    /// <summary>主题是否启用放大（关闭时场恒回落 1，仍走阻尼而非冻结）。</summary>
    public static readonly DependencyProperty ShowMagnificationProperty = DependencyProperty.Register(
        nameof(ShowMagnification), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(true, OnFieldParameterChanged));

    /// <summary>
    /// 底板宽是否随放大总宽生长（默认 true，macOS 行为）。
    /// false 时面板恒为未放大宽度，条目以自身中心对称外扩（可越出面板）。
    /// </summary>
    public static readonly DependencyProperty AutoWidthProperty = DependencyProperty.Register(
        nameof(AutoWidth), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// 图标倒影开关（Task 15 起**有渲染器**）：开 = 每个带图标的条目下方挂一条镜像渐隐倒影
    /// （VisualBrush 实时引用图标 + 冻结垂直透明遮罩），行高随之加长（见 <see cref="RowHeightDip"/>）；
    /// 关 = 摘除倒影元素、行高复原。刷树时 <see cref="SyncReflections"/> 一次性建/删元素，
    /// 动画循环逐帧零新增分配（倒影随容器 ScaleTransform 一起缩放，不需要独立逐帧写入）。
    /// </summary>
    public static readonly DependencyProperty ReflectionEnabledProperty = DependencyProperty.Register(
        nameof(ReflectionEnabled), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnReflectionEnabledChanged));

    public IList? ItemsSource
    {
        get => (IList?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>行高在图标之上的余量（DIP，主题驱动，见 <see cref="RowInsetDipProperty"/>）。</summary>
    public double RowInsetDip
    {
        get => (double)GetValue(RowInsetDipProperty);
        set => SetValue(RowInsetDipProperty, value);
    }

    public double MaxScale
    {
        get => (double)GetValue(MaxScaleProperty);
        set => SetValue(MaxScaleProperty, value);
    }

    public double Sigma
    {
        get => (double)GetValue(SigmaProperty);
        set => SetValue(SigmaProperty, value);
    }

    public bool ShowMagnification
    {
        get => (bool)GetValue(ShowMagnificationProperty);
        set => SetValue(ShowMagnificationProperty, value);
    }

    public bool AutoWidth
    {
        get => (bool)GetValue(AutoWidthProperty);
        set => SetValue(AutoWidthProperty, value);
    }

    public bool ReflectionEnabled
    {
        get => (bool)GetValue(ReflectionEnabledProperty);
        set => SetValue(ReflectionEnabledProperty, value);
    }

    /// <summary>
    /// 条目行高（DIP）= IconSize + 主题行内余量 + 倒影余量（仅 ReflectionEnabled 时非零）。
    /// 【DockWindow 条带几何的唯一来源】可见面板高 = 本值 + 2×主题竖向内边距，
    /// 倒影开启时条带随之一致加高，AppBar 协商仍贴底无缝。算术本体在 Core（<c>DockRowMath</c>，可单测），
    /// 这里只提供"当前控件参数"这一层。
    /// </summary>
    public double RowHeightDip => DockRowMath.RowHeightDip(IconSize, RowInsetDip, ReflectionEnabled,
                                                            ReflectionGapDip, ReflectionHeightRatio);

    // ---------- 外部协作点（由 DockWindow 注入） ----------

    /// <summary>条目模板选择器（复用 DockWindow 的 DefaultItemTemplate / MediaItemTemplate，主题化不受影响）。</summary>
    public DataTemplateSelector? TemplateSelector { get; set; }

    /// <summary>无选择器时的兜底模板。</summary>
    public DataTemplate? ItemTemplate { get; set; }

    /// <summary>
    /// 光标取样委托：返回光标 X（**本控件本地 DIP**）；返回 null = 光标不在有效区域（场回落 1）。
    /// 由 DockWindow 注入：内部用 Win32 GetCursorPos 取真实物理光标并过 AppBar 矩形守卫，
    /// 因此 WPF 布局变动合成的陈旧 MouseMove 永远无法把放大目标钉住（v1 的卡死根因之一）。
    /// </summary>
    public Func<double?>? CursorProvider { get; set; }

    /// <summary>左键命中条目（启动 / 激活 / 最小化 / 胶囊展开由 DockWindow 处理）。</summary>
    public Action<DockItem>? Activated { get; set; }

    /// <summary>右键命中条目 + 事件点（本控件本地坐标）；条目固定/取消固定菜单由 DockWindow 打开。</summary>
    public Action<DockItem, Point>? ContextRequested { get; set; }

    // ---------- 布局 / 可视树 ----------

    private void HookItemsSourceNotifications(IList? oldSource, IList? newSource)
    {
        if (oldSource is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= OnCollectionChanged;
        if (newSource is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += OnCollectionChanged;
        RebuildVisualTree();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DockSurface)d).HookItemsSourceNotifications(e.OldValue as IList, e.NewValue as IList);

    private static void OnGeometryParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (DockSurface)d;
        s.UpdateReflectionGeometry();   // 倒影高度/下探量随 IconSize 走（RowHeightDip 自动含新倒影余量）
        s._layoutDirty = true;
        s.InvalidateMeasure();
        s.Wake();
    }

    private static void OnFieldParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DockSurface)d).Wake();

    private static void OnReflectionEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (DockSurface)d;
        s.SyncReflections();          // 增删倒影元素（一次性，非逐帧路径）
        s._layoutDirty = true;        // 行高/倒影几何随开关变化重排；DockWindow 侧由调用方触发条带重排
        s.InvalidateMeasure();
        s.Wake();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 标题/图标刷新（Replace）只换 DataContext：避免整树重建带来的闪烁与生成开销
        if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems is not null)
        {
            for (var k = 0; k < e.NewItems.Count; k++)
            {
                var idx = e.NewStartingIndex + k;
                if (idx < 0 || idx >= _containers.Count) continue;
                if (_containers[idx] is { } fe) fe.DataContext = e.NewItems[k];
                _items[idx] = e.NewItems[k] as DockItem;
            }
            _layoutDirty = true;   // 长标题/胶囊宽度可能变化 → 基准中心重算
            InvalidateMeasure();
            Wake();
            return;
        }
        RebuildVisualTree();
    }

    /// <summary>重建可视树：模板由选择器分派，容器按引用缓存，逐帧不再向生成器要容器。</summary>
    private void RebuildVisualTree()
    {
        foreach (var fe in _containers)
            if (fe is not null) fe.SizeChanged -= OnContainerSizeChanged;

        Children.Clear();
        _containers.Clear();
        _items.Clear();
        _reflections.Clear();
        _badgeElements.Clear();   // 容器已销毁：映射会在下面按 _badgeTexts 重新挂回
        _progressTracks.Clear();
        _progressFills.Clear();

        var items = ItemsSource;
        if (items is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as DockItem;
                _items.Add(item);
                FrameworkElement? fe = null;
                var tpl = item is null ? null : (TemplateSelector?.SelectTemplate(item, this) ?? ItemTemplate);
                if (item is not null && tpl is not null && tpl.LoadContent() is FrameworkElement root)
                {
                    fe = root;
                    fe.DataContext = item;
                    fe.Margin = new Thickness(0);   // 条目间距统一由 Spacing 给出（模板内的旧 Margin 归零）
                    fe.RenderTransformOrigin = new Point(0.5, 1);  // 底部中心缩放：图标"站在"面板底面上长大
                    fe.RenderTransform = new ScaleTransform(1.0, 1.0);
                    fe.SizeChanged += OnContainerSizeChanged;
                    Children.Add(fe);               // Panel.Children 负责可视/逻辑父子关系
                    AttachBadge(fe, item.Id);       // 已知角标（换源/重建后）跟着新容器一起挂回去
                    AttachProgress(fe, item.Id);    // 进度条同理
                }
                _containers.Add(fe);
            }
        }

        // 倒影元素随树重建重挂（容器是新对象，旧倒影已随 Children.Clear 消失，_reflections 已清空为占位）
        SyncReflections();

        // 指示条是常驻元素，不随条目走：Children.Clear() 把它一并摘掉了，这里重新挂回最上层（后 Add = 后画）。
        Children.Add(_dropBar);

        _structureDirty = true;   // 条目增删/换源：整树重建 → 场状态一并重置
        _layoutDirty = true;
        InvalidateMeasure();
        Wake();
    }

    /// <summary>
    /// 按开关与当前容器同步倒影元素（Task 15）：每个"模板根为 Grid + 含 Image + 条目有图标"的项，
    /// 在其 Grid 里挂一条底边对齐、负 Margin 探出图标下方的镜像 Border（VisualBrush 实时引用该 Image，
    /// 图标换源自动跟随；ScaleY=-1 镜像 + 冻结垂直渐隐遮罩）。媒体胶囊/回退字形项（Icon=null）不挂。
    /// 只在开关变更/树重建时调用——VisualBrush/遮罩每元素建一次，动画循环零新增分配。
    /// </summary>
    private void SyncReflections()
    {
        var n = _containers.Count;
        for (var i = 0; i < _reflections.Count; i++)
            if (_reflections[i]?.Parent is Panel host) host.Children.Remove(_reflections[i]!);
        _reflections.Clear();

        if (!ReflectionEnabled)
        {
            for (var i = 0; i < n; i++) _reflections.Add(null);
            return;
        }
        for (var i = 0; i < n; i++)
        {
            _reflections.Add(null);
            if (_containers[i] is not Grid grid || _items[i] is not { } item || item.Icon is null) continue;
            Image? icon = null;
            foreach (var child in grid.Children)
                if (child is Image im) { icon = im; break; }
            if (icon is null) continue;

            var h = IconSize * ReflectionHeightRatio;
            var reflection = new Border
            {
                Height = h,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                // 负下 Margin 让倒影探出容器底边（容器不裁剪：Canvas ClipToBounds=false、Grid 默认不裁），
                // 探出部分由加长后的行高（RowHeightDip）容纳，指示点在图标内下方仍压在图标上、不被倒影遮断。
                Margin = new Thickness(0, 0, 0, -(h + ReflectionGapDip)),
                Background = new VisualBrush { Visual = icon, Stretch = Stretch.Fill },
                OpacityMask = ReflectionMask,
                RenderTransform = new ScaleTransform(1.0, -1.0),   // 垂直镜像
                RenderTransformOrigin = new Point(0.5, 0.5),
                IsHitTestVisible = false,                          // 不参与命中：点击热区仍是图标本体
            };
            grid.Children.Add(reflection);
            _reflections[i] = reflection;
        }
    }

    // ---------- 角标（Phase 7-T2） ----------

    /// <summary>
    /// 按"进程 exe 文件名（小写、含扩展名）→ 角标文字"应用角标；不在表里的条目清空角标。
    /// <para>
    /// 键用 exe 文件名而不是完整路径：同一应用可能从不同版本目录启动（WindowsApps 的桩与实体路径），
    /// 按整路径匹配会漏；而不同应用同名 exe 的概率远小于此。固定项与窗口项都能命中同一份表——
    /// macOS 的角标语义就是"这个应用有几条未读"，与它是钉住的还是正在跑的无关。
    /// </para>
    /// </summary>
    public void ApplyBadges(IReadOnlyDictionary<string, string> badgeByProcess)
    {
        foreach (var item in _items)
        {
            if (item is null || item.Kind == DockItemKind.Separator)
            {
                continue;
            }

            var exe = ExecutableKeyOf(item);
            var text = exe.Length > 0 && badgeByProcess.TryGetValue(exe, out var value) ? value : null;
            SetBadge(item.Id, text);
        }
    }

    /// <summary>给某条目设置角标（null/空 = 清空）。</summary>
    public void SetBadge(string itemId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _badgeTexts.Remove(itemId);
        }
        else
        {
            _badgeTexts[itemId] = text;
        }

        if (_badgeElements.TryGetValue(itemId, out var element) && element is Border border
            && border.Child is TextBlock label)
        {
            if (string.IsNullOrEmpty(text))
            {
                border.Visibility = Visibility.Collapsed;
                return;
            }

            label.Text = text;
            border.Visibility = Visibility.Visible;
            return;
        }

        // 容器已存在但还没挂角标（例如诊断入口在树建好之后才设置）：就地补挂一次。
        // 这一步必须在这里做，否则"设置了却看不见"——真机截图也看不出来，只有单测/日志能吵。
        if (!string.IsNullOrEmpty(text))
        {
            var index = _items.FindIndex(item => string.Equals(item?.Id, itemId, StringComparison.Ordinal));
            if (index >= 0 && index < _containers.Count && _containers[index] is { } container)
            {
                AttachBadge(container, itemId);
            }
        }
    }

    /// <summary>把条目对应的 exe 文件名折成键（小写）；拿不到返回空串。</summary>
    private static string ExecutableKeyOf(DockItem item)
    {
        var candidate = item.Kind == DockItemKind.Window ? item.IconSource : item.IconSource ?? item.LaunchPath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        try
        {
            var name = System.IO.Path.GetFileName(candidate);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.ToLowerInvariant()
                : string.Empty;   // .lnk / 目录 / shell: 之类没有 exe 语义，不参与角标
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>若该条目有已知角标，就在它的模板根 Grid 里挂一个右上角胶囊。</summary>
    private void AttachBadge(FrameworkElement container, string? itemId)
    {
        if (itemId is null || container is not Grid grid)
        {
            return;
        }

        if (!_badgeTexts.TryGetValue(itemId, out var text) || text.Length == 0)
        {
            return;
        }

        var label = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = BadgeBrush(),
            MinWidth = 16,
            Height = 16,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // 右上角、略微探出图标：macOS 的角标是压在图标边上的，不是嵌在图标里
            Margin = new Thickness(0, -2, -4, 0),
            IsHitTestVisible = false,
            Child = label,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.5,
                Color = Colors.Black
            }
        };

        grid.Children.Add(badge);
        _badgeElements[itemId] = badge;
        Logger.Info($"Dock 角标：已挂 {text}");
    }

    // ---------- 进度条（Phase 7-T1） ----------

    /// <summary>
    /// 按"进程 exe 文件名 → (进度, 状态)"应用图标下沿的进度条；不在表里的条目清除。
    /// 与角标同一套 exe 键规则（不同启动路径的同一应用能命中）。
    /// </summary>
    public void ApplyProgress(
        IReadOnlyDictionary<string, (double Fraction, DockProgressState State)> byProcess)
    {
        foreach (var item in _items)
        {
            if (item is null || item.Kind == DockItemKind.Separator)
            {
                continue;
            }

            var exe = ExecutableKeyOf(item);
            if (exe.Length > 0 && byProcess.TryGetValue(exe, out var value))
            {
                SetProgress(item.Id, value.Fraction, value.State);
            }
            else
            {
                SetProgress(item.Id, 0, DockProgressState.None);
            }
        }
    }

    /// <summary>给某条目设置进度（<see cref="DockProgressState.None"/> 或非法值 = 隐藏）。</summary>
    public void SetProgress(string itemId, double fraction, DockProgressState state)
    {
        if (state == DockProgressState.None)
        {
            _progressValues.Remove(itemId);
        }
        else
        {
            _progressValues[itemId] = (DockProgressBar.Clamp(fraction), state);
        }

        if (_progressTracks.TryGetValue(itemId, out var track))
        {
            ApplyProgressVisual(track, itemId);
            return;
        }

        if (state != DockProgressState.None)
        {
            var index = _items.FindIndex(item => string.Equals(item?.Id, itemId, StringComparison.Ordinal));
            if (index >= 0 && index < _containers.Count && _containers[index] is { } container)
            {
                AttachProgress(container, itemId);
            }
        }
    }

    /// <summary>若该条目有已知进度，就在模板根 Grid 的底部挂一条细进度带。</summary>
    private void AttachProgress(FrameworkElement container, string? itemId)
    {
        if (itemId is null || container is not Grid grid)
        {
            return;
        }

        if (!_progressValues.ContainsKey(itemId))
        {
            return;
        }

        var trackWidth = Math.Max(12, IconSize * DockProgressBar.WidthRatio);
        var fill = new Border
        {
            Width = 0,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(1.5)
        };

        var track = new Border
        {
            Width = trackWidth,
            Height = DockProgressBar.HeightDip,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            IsHitTestVisible = false,
            Child = fill,
            // 与角标一致的轻投影：白条压在彩色图标上时要有边界，否则看着像图标本身的一部分
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 3, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black
            }
        };

        grid.Children.Add(track);
        _progressTracks[itemId] = track;
        _progressFills[itemId] = fill;
        ApplyProgressVisual(track, itemId);
    }

    private void ApplyProgressVisual(Border track, string itemId)
    {
        if (!_progressValues.TryGetValue(itemId, out var value) || !_progressFills.TryGetValue(itemId, out var fill))
        {
            track.Visibility = Visibility.Collapsed;
            return;
        }

        track.Visibility = Visibility.Visible;
        var width = track.Width;
        fill.Background = ProgressBrush(value.State);
        fill.Width = value.State == DockProgressState.Indeterminate
            ? width * DockProgressBar.IndeterminateSegment   // 静态段：不做动画（本项只做"看得见进度"）
            : width * DockProgressBar.Clamp(value.Fraction);
    }

    /// <summary>进度条颜色：正常白、出错红、暂停黄（与 Windows 任务栏语义一致）。</summary>
    private static Brush ProgressBrush(DockProgressState state)
    {
        var color = state switch
        {
            DockProgressState.Error => Color.FromRgb(0xE8, 0x11, 0x23),
            DockProgressState.Paused => Color.FromRgb(0xFF, 0xB9, 0x00),
            _ => Color.FromRgb(0xFF, 0xFF, 0xFF)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>角标底色：macOS 的红色（比系统强调色更"提醒"）。冻结共享，避免每个角标一份 brush。</summary>
    private static Brush BadgeBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        brush.Freeze();
        return brush;
    }

    /// <summary>图标尺寸变更后刷新已挂倒影的高度/下探量（低频路径：滑杆/主题切换）。</summary>
    private void UpdateReflectionGeometry()
    {
        if (!ReflectionEnabled) return;
        var h = IconSize * ReflectionHeightRatio;
        for (var i = 0; i < _reflections.Count; i++)
            if (_reflections[i] is Border b)
            {
                b.Height = h;
                b.Margin = new Thickness(0, 0, 0, -(h + ReflectionGapDip));
            }
    }

    /// <summary>模板内宽度变化（媒体胶囊有/无会话切换 Width=0↔124）→ 基准排布重算，不留空隙、不重置缩放状态。</summary>
    private void OnContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _layoutDirty = true;
        InvalidateMeasure();
        Wake();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = _containers.Count;
        var constraint = new Size(double.PositiveInfinity, IconSize);
        for (var i = 0; i < n; i++)
            _containers[i]?.Measure(constraint);   // 已量过且约束未变 → WPF 内部短路，几乎零成本
        _dropBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));   // 常驻指示条：非约束量，尊重其动态 Height

        if (_layoutDirty || _widths.Length != n) RecomputeBaseLayout();

        _rowHeight = RowHeightDip;   // 倒影开启时自动加长（DockRowMath.ReflectionExtraDip），DockWindow 条带读同一来源
        // AutoWidth：期望尺寸 = 当前放大总宽（逐帧由 ApplyGeometry 触发 InvalidateMeasure）；
        // 否则恒为未放大总宽 → 放大过程完全不触发布局。
        var width = AutoWidth ? _naturalWidth + CurrentExtraWidth() : _naturalWidth;
        _lastReportedTotal = width;
        return new Size(Math.Max(0, width), _rowHeight);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _rowHeight = Math.Max(arrangeSize.Height, _rowHeight);
        ApplyGeometry();     // 首帧/尺寸变化时把几何写一遍，其后交给渲染循环逐帧覆写
        return base.ArrangeOverride(arrangeSize);
    }

    /// <summary>
    /// 未放大基准排布：左缘/中心/可见项场中心。仅在集合或模板尺寸变化时重算。
    /// <para>
    /// 【分隔符】宽度照常占位（行进宽），但 <c>DockSeparatorPlan.IsMagnifiable</c> 给 false →
    /// 中心不进放大场、下标映射里也没有它 → 它的目标缩放恒 1：邻居图标长大时竖线不被拉伸，
    /// 也不作为"可拉伸图标"参与前缀和推挤（相邻像素间距仍恒等于 Spacing，见 Core 单测）。
    /// </para>
    /// </summary>
    private void RecomputeBaseLayout()
    {
        var n = _containers.Count;
        var structural = _structureDirty;
        if (_structureDirty)
        {
            // 整树重建：条目与下标的对应关系可能整体平移，旧缩放状态一律作废（brief 要求）
            _cur = new double[n]; _tgt = new double[n];
            _baseLefts = new double[n]; _widths = new double[n];
            _heights = new double[n]; _lefts = new double[n]; _layoutLefts = new double[n];
            _bounceRemaining = new double[n]; _bounceY = new double[n];   // 弹跳随树作废（下标映射已失效）
            Array.Fill(_cur, 1.0);
            Array.Fill(_tgt, 1.0);
        }
        else
        {
            _cur = EnsureLength(_cur, n, 1.0);
            _tgt = EnsureLength(_tgt, n, 1.0);
            _baseLefts = EnsureLength(_baseLefts, n, 0.0);
            _widths = EnsureLength(_widths, n, 0.0);
            _heights = EnsureLength(_heights, n, 0.0);
            _lefts = EnsureLength(_lefts, n, 0.0);
            _layoutLefts = EnsureLength(_layoutLefts, n, 0.0);
            _bounceRemaining = EnsureLength(_bounceRemaining, n, 0.0);
            _bounceY = EnsureLength(_bounceY, n, 0.0);
        }
        _structureDirty = false;

        // 场缓冲区按条目数一次性给足容量（Core 只填前 FieldCount 项）：条目增删时不重新精确分配，
        // 逐帧路径读的也只有那 FieldCount 项 → 既零分配又不会读到陈旧尾巴。
        _magnifiable = EnsureLength(_magnifiable, n, true);
        _fieldCenters = EnsureLength(_fieldCenters, n, 0.0);
        _fieldToItem = EnsureIntLength(_fieldToItem, n);
        if (_fieldScales.Length < n) _fieldScales = new double[Math.Max(1, n)];

        for (var i = 0; i < n; i++)
        {
            var fe = _containers[i];
            var w = fe?.DesiredSize.Width ?? 0;
            if (double.IsNaN(w) || w < 0) w = 0;
            _widths[i] = w;
            _heights[i] = fe?.DesiredSize.Height ?? 0;
            // 非 DockItem 的未知源按图标处理（宁可多算一场也不让条目画歪）；分隔符明确排除出放大场
            var item = _items[i];
            _magnifiable[i] = item is null || DockSeparatorPlan.IsMagnifiable(item.Kind);
        }

        var layout = DockRowLayout.Compute(_widths, _magnifiable, Spacing, MinVisibleWidthDip,
                                           _baseLefts, _fieldCenters, _fieldToItem);
        _naturalWidth = layout.NaturalWidth;
        _fieldCount = layout.FieldCount;
        _layoutDirty = false;
        _settled = false;
        if (structural)
            Logger.Info($"DockSurface 排布: 容器={n} 可见={layout.VisibleCount} 入场={_fieldCount} 自然宽={_naturalWidth:F0} " +
                        $"IconSize={IconSize} Spacing={Spacing} RowInset={RowInsetDip} MaxScale={MaxScale} ShowMag={ShowMagnification}");
    }

    private static double[] EnsureLength(double[] arr, int n, double fill)
    {
        if (arr.Length == n) return arr;
        var next = new double[n];
        Array.Fill(next, fill);
        Array.Copy(arr, next, Math.Min(arr.Length, n));
        return next;
    }

    /// <summary>bool 版缓冲增长：新位补"可放大"（默认按图标处理，宁可多算一场也不让条目画歪）。</summary>
    private static bool[] EnsureLength(bool[] arr, int n, bool fill)
    {
        if (arr.Length == n) return arr;
        var next = new bool[n];
        Array.Fill(next, fill);
        Array.Copy(arr, next, Math.Min(arr.Length, n));
        return next;
    }

    private static int[] EnsureIntLength(int[] arr, int n)
    {
        if (arr.Length == n) return arr;
        var next = new int[n];
        Array.Copy(arr, next, Math.Min(arr.Length, n));
        return next;
    }

    private double CurrentExtraWidth()
    {
        var acc = 0.0;
        for (var i = 0; i < _cur.Length; i++) acc += (_cur[i] - 1.0) * _widths[i];
        return acc;
    }

    // ---------- 动画循环（vsync） ----------

    /// <summary>启动渲染钩子（若未启动）并要求下一帧真正干活。</summary>
    private void Wake()
    {
        _settled = false;
        _lastCursorX = double.NaN;   // 位置未知，下一帧必须取样
        if (_hooked) return;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private void StopLoop()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    /// <summary>外部（窗口关闭/隐藏）停驱：空闲时必须彻底摘掉 Rendering 钩子。</summary>
    public void Shutdown() => StopLoop();

    /// <summary>
    /// 启动该条目的纵向弹跳（Task 15，BounceEnabled 的渲染入口，由 DockWindow 启动固定应用时调用）。
    /// 复用现有 CompositionTarget.Rendering 循环驱动（不加第二个计时器）；
    /// 弹跳进行中重复点击 = **重置**进度重跳一次，绝不叠加（剩余时长直接置回满值）。
    /// </summary>
    public void StartBounce(DockItem item)
    {
        if (!DockSeparatorPlan.IsInteractive(item.Kind)) return;   // 分隔符不弹（不是图标）
        var i = _items.IndexOf(item);
        if (i < 0 || i >= _bounceRemaining.Length) return;
        _bounceRemaining[i] = BounceDurationSeconds;
        Wake();
    }

    /// <summary>
    /// 逐帧推进弹跳：剩余时长减 dt，偏移取 <see cref="AnimationMath.BounceOffset"/>（负=上移）。
    /// 返回"全部弹跳是否已了结"——了结者偏移归零，未了结者令本帧不得判为收敛（钩子不许提前摘除）。
    /// 只在有活动弹跳的 ~600ms 内每帧多跑一趟 O(n) 标量循环，零分配。
    /// </summary>
    private bool AdvanceBounces(double dt)
    {
        var idle = true;
        for (var i = 0; i < _bounceRemaining.Length; i++)
        {
            if (_bounceRemaining[i] <= 0.0)
            {
                if (_bounceY[i] != 0.0) _bounceY[i] = 0.0;
                continue;
            }
            var remaining = _bounceRemaining[i] - dt;
            if (remaining <= 0.0)
            {
                _bounceRemaining[i] = 0.0;
                _bounceY[i] = 0.0;
                continue;
            }
            _bounceRemaining[i] = remaining;
            _bounceY[i] = AnimationMath.BounceOffset(1.0 - remaining / BounceDurationSeconds,
                                                     Math.Min(IconSize * BounceAmplitudeRatio, BounceMaxAmplitudeDip));
            idle = false;
        }
        return idle;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;

        // 守卫（Task 5 minor）：dt 为负（时钟回拨/异常）会让 Damp 的 e^(-λdt) 变成放大因子 → 数值发散，夹到 0；
        // dt 过大（>0.25s：机器休眠、调试断点、VM 长时间掉帧）不再逐帧追，直接吸附目标。
        if (dt < 0) dt = 0;
        var stalled = dt > MaxFrameStepSeconds;

        // 光标取样 → 未放大基准坐标（见 ToBaseCoordinate），并按**条目行的水平影响范围**判活：
        // 承载窗口是 AppBar 全宽条带（含两侧透明区与顶部余量），只按窗口矩形判活的话，
        // 光标停在条带任意位置（任务栏/托盘一带）钩子就永不摘除——GetCursorPos/TranslatePoint/场计算
        // 每帧白跑（评审发现 #1，违反"收敛后停止渲染钩子（不空转）"）。
        // 行外一律视为无悬停目标：走回落→收敛→摘钩的正常路径，光标重新进入行范围时由
        // MouseMove/MouseEnter（或本钩子在摘钩前的一帧）重新唤醒；"光标瞬移出条带没有 MouseLeave"
        // 的老场景同样在这条路径上自我了断。
        var cursorX = CursorProvider?.Invoke();
        var baseX = double.NaN;
        var active = false;
        if (cursorX is { } x && ShowMagnification && MaxScale > 1.0 && _fieldCount > 0)
        {
            baseX = ToBaseCoordinate(x);
            // NaN 守卫：WPF 布局期间 visual offset 未就绪时 TranslatePoint 可能返回 NaN，
            // 而 CloseEnough(NaN, x) == false → 会把 NaN 永久钉进 _cur（几何全废），故只在有限值时判活。
            active = !double.IsNaN(baseX) && WithinRowExtent(baseX);
        }

        // 无悬停目标且已全部收敛 → 摘钩（空闲零开销）
        if (!active && _settled && !_layoutDirty)
        {
            StopLoop();
            return;
        }
        // 光标没动 + 已收敛 + 结构不脏 → 本帧无事可做（仍挂着钩子，以便光标一离开立刻回落）
        if (active && _settled && !_layoutDirty && cursorX == _lastCursorX) return;

        ComputeTargets(baseX, active);

        var settled = true;
        for (var i = 0; i < _cur.Length; i++)
        {
            var cur = _cur[i];
            var tgt = _tgt[i];
            if (AnimationMath.CloseEnough(cur, tgt)) continue;
            var next = stalled ? tgt : AnimationMath.Damp(cur, tgt, DampLambda, dt);
            // 尾部吸附：离目标足够近就直接落到目标，避免钩子因 1e-5 级残差反复唤醒
            _cur[i] = AnimationMath.CloseEnough(next, tgt, 5e-4) ? tgt : next;
            if (!AnimationMath.CloseEnough(_cur[i], tgt)) settled = false;
        }
        _settled = settled;
        _lastCursorX = cursorX ?? double.NaN;
        var dampEnd = Stopwatch.GetTimestamp();

        // 启动弹跳与放大场共用这条 vsync 循环：未了结的弹跳强制本帧不算收敛（钩子不许提前摘）。
        if (!AdvanceBounces(dt)) _settled = false;

        ApplyGeometry();

        if (FrameLogEnabled)
        {
            // 诊断字段：frametime.ps1 的正则 BEAUTIFUL_FRAME (\d+) 只取第一个数字，附加字段不影响解析。
            // damp_us = 光标取样 + 场计算 + 阻尼；geom_us = 写 Left/Top/RenderTransform（含底板宽度失效）。
            // 用来区分"本控件逐帧计算慢"与"环境渲染/合成慢"两种掉帧。
            var end = Stopwatch.GetTimestamp();
            var tgtMax = 1.0;
            for (var i = 0; i < _tgt.Length; i++) tgtMax = Math.Max(tgtMax, _tgt[i]);
            Logger.Info(FormattableString.Invariant($"BEAUTIFUL_FRAME {now} damp_us={(dampEnd - now) * 1000000.0 / Stopwatch.Frequency:F0} geom_us={(end - dampEnd) * 1000000.0 / Stopwatch.Frequency:F0} cursor={(cursorX ?? double.NaN):F1} tgt_max={tgtMax:F3}"));
        }

        // 摘钩判定用 _settled（含弹跳未了结的强制唤醒），不是仅看缩放收敛的局部 settled
        if (_settled && !active) StopLoop();
    }

    /// <summary>
    /// 光标（surface 本地 X）→ **未放大基准坐标系** X：AutoWidth 下面板随总宽生长、其左缘随之左移，
    /// 若直接用本地坐标，静止光标的本地 X 会自己漂移 → 放大峰值偏离光标，且每帧目标都在变（永不收敛）。
    /// 面板恒被父级居中，故 (本地 X − 当前宽/2 + 基准宽/2) 与生长量无关，静止光标即静止目标。
    /// </summary>
    private double ToBaseCoordinate(double localX)
    {
        var currentWidth = AutoWidth ? _naturalWidth + CurrentExtraWidth() : _naturalWidth;
        return localX - currentWidth / 2.0 + _naturalWidth / 2.0;
    }

    /// <summary>
    /// 基准坐标是否落在条目行的影响范围内：行左右各留一个"图标间距"的余量（高斯尾部 +
    /// 首末项半格宽度），再远就无可放大项 → 判为不活跃，让钩子收敛后摘除。
    /// </summary>
    private bool WithinRowExtent(double baseX)
    {
        var pitch = IconSize + Spacing;
        return baseX >= -pitch && baseX <= _naturalWidth + pitch;
    }

    /// <summary>目标 scale：连续高斯场（Core 纯函数，写入复用缓冲区）；无悬停目标时全 1。</summary>
    private void ComputeTargets(double baseX, bool active)
    {
        if (!active || double.IsNaN(baseX))
        {
            Array.Fill(_tgt, 1.0);
            return;
        }

        // sigmaDip：把"以图标间距为单位"的 σ 换算成场函数要的真实 DIP 半径
        var sigmaDip = Math.Max(1.0, Sigma * (IconSize + Spacing));
        // 只要 Scales：场函数用 MagnificationField.ComputeScalesInto 写进复用的 _fieldScales，
        // 逐帧零分配（旧写法每帧 new 两个数组 + 跑一轮用不到的 centered 平移，评审发现 #4）。
        // 项数取 _fieldCount（= 可见且可放大项数）：分隔符占位宽却不入场，缩放恒 1，
        // 于是"邻居长大时竖线不跟着拉伸、也不被当作可拉伸图标推挤"（Core DockRowLayout 单测）。
        // 几何由本控件按**阻尼后**的 scale 重新前缀和（见 ApplyGeometry），
        // 这样像素永远与当前帧的 scale 自洽（不会出现"目标已移位、图标还在缩放"的错位/重叠）。
        MagnificationField.ComputeScalesInto(baseX, _fieldCenters, _fieldCount, MaxScale, sigmaDip, _fieldScales);
        Array.Fill(_tgt, 1.0);
        for (var k = 0; k < _fieldCount; k++)
            _tgt[_fieldToItem[k]] = _fieldScales[k];
    }

    /// <summary>
    /// 逐帧几何写入：**只有** Canvas.Left / Canvas.Top / ScaleTransform 三个值。
    /// 不改 Margin、不用 LayoutTransform → 面板内不发生逐项测量排列。
    /// </summary>
    private void ApplyGeometry()
    {
        var n = _containers.Count;
        if (n == 0) return;
        // 缓冲区与条目数必须一致（MeasureOverride 通常已保证）：否则 Core 侧会因长度不足抛异常，
        // 逐帧路径抛异常 = 整排图标冻结，宁可多跑一次基准排布。
        if (_widths.Length != n || _lefts.Length != n || _layoutLefts.Length != n) RecomputeBaseLayout();

        // 绘制左缘 drawn = base + Σ_{k<i} extra_k - shift（相邻像素间距恒为 Spacing）；
        // 写给 Canvas.Left 的是 layout = drawn + extra_i/2，因为缩放原点是 (0.5, 1)：
        // 容器绕自身中心放大 extra_i/2，像素才正好落回 drawn（旧实现漏掉这一项 → 上升沿挤压、
        // 下降沿撑开、首项溢出底板、整行在背板里偏心，命中区也随之偏移，评审发现 #2）。
        var totalExtra = MagnificationGeometry.ComputeRowLefts(
            _baseLefts, _widths, _cur, !AutoWidth, _lefts, _layoutLefts);

        for (var i = 0; i < n; i++)
        {
            var fe = _containers[i];
            if (fe is null) continue;
            if (_widths[i] <= MinVisibleWidthDip)
            {
                if (fe.Visibility != Visibility.Collapsed) fe.Visibility = Visibility.Collapsed;
                continue;
            }
            if (fe.Visibility != Visibility.Visible) fe.Visibility = Visibility.Visible;

            SetLeft(fe, _layoutLefts[i]);
            // 行内垂直居中（底边在 drawn 行底 = (_rowHeight + h)/2）：缩放原点 0.5,1 → 底边不动、向上生长。
            // 旧写法 SetTop(_rowHeight - h) 把底边贴到 surface 下沿，而 surface 下沿比底板 Border 的
            // 内描边还低 1 DIP → 图标压线、运行指示点几乎贴着描边（v1 有约 9 DIP 余量，评审发现 #3）。
            // 叠加启动弹跳偏移（负=上移；无弹跳时恒 0，只多一次数组读，零分配）。
            SetTop(fe, (_rowHeight - _heights[i]) / 2.0 + (i < _bounceY.Length ? _bounceY[i] : 0.0));
            if (fe.RenderTransform is ScaleTransform st)
            {
                var s = _cur[i];
                if (!AnimationMath.CloseEnough(st.ScaleX, s)) st.ScaleX = st.ScaleY = s;  // 原地改值，不新建 Freezable
            }
        }

        // 底板宽度跟随：只失效本控件的 measure（父级 Grid/Border 随之重排一次），
        // 与 v1"逐帧改写每个容器 Margin 导致整排重排"不是一回事；关闭 AutoWidth 后此路径完全不执行。
        if (AutoWidth)
        {
            var total = _naturalWidth + totalExtra;
            if (Math.Abs(total - _lastReportedTotal) >= WidthFollowStepDip)
            {
                _lastReportedTotal = total;
                InvalidateMeasure();
            }
        }

        // 拖放进行中：指示条按最新绘制左缘每帧跟随放大推移（_dropSlot<0 时一次整数比较即跳过，空闲零成本）。
        if (_dropSlot >= 0) PositionDropBar(IndicatorXForSlot(_dropSlot));
    }

    /// <summary>条目底边所在 Y（surface 本地）：与 ApplyGeometry 的居中放置严格一致，命中测试/边界共用。</summary>
    private double RowBottomOf(int i) => (_rowHeight + _heights[i]) / 2.0;

    // ---------- 命中 / 交互 ----------

    /// <summary>
    /// 用**放大后**的绘制矩形做命中（drawn 左缘 + width·scale，纵向自条目底边向上），
    /// 取代 v1"最近基准中心"权宜法——连续场没有离散索引可抖，无需绕。
    /// 读的必须是 drawn（像素）左缘而非槽位左缘，否则放大态下 1.8× 图标左半会被判给邻居（评审发现 #2）。
    /// <para>
    /// 分隔符**不参与命中**：它占着行宽、却不是可交互条目（不悬停、不可点击），命中到它会把右键菜单/
    /// 预览/激活挂在一个细线上。命中返回 null 后，事件自然落到底板/桌面（面板区域内仍是透明命中区）。
    /// </para>
    /// </summary>
    public DockItem? HitTestItem(Point p)
    {
        for (var i = 0; i < _items.Count && i < _widths.Length; i++)
        {
            var item = _items[i];
            if (item is null || _widths[i] <= MinVisibleWidthDip) continue;
            if (!DockSeparatorPlan.IsInteractive(item.Kind)) continue;   // 分隔符：有宽度、无交互
            var w = _widths[i] * _cur[i];
            var left = _lefts[i];
            if (p.X < left || p.X >= left + w) continue;
            var h = _heights[i] * _cur[i];
            var bottom = RowBottomOf(i);
            if (p.Y > bottom || p.Y < bottom - h) continue;
            return item;
        }
        return null;
    }

    /// <summary>条目当前（放大后）**绘制**矩形（surface 本地坐标）；条目不存在/已收起返回 null（媒体面板定位用）。</summary>
    public Rect? GetItemBounds(DockItem item)
    {
        var i = _items.IndexOf(item);
        if (i < 0 || i >= _widths.Length || _widths[i] <= MinVisibleWidthDip) return null;
        var h = _heights[i] * _cur[i];
        var bottom = RowBottomOf(i);
        return new Rect(_lefts[i], bottom - h, _widths[i] * _cur[i], h);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        // 拖拽释放：优先于点击激活。甩出条带 → ItemDraggedOut（仅固定项会真的取消固定）；
        // 落在条带内 → ItemDroppedInside（携带落点探针，DockWindow 决定排序 / 转固定 / 回收站 no-op）。
        if (_dragging)
        {
            var dragged = _dragCandidate;
            FinishDrag();
            // _dragCandidate 为空只可能来自"拖拽进行中又按下一次左键且落点不可拖"（OnDragMouseDown 会把它
            // 清成 null 而 _dragging 仍为真）：本次只收状态机，绝不把 null 递给 DockWindow 的策略回调。
            if (dragged is not null)
            {
                if (IsInsideRow(p)) ItemDroppedInside?.Invoke(dragged, ProbeDrop(p));
                else ItemDraggedOut?.Invoke(dragged);
            }
            e.Handled = true;
            return;
        }
        _dragCandidate = null;
        var item = HitTestItem(p) ?? (e.Source as FrameworkElement)?.DataContext as DockItem;
        if (item is not null) Activated?.Invoke(item);
    }

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;   // 拖拽已接管鼠标：第二次的按下不得清空被拖条目（释放时要有对象可交代）
        var p = e.GetPosition(this);
        var item = HitTestItem(p);
        // 只有固定项 / 运行窗口项可拖；媒体胶囊 / 回收站 / 分隔符不启动拖拽（保持各自的点击/静态语义）。
        if (item is not { Kind: DockItemKind.Pinned or DockItemKind.Window }) { _dragCandidate = null; return; }
        _dragCandidate = item;
        _dragStartPoint = p;   // 先记候选，未越过阈值前不捕获鼠标（否则吞掉本次点击）
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStartPoint.X) < DragStartThresholdDip &&
                Math.Abs(p.Y - _dragStartPoint.Y) < DragStartThresholdDip) return;
            _dragging = true;
            CaptureMouse();               // 越出窗口也要收到 Move/Up，甩出检测与干净取消都依赖它
            DragStarted?.Invoke(_dragCandidate);
        }
        ShowInsertionIndicator(ProbeDrop(p).PinnedSlot);   // 复用常驻指示条，仅改几何，零分配
    }

    private void FinishDrag()
    {
        _dragCandidate = null;
        _dragging = false;
        ReleaseMouseCapture();
        HideInsertionIndicator();
    }

    /// <summary>释放点是否仍在条目行内（水平 ±1 图标间距容差、纵向在行高内）；否则视为"甩出 Dock"。</summary>
    private bool IsInsideRow(Point p)
    {
        var pitch = IconSize + Spacing;
        return p.X >= -pitch && p.X <= _naturalWidth + pitch && p.Y >= 0 && p.Y <= _rowHeight;
    }

    // ---------- 拖放指示条（常驻单元素，逐帧只改几何） ----------

    /// <summary>显示/移动插入指示条到固定块的第 slot 槽（0..固定项数）。slot 越界不钳——调用方已钳。</summary>
    public void ShowInsertionIndicator(int slot)
    {
        _dropSlot = slot;
        PositionDropBar(IndicatorXForSlot(slot));
        if (_dropBar.Visibility != Visibility.Visible) _dropBar.Visibility = Visibility.Visible;
        Wake();   // 拖拽期间保持钩子：放大场随光标推移时指示条每帧跟随（见 ApplyGeometry 末尾）
    }

    /// <summary>收起插入指示条（拖放完成/取消/离开一律走这里，绝不留残影）。</summary>
    public void HideInsertionIndicator()
    {
        _dropSlot = -1;
        if (_dropBar.Visibility != Visibility.Collapsed) _dropBar.Visibility = Visibility.Collapsed;
    }

    private void PositionDropBar(double x)
    {
        var h = _rowHeight * DropBarHeightRatio;
        _dropBar.Height = h;
        SetLeft(_dropBar, x - _dropBar.Width / 2.0);
        SetTop(_dropBar, (_rowHeight - h) / 2.0);   // 与分隔符/图标同一条垂直居中线
    }

    /// <summary>
    /// 落点探针：一次扫描给出固定块插入槽 / 是否压在回收站上 / 是否落在固定块区间内。
    /// 值类型返回，不重建条目列表、不分配（DragOver 连续触发时也安全）。
    /// </summary>
    public DropProbe ProbeDrop(Point p)
    {
        var count = 0;
        var slot = 0;
        var firstLeft = double.NaN;
        var lastRight = double.NaN;
        var pitch = IconSize + Spacing;
        for (var i = 0; i < _items.Count && i < _widths.Length; i++)
        {
            if (_items[i] is not { Kind: DockItemKind.Pinned } || _widths[i] <= MinVisibleWidthDip) continue;
            var left = _lefts[i];
            var right = left + _widths[i] * _cur[i];
            if (double.IsNaN(firstLeft)) firstLeft = left;
            lastRight = right;
            count++;
            var center = (left + right) / 2.0;
            if (p.X > center) slot = count;   // 光标越过第 count 个固定项中心 → 落点在其之后
        }
        var withinPinned = !double.IsNaN(firstLeft) &&
                           p.X >= firstLeft - pitch && p.X <= lastRight + pitch;
        var overTrash = HitTestItem(p)?.Kind == DockItemKind.RecycleBin;
        return new DropProbe(slot, overTrash, withinPinned);
    }

    /// <summary>被拖条目在固定块中的下标（用于排序的 from）；非固定项或找不到返回 -1。</summary>
    public int PinnedIndexOf(DockItem item)
    {
        if (item.Kind != DockItemKind.Pinned) return -1;
        var idx = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i] is null) continue;
            if (_items[i]!.Kind != DockItemKind.Pinned) continue;
            if (ReferenceEquals(_items[i], item)) return idx;
            idx++;
        }
        return -1;
    }

    /// <summary>固定块第 slot 槽（用绘制后左缘算）在 surface 本地 X；无固定项时钳到 0。</summary>
    private double IndicatorXForSlot(int slot)
    {
        var pitch = IconSize + Spacing;
        var seen = 0;
        var prevRight = double.NaN;
        for (var i = 0; i < _items.Count && i < _widths.Length; i++)
        {
            if (_items[i] is not { Kind: DockItemKind.Pinned } || _widths[i] <= MinVisibleWidthDip) continue;
            var left = _lefts[i];
            if (seen == slot)
                return double.IsNaN(prevRight) ? left - pitch * 0.5 : (prevRight + left) * 0.5;
            prevRight = left + _widths[i] * _cur[i];
            seen++;
        }
        // slot == 固定项数（末尾槽）；或没有任何固定项 → 落到首项左缘之前 / 0
        return double.IsNaN(prevRight) ? 0 : prevRight + pitch * 0.5;
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var item = HitTestItem(p) ?? (e.Source as FrameworkElement)?.DataContext as DockItem;
        if (item is null) return;
        ContextRequested?.Invoke(item, p);
        e.Handled = true;
    }

    /// <summary>主题切换等外部参数变更后强制重排 + 唤醒（DockWindow.ApplyTheme 用）。</summary>
    public void Refresh()
    {
        _layoutDirty = true;
        InvalidateMeasure();
        Wake();
    }

    /// <summary>
    /// 让每个条目的**绑定重新求值**（v3 T6）：图标位图是模板绑定经 <see cref="IconBakedSourceConverter"/>
    /// 烘出来的，主题 / 图标尺寸 / DPI / IconTreatment 一变，旧位图就不再是"当前参数下的那一张"。
    /// 重建容器是唯一正确的出口——WPF 会重新实例化模板并重跑绑定，不需要再造一套"图标刷新"通道。
    /// <para>
    /// 【低频纪律】只在参数变更时调用（DockWindow 侧对滑杆拖动做了防抖），绝不进逐帧路径。
    /// 它走的是既有的 <c>RebuildVisualTree</c>：可视树整体换掉，逐帧缩放状态随之重置为 1，
    /// 这与"条目增删/换源"的既有语义一致，不会带来额外的布局成本。
    /// </para>
    /// </summary>
    public void RebindIconContainers() => RebuildVisualTree();
}
