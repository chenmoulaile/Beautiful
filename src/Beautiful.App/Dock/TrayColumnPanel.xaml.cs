using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Controls;
using Beautiful.App.Services;
using Beautiful.Core.Layout;   // PopupShadow（投影留白的类型）
using Beautiful.Core.Tray;
using UserControl = System.Windows.Controls.UserControl;   // UseWindowsForms 隐式 using 与 WinForms 同名
using Button = System.Windows.Controls.Button;             // 同上
using Image = System.Windows.Controls.Image;               // 同上（System.Drawing.Image）
using Brush = System.Windows.Media.Brush;                  // 同上，System.Drawing.Brush
using Brushes = System.Windows.Media.Brushes;              // 同上
using Color = System.Windows.Media.Color;                  // 同上
using Stretch = System.Windows.Media.Stretch;              // 同上（图标按原始尺寸显示，不拉伸）

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 托盘收纳列的展开弹层（5.0.0 未闭合项④）：把 Windows 通知区域的第三方托盘图标平铺出来，
/// 点击/右键把消息转发回图标自己的窗口——数据链与顶栏完全同源（<see cref="TrayIconSource"/> 两路
/// → <see cref="TrayIconSnapshot.BackfillAll"/> 注册表快照回填真 PNG），<b>不重复扫描</b>：
/// 平时零成本（Dock 格子是静态图形），弹层打开才读（TrayPanel 的"展开才读"模式），
/// 打开期间 3 秒自动重读一次（"数据源变化实时刷新"），关闭即停——不与顶栏常驻条叠加任何
/// 常驻扫描/抓屏。每张弹层自持一份 <see cref="TrayIconSource"/>（本仓库纪律：消费方不共享
/// 句柄生命周期；远程内存每次读取的 finally 里就还掉了，无驻留）。
/// <para>
/// 【材质】与 Dock 面板同一套 token：玻璃底 <c>ThemeDockGlassBackground</c>、圆角
/// <c>ThemePanelCornerRadius</c>、顶缘发丝线 <c>AurHairlineBrush</c>（见 XAML 注释）。
/// </para>
/// <para>
/// 【为什么所有取数都在后台线程】<see cref="TrayIconSource"/> 的每一条路都是别人的进程
/// （远程读 explorer、跨进程 send、整棵 UIA 树遍历，几十到几百毫秒）——与 TrayPanel 同一条
/// 线程契约：<c>Task.Run</c> 之后 <c>await</c> 回投，回投先问 <see cref="UserControl.IsLoaded"/>
/// （用户可能已经点了别处把弹层关了）。
/// </para>
/// <para>
/// 【为什么没有"只读格"】不能转发的图标不 <c>IsEnabled=false</c>（WPF 禁用元素连 ToolTip 都不吃，
/// 用户只会看到一个点不动的方块）：保持可点，点下去把"为什么不能转发"写进状态行；视觉用
/// 0.62 不透明度做提示，不靠颜色当唯一说明（与 TrayPanel 同一裁定）。
/// </para>
/// </summary>
public partial class TrayColumnPanel : UserControl, IBackdropAware, IPopupShadow
{
    /// <summary>一页平铺多少格（与 Core 同源，不在界面里另抄一个常数）。</summary>
    private const int PageSize = TrayLayout.DefaultPageSize;

    /// <summary>弹层打开期间的自动重读节奏：够"实时"，又远低于顶栏 45s 常驻条的叠加焦虑——
    /// 弹层是几秒的瞬态表面，整段生命周期的跨进程读取次数是个位数。</summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly TrayIconSource _source = new();
    private FluentPopupHost? _host;

    private TrayReadResult _result = new();
    private IReadOnlyList<TrayIconItem> _items = [];

    /// <summary>当前页（0 基）。刷新后会被 <see cref="TrayLayout.ClampPage"/> 拉回合法范围。</summary>
    private int _page;

