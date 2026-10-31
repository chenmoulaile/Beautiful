using System.IO;
using Path = System.IO.Path;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Beautiful.Core.Animation;

namespace Beautiful.AnimLab;

/// <summary>
/// AnimLab 主窗口：Beautiful 打开/关闭/genie 动画的参数调参台。
/// <para>
/// 复用 <see cref="AnimParams"/>（与主程序同一份 JSON 校验与归一化），在演示舞台上用
/// <see cref="OpenAnimMath"/> / <see cref="CloseAnimMath"/> / <see cref="GenieMath"/> 逐帧
/// 呈现当前参数下的几何；底部实时渲染帧间隔图（<see cref="FrameTimeStats"/>）。
/// 命令行可传入主程序的 anim-params.json 路径：保存后主程序侧 watcher 立即热加载。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    // ---------- 参数文件（轻量版：与主程序同路径规则/同 JSON 口径，不引 App 工程） ----------

    private string _paramsPath = "";   // alpha.18：构造期 TryStep 内赋值，声明时给安全初值（可空性 + 失败降级）
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadDebounce;
    private bool _suppressWatch;

    private sealed class ParamRow
    {
        public string Name = "";
        public TextBox Box = null!;
    }

    private readonly List<ParamRow> _rows = new();

    // ---------- 演示舞台 ----------

    private enum DemoKind { None, Open, Close, Genie, Morph }

    private DemoKind _demo = DemoKind.None;
    private TimeSpan _demoStart;
    private Rectangle _demoRect = null!;
    private readonly List<Rectangle> _genieRects = new();
    private const int GenieSlices = GenieMath.DefaultSlices;
    private Ellipse _tMarker = null!;

    // ---------- 帧时间 ----------

    private readonly Queue<double> _frames = new();
    private const int FrameWindow = 120;
    private TimeSpan _lastRender;
    private int _renderTick;
    private readonly List<Rectangle> _frameBars = new();
    private System.Windows.Shapes.Line _budgetLine = null!;

    public MainWindow()
    {
        // alpha.18：InitializeComponent（XAML 构造）失败没有降级可言（字段全 null），
        // 直接抛给 App 层拿完整异常链；之后的初始化每段独立兜底——任何一段失败，
        // 窗口都保持已建成的形态继续开着，并把原因写进状态栏（调参台绝不允许"无声失败"）。
        InitializeComponent();
        RunInit();
    }

    private void RunInit()
    {
        TryStep("参数路径", () =>
        {
            _paramsPath = ResolveParamsPath();
            ParamsPathText.Text = _paramsPath;
        });
        TryStep("演示舞台", BuildDemoScene);
        TryStep("帧时间图", BuildFrameChart);
        TryStep("尺寸联动", () => SizeChanged += (_, _) => { BuildCurvePlot(); LayoutFrameChart(); });
        TryStep("参数加载", () => LoadParamsFile(initial: true));
        TryStep("曲线面板", BuildCurvePlot);
        TryStep("帧图布局", LayoutFrameChart);
        TryStep("渲染钩子", () => CompositionTarget.Rendering += OnRendering);
        TryStep("参数监视", StartWatcher);
        SetStatus($"就绪 · GenieSlices={GenieSlices}", ok: true);
    }

    private void TryStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(new Exception($"AnimLab 初始化段[{name}]失败", ex));
            try { SetStatus($"{name}失败：{ex.GetBaseException().Message}", ok: false); }
            catch { /* 状态栏也挂了就只剩日志 */ }
        }
    }

    private static string ResolveParamsPath()
    {
        // AnimLab.exe <path-to-anim-params.json>：直接编辑主程序目录里的参数文件。
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) return Path.GetFullPath(args[1]);
        return Path.Combine(AppContext.BaseDirectory, "config", "anim-params.json");
    }

    // ---------- 参数行（JsonObject 双向，复用 Core 的 FromJson 校验） ----------

    private void LoadParamsFile(bool initial = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(_paramsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(_paramsPath))
                File.WriteAllText(_paramsPath, AnimParams.Default.ToJson());
        }
        catch (Exception ex)
        {
            SetStatus($"参数文件不可写：{ex.Message}", ok: false);
        }

        var rows = new List<ParamRow>();
        AnimParams current = AnimParams.Default;
        try
        {
            var parsed = AnimParams.FromJson(File.ReadAllText(_paramsPath), out var error);
            if (error is null) current = parsed;
            else SetStatus($"解析失败（{error}），显示默认值", ok: false);
        }
        catch (Exception ex)
        {
            SetStatus($"读取失败（{ex.GetType().Name}），显示默认值", ok: false);
        }

        var obj = JsonNode.Parse(current.ToJson()) as JsonObject;
        if (obj is not null)
        {
            foreach (var (name, node) in obj)
            {
                if (node is null) continue;
                var row = new ParamRow { Name = name };
                var box = new TextBox { Style = (Style)FindResource("ParamBox"), Text = node.ToJsonString() };
                box.Tag = row;
                row.Box = box;
                rows.Add(new ParamRow { Name = name, Box = box });
            }
        }

        ParamsHost.Children.Clear();
        _rows.Clear();
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            var label = new TextBlock { Text = row.Name, Style = (Style)FindResource("ParamName") };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(row.Box, 1);
            grid.Children.Add(label);
            grid.Children.Add(row.Box);
            ParamsHost.Children.Add(grid);
            _rows.Add(row);
        }

        if (initial)
            SetStatus($"已加载 {Path.GetFileName(_paramsPath)}（{_rows.Count} 项）", ok: true);
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        LoadParamsFile();
        SetStatus($"已重新加载 · {DateTime.Now:HH:mm:ss.fff}", ok: true);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 行值 → JsonObject → FromJson 校验 → 落盘。非法数值直接拒绝并指出行名。
        var obj = new JsonObject();
        foreach (var row in _rows)
        {
            var text = row.Box.Text.Trim();
            JsonNode? node;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) node = true;
            else if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) node = false;
            else if (long.TryParse(text, out var i)) node = i;
            else if (double.TryParse(text, out var d)) node = d;
            else node = text;
            obj[row.Name] = node;
        }

        var parsed = AnimParams.FromJson(obj.ToJsonString(), out var error);
        if (error is not null)
        {
            SetStatus($"保存被拒绝：{error}", ok: false);
            return;
        }

        try
        {
            _suppressWatch = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_paramsPath)!);
            File.WriteAllText(_paramsPath, parsed.ToJson());
            SetStatus($"已保存并归一化 · {DateTime.Now:HH:mm:ss.fff}（主程序 watcher 会自动热加载）", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}", ok: false);
        }
        finally
        {
            // 写回后把 UI 行刷成归一化值（FromJson 会补齐缺省/夹紧越界）。
            RefreshRowsFrom(parsed);
            var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            reset.Tick += (_, _) => { _suppressWatch = false; reset.Stop(); };
            reset.Start();
        }
    }

    private void OnDefaultsClick(object sender, RoutedEventArgs e)
    {
        RefreshRowsFrom(AnimParams.Default);
        SetStatus("已恢复默认（尚未保存）", ok: true);
    }

    private void RefreshRowsFrom(AnimParams p)
    {
        var obj = JsonNode.Parse(p.ToJson()) as JsonObject;
        if (obj is null) return;
        foreach (var row in _rows)
            if (obj.TryGetPropertyValue(row.Name, out var v) && v is not null)
                row.Box.Text = v.ToJsonString();
    }

    // ---------- 文件 watcher（外部编辑热加载，防抖 300ms） ----------

    private void StartWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_paramsPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_paramsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) =>
            {
                if (_suppressWatch) return;
                Dispatcher.BeginInvoke(() =>
                {
                    _reloadDebounce ??= NewDebounce();
                    _reloadDebounce.Stop();
                    _reloadDebounce.Start();
                });
            };
        }
        catch (Exception)
        {
            // watcher 起不来不致命：手动"重新加载"按钮始终可用。
        }
    }

    private System.Windows.Threading.DispatcherTimer NewDebounce()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            LoadParamsFile();
            SetStatus($"anim-params.json 已热加载 · {DateTime.Now:HH:mm:ss.fff}", ok: true);
        };
        return t;
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    }

    // ---------- 演示舞台 ----------

    private void BuildDemoScene()
    {
        // Genie 条带（最先加入，垫底）
        for (var i = 0; i < GenieSlices; i++)
        {
            var r = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(210, 0x0A, 0x84, 0xFF)),
                RadiusX = 2, RadiusY = 2,
                Visibility = Visibility.Hidden
            };
            Stage.Children.Add(r);
            _genieRects.Add(r);
        }

        // 演示窗口（白色圆角矩形，模拟一个被打开/关闭的窗口）
        _demoRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)),
            RadiusX = 10, RadiusY = 10,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45
            }
        };
        Stage.Children.Add(_demoRect);
        ResetStage();

        // 曲线 t 游标
        _tMarker = new Ellipse
        {
            Width = 9, Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)),
            Visibility = Visibility.Hidden
        };
        CurvePlot.Children.Add(_tMarker);
    }

    private void ResetStage()
    {
        _demoRect.Visibility = Visibility.Hidden;
        foreach (var r in _genieRects) r.Visibility = Visibility.Hidden;
        _tMarker.Visibility = Visibility.Hidden;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => StartDemo(DemoKind.Open);
    private void OnCloseClick(object sender, RoutedEventArgs e) => StartDemo(DemoKind.Close);
    private void OnGenieClick(object sender, RoutedEventArgs e) => StartDemo(DemoKind.Genie);
    private void OnMorphClick(object sender, RoutedEventArgs e) => StartDemo(DemoKind.Morph);

    private void StartDemo(DemoKind kind)
    {
        ResetStage();
        _demo = kind;
        _demoStart = _lastRender;
        if (kind is DemoKind.Open or DemoKind.Morph) _demoRect.Visibility = Visibility.Visible;
    }

    private int BaseDuration(DemoKind kind) => kind switch
    {
        DemoKind.Open => AnimParamsStore_OpenDurationMs(),
        DemoKind.Close => AnimParamsStore_CloseDurationMs(),
        DemoKind.Genie => AnimParamsStore_GenieDurationMs(),
        DemoKind.Morph => AnimParamsStore_MorphDurationMs(),
        _ => 0
    };

    // 参数行是唯一事实来源：演示直接读 UI 行（保存前也能试听未落盘的参数）。
    private int RowInt(string name, int fallback) =>
        _rows.FirstOrDefault(r => r.Name == name) is { } row && int.TryParse(row.Box.Text, out var v) ? v : fallback;
    private double RowDouble(string name, double fallback) =>
        _rows.FirstOrDefault(r => r.Name == name) is { } row && double.TryParse(row.Box.Text, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private int AnimParamsStore_OpenDurationMs() => RowInt("OpenDurationMs", 360);   // alpha.15 与 AnimParams.Default 同步
    private int AnimParamsStore_CloseDurationMs() => RowInt("CloseDurationMs", 260);
    private int AnimParamsStore_GenieDurationMs() => RowInt("GenieDurationMs", 500);
    private int AnimParamsStore_MorphDurationMs() => RowInt("MorphDurationMs", 240);
    private double AnimOpenStartScale() => RowDouble("OpenStartScale", 0.90);
    private double AnimCloseEndScale() => RowDouble("CloseEndScale", 0.92);
    private double AnimSpeed() => Math.Max(0.05, RowDouble("SpeedMultiplier", 1.0));

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        var now = re.RenderingTime;
        var dt = (now - _lastRender).TotalMilliseconds;
        _lastRender = now;
        if (dt > 0 && dt < 1000) PushFrame(dt);

        if (_demo == DemoKind.None) return;

        var elapsed = (now - _demoStart).TotalMilliseconds;
        var baseMs = BaseDuration(_demo);
        var slow = ChkSlow.IsChecked == true;
        // 与 AnimParams.Scaled 同口径：SpeedMultiplier 全局变速，慢速观察再除 3。
        var duration = Math.Max(1, baseMs / AnimSpeed()) * (slow ? 3.0 : 1.0);
        var t = AnimCurve.Clamp01(elapsed / duration);

        UpdateCurveMarker(t);
        switch (_demo)
        {
            case DemoKind.Open: RenderOpen(t); break;
            case DemoKind.Close: RenderClose(t); break;
            case DemoKind.Genie: RenderGenie(t); break;
            case DemoKind.Morph: RenderMorph(t); break;
        }

        if (t >= 1.0) FinishDemo(_demo);
    }

    private void FinishDemo(DemoKind kind)
    {
        _demo = DemoKind.None;
        if (kind == DemoKind.Close || kind == DemoKind.Genie)
        {
            if (kind == DemoKind.Close) _demoRect.Visibility = Visibility.Hidden;
            foreach (var r in _genieRects) r.Visibility = Visibility.Hidden;
        }
        _tMarker.Visibility = Visibility.Hidden;
    }

    private void RenderOpen(double t)
    {
        // Core 的打开几何：缩放 + 前段淡入（FadeShare=45% 时长内完成透明度）。
        var f = OpenAnimMath.Frame(t, AnimOpenStartScale());
        ApplyNormalized(_demoRect, f.Rect, f.Opacity);
    }

    private void RenderClose(double t)
    {
        var f = CloseAnimMath.Frame(t, AnimCloseEndScale());
        ApplyNormalized(_demoRect, f.Rect, f.Opacity);
    }

    private void RenderGenie(double t)
    {
        _demoRect.Visibility = Visibility.Hidden;
        var samples = GenieMath.Sample(t, GenieSlices);
        var n = Math.Min(samples.Count, _genieRects.Count);
        for (var i = 0; i < n; i++)
        {
            var s = samples[i];
            var rect = _genieRects[s.Index];
            rect.Visibility = s.Opacity > 0.01 ? Visibility.Visible : Visibility.Hidden;
            ApplyNormalized(rect, s.Rect, s.Opacity);
        }
    }

    private void RenderMorph(double t)
    {
        // 最大化/还原语义：中央矩形 → 全舞台，ZoomLerp 单矩形插值。
        var stageW = Stage.ActualWidth;
        var stageH = Stage.ActualHeight;
        if (stageW < 10 || stageH < 10) return;
        var src = new GenieRect(0.2, 0.25, 0.6, 0.5);
        var dst = new GenieRect(0.0, 0.0, 1.0, 1.0);
        var r = GenieMath.ZoomLerp(src, dst, GenieMath.EaseOut(t));
        ApplyNormalized(_demoRect, r, 1.0);
    }

    private void ApplyNormalized(Shape shape, GenieRect norm, double opacity)
    {
        var w = Stage.ActualWidth;
        var h = Stage.ActualHeight;
        if (w < 10 || h < 10) return;
        Canvas.SetLeft(shape, norm.X * w);
        Canvas.SetTop(shape, norm.Y * h);
        shape.Width = Math.Max(0, norm.Width * w);
        shape.Height = Math.Max(0, norm.Height * h);
        shape.Opacity = Math.Clamp(opacity, 0, 1);
    }

    // ---------- 缓动曲线面板 ----------

    private void BuildCurvePlot()
    {
        CurvePlot.Children.Clear();
        CurvePlot.Children.Add(_tMarker);
        var w = CurvePlot.ActualWidth;
        var h = CurvePlot.ActualHeight;
        if (w < 40 || h < 40) return;

        // 网格与对角参考线
        for (var i = 0; i <= 4; i++)
        {
            var gx = new System.Windows.Shapes.Line
            {
                X1 = i * w / 4, Y1 = 0, X2 = i * w / 4, Y2 = h,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)), StrokeThickness = 1
            };
            var gy = new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = i * h / 4, X2 = w, Y2 = i * h / 4,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)), StrokeThickness = 1
            };
            CurvePlot.Children.Add(gx);
            CurvePlot.Children.Add(gy);
        }

        DrawCurve(AnimCurve.Names.Open, t => AnimCurve.EaseOutQuint(t), Colors: (0x0A, 0x84, 0xFF));
        DrawCurve(AnimCurve.Names.Close, t => AnimCurve.EaseInQuad(t), (0xFF, 0x45, 0x3A));

        var legend = new TextBlock
        {
            Text = "● open (easeOutQuint)\n● close (easeInQuad)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
            FontSize = 11
        };
        Canvas.SetLeft(legend, 10);
        Canvas.SetTop(legend, 8);
        CurvePlot.Children.Add(legend);
    }

    private void DrawCurve(string _, Func<double, double> ease, (byte R, byte G, byte B) Colors)
    {
        var w = CurvePlot.ActualWidth;
        var h = CurvePlot.ActualHeight;
        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(Colors.R, Colors.G, Colors.B)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        const int steps = 100;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            poly.Points.Add(new Point(t * w, h - ease(t) * h));
        }
        CurvePlot.Children.Add(poly);
    }

    private void UpdateCurveMarker(double t)
    {
        var w = CurvePlot.ActualWidth;
        var h = CurvePlot.ActualHeight;
        if (w < 40 || h < 40) return;
        _tMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(_tMarker, t * w - _tMarker.Width / 2);
        Canvas.SetTop(_tMarker, h - AnimCurve.EaseOutQuint(t) * h - _tMarker.Height / 2);
    }

    // ---------- 帧时间图 ----------

    private void BuildFrameChart()
    {
        FrameCanvas.Children.Clear();
        _frameBars.Clear();
        _budgetLine = new System.Windows.Shapes.Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xD6, 0x0A)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        };
        FrameCanvas.Children.Add(_budgetLine);
        for (var i = 0; i < FrameWindow; i++)
        {
            var bar = new Rectangle { Width = 4, Height = 1, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) };
            FrameCanvas.Children.Add(bar);
            _frameBars.Add(bar);
        }
    }

    private void LayoutFrameChart()
    {
        var h = FrameCanvas.ActualHeight;
        if (h < 10) return;
        _budgetLine.X1 = 0;
        _budgetLine.X2 = FrameCanvas.ActualWidth;
        // 16.7ms 映射：0..48ms → 0..满高
        _budgetLine.Y1 = _budgetLine.Y2 = h - (16.7 / 48.0) * h;
    }

    private void PushFrame(double ms)
    {
        _frames.Enqueue(ms);
        while (_frames.Count > FrameWindow) _frames.Dequeue();
        _renderTick++;

        var h = FrameCanvas.ActualHeight;
        var w = FrameCanvas.ActualWidth;
        if (h < 10 || w < 40) return;

        var arr = _frames.ToArray();
        var spacing = w / FrameWindow;
        for (var i = 0; i < FrameWindow; i++)
        {
            var bar = _frameBars[i];
            if (i < arr.Length)
            {
                var v = arr[i];
                var frac = Math.Min(1.0, v / 48.0);
                bar.Height = Math.Max(1, frac * (h - 2));
                Canvas.SetLeft(bar, w - (arr.Length - i) * spacing);
                Canvas.SetTop(bar, h - bar.Height);
                bar.Fill = new SolidColorBrush(v > FrameTimeStats.DefaultBudgetMs
                    ? Color.FromRgb(0xFF, 0x45, 0x3A)
                    : Color.FromRgb(0x30, 0xD1, 0x58));
                bar.Visibility = Visibility.Visible;
            }
            else bar.Visibility = Visibility.Hidden;
        }

        if (_renderTick % 20 == 0)
        {
            var s = FrameTimeStats.Compute(arr);
            StatAvg.Text = $"{s.AverageMs,5:0.0} ms";
            StatP95.Text = $"{s.P95Ms,5:0.0} ms";
            StatMax.Text = $"{s.MaxMs,5:0.0} ms";
            StatBad.Text = $"{s.BadRatio,4:0%}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
