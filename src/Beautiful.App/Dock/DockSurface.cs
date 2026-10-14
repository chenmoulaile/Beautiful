using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beautiful.App.Services;
using Beautiful.Core.Dock;
// UseWindowsForms=true + ImplicitUsings 引入了 System.Drawing 全局命名空间，
// 与 WPF 的 Point/Size/Rect 同名 → 显式指到 WPF 版本（与 DockWindow.xaml.cs 同一手法）。
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

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
/// 绝不改 Margin / LayoutTransform → 无逐项布局重排。</description></item>
/// <item><description>逐帧步长 <c>dt = (now-last)/Frequency</c> 交给
/// <see cref="AnimationMath.Damp"/>（指数阻尼，帧率无关），而非固定 0.35 系数 lerp。</description></item>
/// <item><description>容器由 <see cref="DataTemplateSelector"/> 一次性生成并缓存在
/// <see cref="_containers"/>，逐帧按引用直接取用，不再调 ItemContainerGenerator。</description></item>
/// </list>
/// 全部条目收敛到目标**且**光标已离开（无悬停目标）时摘掉 Rendering 钩子，空闲零开销。
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

    /// <summary>条目行高在图标尺寸之上的余量（DIP）：44+20=64，与 v1 面板内容区（76-2×6）一致。</summary>
    private const double RowInsetDip = 20;

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

    // 逐帧几何状态（下标 = 条目下标，与 _containers 对齐）
    private double[] _cur = Array.Empty<double>();     // 当前（阻尼后）scale
    private double[] _tgt = Array.Empty<double>();     // 目标 scale（来自放大场）
    private double[] _baseLefts = Array.Empty<double>(); // 未放大左缘
    private double[] _lefts = Array.Empty<double>();     // 本帧实际写下的左缘（命中测试/媒体面板定位读它）
    private double[] _widths = Array.Empty<double>();  // 未放大宽（模板 DesiredSize，胶囊 124 / 图标 44）
    private double[] _heights = Array.Empty<double>();
    private double[] _extra = Array.Empty<double>();   // 本帧 (scale-1)·width 累加用暂存

    // 放大场输入：仅**可见**项参与（隐藏胶囊不占位也不产生"幽灵鼓包"）
    private double[] _fieldCenters = Array.Empty<double>();
    private int[] _fieldToItem = EmptyInts;

    private double _naturalWidth;    // 未放大总宽
    private double _rowHeight = RowInsetDip;
    private double _lastReportedTotal = double.NaN;
    private double _lastCursorX = double.NaN;

    private bool _layoutDirty = true;
    private bool _structureDirty = true;   // 可视树重建（区别于仅尺寸变化）：只有它才重置逐帧缩放状态
    private bool _settled;
    private bool _hooked;
    private long _lastTick;

    public DockSurface()
    {
        ClipToBounds = false;                       // 放大后允许画出自身 bounds（顶部余量区）
        Background = System.Windows.Media.Brushes.Transparent;  // 面板区域内空白也参与命中（MouseMove 唤醒循环）
        IsHitTestVisible = true;
        SnapsToDevicePixels = false;

        MouseMove += (_, _) => Wake();              // 仅作"醒来"信号：真实位置每帧由 CursorProvider 取
        MouseEnter += (_, _) => Wake();
        MouseLeftButtonUp += OnMouseLeftButtonUp;
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

    /// <summary>正对光标项的最大倍率（1 = 等效关闭放大）。</summary>
    public static readonly DependencyProperty MaxScaleProperty = DependencyProperty.Register(
        nameof(MaxScale), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(1.0, OnFieldParameterChanged));

    /// <summary>高斯衰减半径，单位 = "一个图标间距"（IconSize+Spacing）；2.0 与 v1 的视觉尺度等效。</summary>
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
    /// 图标倒影开关（brief 接口位）。本任务不绘制倒影——镜像副本会让可视树翻倍、直接吃帧时间预算，
    /// 倒影效果由后续任务（高级特性）在帧时间允许的前提下实现。
    /// </summary>
    public static readonly DependencyProperty ReflectionEnabledProperty = DependencyProperty.Register(
        nameof(ReflectionEnabled), typeof(bool), typeof(DockSurface),
        new PropertyMetadata(false));

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
        s._layoutDirty = true;
        s.InvalidateMeasure();
        s.Wake();
    }

    private static void OnFieldParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DockSurface)d).Wake();

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
                }
                _containers.Add(fe);
            }
        }

        _structureDirty = true;   // 条目增删/换源：整树重建 → 场状态一并重置
        _layoutDirty = true;
        InvalidateMeasure();
        Wake();
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

        if (_layoutDirty || _widths.Length != n) RecomputeBaseLayout();

        _rowHeight = IconSize + RowInsetDip;
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

    /// <summary>未放大基准排布：左缘/中心/可见项场中心。仅在集合或模板尺寸变化时重算。</summary>
    private void RecomputeBaseLayout()
    {
        var n = _containers.Count;
        var structural = _structureDirty;
        if (_structureDirty)
        {
            // 整树重建：条目与下标的对应关系可能整体平移，旧缩放状态一律作废（brief 要求）
            _cur = new double[n]; _tgt = new double[n];
            _baseLefts = new double[n]; _widths = new double[n];
            _heights = new double[n]; _extra = new double[n]; _lefts = new double[n];
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
            _extra = EnsureLength(_extra, n, 0.0);
            _lefts = EnsureLength(_lefts, n, 0.0);
        }
        _structureDirty = false;

        var centers = new List<double>(n);
        var map = new List<int>(n);
        var acc = 0.0;
        for (var i = 0; i < n; i++)
        {
            var fe = _containers[i];
            var w = fe?.DesiredSize.Width ?? 0;
            if (double.IsNaN(w) || w < 0) w = 0;
            _widths[i] = w;
            _heights[i] = fe?.DesiredSize.Height ?? 0;
            if (w > MinVisibleWidthDip)
            {
                _baseLefts[i] = acc;
                centers.Add(acc + w / 2);
                map.Add(i);
                acc += w + Spacing;
            }
            else
            {
                _baseLefts[i] = acc;             // 隐藏槽：不占位（下一个可见项紧接其后）
            }
        }

        _naturalWidth = Math.Max(0, acc - (centers.Count > 0 ? Spacing : 0));
        _fieldCenters = centers.ToArray();
        _fieldToItem = map.ToArray();
        _layoutDirty = false;
        _settled = false;
        if (structural)
            Logger.Info($"DockSurface 排布: 容器={n} 可见={centers.Count} 自然宽={_naturalWidth:F0} " +
                        $"IconSize={IconSize} Spacing={Spacing} MaxScale={MaxScale} ShowMag={ShowMagnification}");
    }

    private static double[] EnsureLength(double[] arr, int n, double fill)
    {
        if (arr.Length == n) return arr;
        var next = new double[n];
        Array.Fill(next, fill);
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

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;

        // 守卫（Task 5 minor）：dt 为负（时钟回拨/异常）会让 Damp 的 e^(-λdt) 变成放大因子 → 数值发散，夹到 0；
        // dt 过大（>0.25s：机器休眠、调试断点、VM 长时间掉帧）不再逐帧追，直接吸附目标。
        if (dt < 0) dt = 0;
        var stalled = dt > MaxFrameStepSeconds;

        var cursorX = CursorProvider?.Invoke();
        var active = cursorX.HasValue && ShowMagnification && MaxScale > 1.0 && _fieldCenters.Length > 0;

        // 无悬停目标且已全部收敛 → 摘钩（空闲零开销）
        if (!active && _settled && !_layoutDirty)
        {
            StopLoop();
            return;
        }
        // 光标没动 + 已收敛 + 结构不脏 → 本帧无事可做（仍挂着钩子，以便光标一离开立刻回落）
        if (active && _settled && !_layoutDirty && cursorX == _lastCursorX) return;

        ComputeTargets(cursorX, active);

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

        if (settled && !active) StopLoop();
    }

    /// <summary>目标 scale：连续高斯场（Core 纯函数）；无悬停目标时全 1。</summary>
    private void ComputeTargets(double? cursorX, bool active)
    {
        if (!active || cursorX is not { } x)
        {
            Array.Fill(_tgt, 1.0);
            return;
        }

        // sigmaDip：把"以图标间距为单位"的 σ 换算成场函数要的真实 DIP 半径
        var sigmaDip = Math.Max(1.0, Sigma * (IconSize + Spacing));
        // 光标 X 必须换算到**未放大基准坐标系**再喂给场函数：AutoWidth 下面板随总宽生长、
        // 其左缘随之左移，若直接用 surface 本地坐标，静止光标的本地 X 会自己漂移 →
        // 放大峰值偏离光标，且每帧目标都在变（循环永不收敛）。
        // 面板恒被父级居中，故 (本地 X − 当前宽/2 + 基准宽/2) 与生长量无关，静止光标即静止目标。
        var currentWidth = AutoWidth ? _naturalWidth + CurrentExtraWidth() : _naturalWidth;
        var baseX = x - currentWidth / 2.0 + _naturalWidth / 2.0;
        // 只取 Scales：Offsets/TotalWidth 由本控件按**阻尼后**的 scale 重新前缀和（见 ApplyGeometry），
        // 这样几何永远与当前帧的 scale 自洽（不会出现"目标已移位、图标还在缩放"的错位/重叠）。
        // centered 参数只作用于返回值 Offsets，故此处取值不影响几何。
        var field = MagnificationField.Compute(baseX, _fieldCenters, IconSize, Spacing, MaxScale, sigmaDip, true);
        Array.Fill(_tgt, 1.0);
        for (var k = 0; k < _fieldToItem.Length && k < field.Scales.Length; k++)
            _tgt[_fieldToItem[k]] = field.Scales[k];
    }

    /// <summary>
    /// 逐帧几何写入：**只有** Canvas.Left / Canvas.Top / ScaleTransform 三个值。
    /// 不改 Margin、不用 LayoutTransform → 面板内不发生逐项测量排列。
    /// </summary>
    private void ApplyGeometry()
    {
        var n = _containers.Count;
        if (n == 0) return;

        var totalExtra = 0.0;
        for (var i = 0; i < n; i++)
        {
            var e = (_cur[i] - 1.0) * _widths[i];
            _extra[i] = e;
            totalExtra += e;
        }

        // AutoWidth：面板随总宽生长，条目从 0 起排（左对齐即整体居中，因为父级把面板居中摆放）；
        // 固定宽：以未放大中心为轴对称外扩（会越出面板，供 AutoWidth=false 的对比方案使用）。
        var shift = AutoWidth ? 0.0 : totalExtra / 2.0;
        var prefix = 0.0;
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

            var left = _baseLefts[i] + prefix - shift;
            _lefts[i] = left;                                // 命中测试与媒体面板定位读的就是这一帧的真值
            SetLeft(fe, left);
            SetTop(fe, _rowHeight - _heights[i]);            // 底部对齐：缩放原点 0.5,1 → 底边不动、向上生长
            if (fe.RenderTransform is ScaleTransform st)
            {
                var s = _cur[i];
                if (!AnimationMath.CloseEnough(st.ScaleX, s)) st.ScaleX = st.ScaleY = s;  // 原地改值，不新建 Freezable
            }
            prefix += _extra[i];
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
    }

    // ---------- 命中 / 交互 ----------

    /// <summary>
    /// 用**放大后**的矩形做命中（左缘/宽度按当前 scale 展开，纵向自底边向上），
    /// 取代 v1"最近基准中心"权宜法——连续场没有离散索引可抖，无需绕。
    /// </summary>
    public DockItem? HitTestItem(Point p)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item is null || _widths[i] <= MinVisibleWidthDip) continue;
            var w = _widths[i] * _cur[i];
            var left = _lefts[i];
            if (p.X < left || p.X >= left + w) continue;
            var h = _heights[i] * _cur[i];
            if (p.Y > _rowHeight || p.Y < _rowHeight - h) continue;
            return item;
        }
        return null;
    }

    /// <summary>条目当前（放大后）在面板坐标系中的矩形；条目不存在/已收起返回 null（媒体面板定位用）。</summary>
    public Rect? GetItemBounds(DockItem item)
    {
        var i = _items.IndexOf(item);
        if (i < 0 || _widths[i] <= MinVisibleWidthDip) return null;
        return new Rect(_lefts[i], _rowHeight - _heights[i] * _cur[i],
                        _widths[i] * _cur[i], _heights[i] * _cur[i]);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = HitTestItem(e.GetPosition(this)) ?? (e.Source as FrameworkElement)?.DataContext as DockItem;
        if (item is not null) Activated?.Invoke(item);
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
}