    /// <summary>是否已有一次读取在跑：自动刷新 + 手动刷新 + 连点会叠出多串跨进程读，
    /// 最后一串赢者未必是最新的那串（与 TrayPanel 同一防重入）。</summary>
    private bool _reading;

    /// <summary>投影留白 = 自己根元素的 Margin（唯一来源，不与 XAML 各说各话）。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    /// <summary>弹层打开期间的自动重读（Loaded 起、Shutdown 停；DispatcherTimer 必须在 UI 线程建）。</summary>
    private readonly DispatcherTimer _autoRefresh = new();

    public TrayColumnPanel()
    {
        InitializeComponent();
        // Tick 订阅与节奏都在构造期定好（Loaded 理论上只触发一次，防御性避免重入叠加）。
        _autoRefresh.Interval = AutoRefreshInterval;
        _autoRefresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _autoRefresh.Start(); };
    }

    /// <summary>宿主注入：需要能收起自己（转发右键、展开系统隐藏区之前都必须先关）。</summary>
    public void AttachHost(FluentPopupHost host) => _host = host;

    /// <summary>
    /// 面板拆除（宿主 Closed 时由打开方调用）：停自动刷新 + 断宿主引用 + 还掉 source。
    /// <see cref="TrayIconSource"/> 不驻留句柄（远程内存在每次读取的 finally 里就还掉了），
    /// 这里主要是把 3 秒定时器停干净——弹层已经关了还在跑跨进程读就是纯浪费。
    /// </summary>
    public void Shutdown()
    {
        _autoRefresh.Stop();
        _host = null;
        _source.Dispose();
    }

    /// <summary>
    /// 拿到抓屏模糊 → 卡底铺该画刷并压 <c>AurPopupFillBlur</c> 遮罩；拿不到 → 铺 Dock 同款实心
    /// 玻璃底（ThemeDockGlassBackground 本身就是实心渐变）+ 遮罩透明。降级链上没有任何一环会画成黑。
    /// </summary>
    public void OnBackdropReady(ScreenBackdrop? backdrop)
    {
        var blur = backdrop?.Brush;
        if (blur is not null)
        {
            Card.Background = blur;
            Scrim.Background = (Brush)FindResource("AurPopupFillBlur");
            return;
        }
        Card.Background = (Brush)FindResource("ThemeDockGlassBackground");
        Scrim.Background = Brushes.Transparent;
    }

    // ---------- 数据 ----------

    /// <summary>
    /// 后台读两路 → 注册表快照回填 → 回投重画。取不到就画空态 + 写明两路各自的原因，
    /// 绝不留旧数据冒充新的，也绝不画假图标（没有像素的格子保持中性占位 + 原因）。
    /// </summary>
    private async void Refresh()
    {
        if (_reading) return;
        _reading = true;
        HeaderStatus.Text = "读取中…";
        EmptyLine.Visibility = Visibility.Collapsed;
        try
        {
            TrayReadResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(() => _source.Read());
            }
            catch (Exception ex)
            {
                // Read 内部已把两条路各自的失败变成状态字符串；走到这里是意外（内存/环境异常）。
                // 弹层不能因为一次枚举炸掉：写回状态行，界面还能用。
                Logger.Warn($"托盘收纳列枚举异常: {ex.Message}");
                if (!IsLoaded) return;
                ShowStatus($"读取托盘图标时出了意外：{ex.Message}");
                result = new TrayReadResult();
            }
            if (!IsLoaded) return;   // 弹层可能已经被关掉
            var backfill = await System.Threading.Tasks.Task.Run(
                () => TrayIconSnapshot.BackfillAll(result.Items));
            if (!IsLoaded) return;
            _result = result;
            _items = backfill.Items;
            _page = TrayLayout.ClampPage(_page, _items.Count, PageSize);
            Render();
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>把当前结果画成格子（含分页、空态、诊断行、"展开系统隐藏区"按钮的可见性）。</summary>
    private void Render()
    {
        IconHost.Children.Clear();
        DiagLine.Text = BuildDiag();
        DiagLine.Visibility = Visibility.Visible;
        OverflowButton.Visibility = _result.HasShowHiddenButton ? Visibility.Visible : Visibility.Collapsed;

        if (_items.Count == 0)
        {
            HeaderStatus.Text = "读不到";
            IconBand.Visibility = Visibility.Collapsed;
            PageRow.Visibility = Visibility.Collapsed;
            EmptyLine.Text = BuildEmptyText();
            EmptyLine.Visibility = Visibility.Visible;
            return;
        }

        HeaderStatus.Text = $"{_items.Count} 个";
        IconBand.Visibility = Visibility.Visible;
        EmptyLine.Visibility = Visibility.Collapsed;

        var (start, take) = TrayLayout.Slice(_items.Count, _page, PageSize);
        for (var i = 0; i < take; i++) IconHost.Children.Add(MakeTile(_items[start + i]));

        var pages = TrayLayout.PageCount(_items.Count, PageSize);
        PageRow.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (pages <= 1) return;
        PageText.Text = $"第 {_page + 1} / {pages} 页";
        PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = _page < pages - 1;
    }

    /// <summary>两路各自的结果并排写出来（"这一格为什么点不动""为什么读不到"的第一手判据）。</summary>
    private string BuildDiag()
    {
        var text = $"经典工具栏：{_result.ClassicStatus}｜无障碍树：{_result.UiaStatus}";
        if (_result.SkippedSystemOwned > 0) text += $"｜已跳过 {_result.SkippedSystemOwned} 个系统自己的项";
        return text;
    }

    /// <summary>空态文案：说清"读不到"而不是画一排假图标占位；能确定原因时把原因写在最前面。</summary>
    private string BuildEmptyText()
    {
        if (_result.SkippedSystemOwned > 0)
            return $"只读到 {_result.SkippedSystemOwned} 个系统自己的通知区域控件（网络、音量一类），" +
                   "这台机器上没有第三方托盘图标";
        return "这台机器的托盘图标读不到（Win11 24H2 已把通知区域换成 XAML 岛，经典工具栏那条路不存在；" +
               "无障碍树这一路的具体结果见下面一行）";
    }

    // ---------- 格子 ----------

    /// <summary>一格图标：有真实像素就画它（裁屏/注册表快照，绝不拿 exe 图标凑数），没有画中性空框。</summary>
    private UIElement MakeTile(TrayIconItem item)
    {
        var visual = item.Image is { } img
            ? new Image { Source = img, Width = 24, Height = 24, Stretch = Stretch.Uniform }
            : MakePlaceholder();

        var tile = new Button
        {
            Content = visual,
            Style = (Style)Resources["TrayTileStyle"],
            ToolTip = BuildTip(item),
            Tag = item,
            // 不能转发的格子压暗一点做提示；解释全在 ToolTip 与点击后的状态行里
            Opacity = item.CanActivate ? 1.0 : 0.62,
        };
        AutomationProperties.SetName(tile, string.IsNullOrWhiteSpace(item.Label)
            ? "托盘图标（应用没给名字）"
            : $"托盘图标 {item.Label}");
        tile.Click += Tile_Click;
        tile.MouseRightButtonUp += Tile_RightClick;
        return tile;
    }

    /// <summary>
    /// 中性占位：一个空框里一个问号。<b>不用应用名首字母、也不用任何 exe/窗口图标</b>——
    /// 首字母看起来像某些应用的 monogram，窗口图标看起来就像"这就是它的图标"，
    /// 两者都会让用户把一个猜测当成事实（项目裁定：宁可空框也不错画）。
    /// </summary>
    private static UIElement MakePlaceholder()
        => new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "?",
                FontSize = 12.5,
                Opacity = 0.62,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    /// <summary>
    /// ToolTip 四行：名字 / 哪一路读到的 / 图从哪来或为什么没有 / 能不能点（以及为什么）。
    /// </summary>
    private static string BuildTip(TrayIconItem item)
    {
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(item.Label) ? "（应用没给名字）" : item.Label!,
            item.Origin == TrayIconOrigin.ClassicToolbar ? "来源：经典托盘工具栏" : "来源：任务栏无障碍树",
        };
        if (item.Image is null) lines.Add("图标：" + (item.ImageNote ?? "读不到它的位置"));
        lines.Add(item.BlockReason is null
            ? "左键＝它的默认动作，右键＝它自己的菜单（都交回那个应用处理）"
            : "不能点：" + item.BlockReason);
        return string.Join("\n", lines);
    }

    // ---------- 交互 ----------

    /// <summary>左键：交给那个应用（弹层不关——很多应用的默认动作就是显示自己的主窗口）。</summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item }) return;
        _ = ForwardAsync(item, TrayClickAction.LeftClick, closeFirst: false);
    }

    /// <summary>
    /// 右键：交回那个应用，让它弹<b>它自己的</b>菜单。
    /// 【为什么必须先拆弹层】本面板由 FluentPopupHost 承载（Topmost 顶层窗口），那个应用的菜单
    /// 是它自己的 TrackPopupMenu——两个顶层弹层叠在一起时"谁压住谁"取决于时序，观感就是"右键没反应"。
    /// 先关自己再发（与 TrayPanel 同一结论）。
    /// </summary>
    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item }) return;
        e.Handled = true;   // 别让这次右键继续冒泡
        _ = ForwardAsync(item, TrayClickAction.RightClick, closeFirst: true);
    }

    /// <summary>转发一次点击。关闭之后的结果不写界面（IsLoaded 守卫）：点击已发出、收不回来。</summary>
    private async System.Threading.Tasks.Task ForwardAsync(TrayIconItem item, TrayClickAction action, bool closeFirst)
    {
        if (closeFirst) _host?.Close();
        var (ok, error) = await System.Threading.Tasks.Task.Run(() => _source.Activate(item, action));
        if (!IsLoaded) return;
        ShowStatus(ok
            ? $"{NameOf(item)}：已把{(action == TrayClickAction.RightClick ? "右键" : "左键")}交给它自己的应用"
            : error ?? "没能把这次点击交给这个应用");
    }

    /// <summary>分页：只重画，不重新读（重读要走跨进程一遍，翻页不该付这个代价）。</summary>
    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 0) return;
        _page--;
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page >= TrayLayout.PageCount(_items.Count, PageSize) - 1) return;
        _page++;
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// "展开系统隐藏区"：让<b>系统自己</b>弹出隐藏图标浮层。先关自己（系统浮层要拿前台，
    /// 两个都要前台就是互相顶——与 TrayPanel 同一取舍：让位给系统）。
    /// </summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        _host?.Close();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var (ok, error) = _source.ShowHiddenIcons();
            if (ok) Logger.Info("托盘收纳列：已请求系统展开隐藏图标区");
            else Logger.Warn($"托盘收纳列：展开隐藏图标区失败 {error}");
            return ok;
        });
    }

    /// <summary>系统通知设置：读不到、点不动、想管"哪些图标允许显示"时的唯一真出路。</summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications")
            { UseShellExecute = true });
            _host?.Close();
        }
        catch (Exception ex)
        {
            // 弹层不该因为一次打不开设置页就炸：原因写回状态行
            ShowStatus($"打不开系统通知设置：{ex.Message}");
        }
    }

    private void ShowStatus(string text)
    {
        StatusLine.Text = text;
        StatusLine.Visibility = Visibility.Visible;
    }

    private static string NameOf(TrayIconItem item)
        => string.IsNullOrWhiteSpace(item.Label) ? "那个图标" : $"“{item.Label}”";
}
