using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏的"系统托盘图标区"：把 Windows 通知区域（含隐藏图标溢出区）里的第三方托盘图标枚举出来平铺，
/// 点击/右键时把消息转发回图标自己的窗口，让那个应用照常弹它自己的菜单——全程不借系统那个浮层。
/// <para>
/// 【材质与 WiFi 面板/控制中心完全同一套】根 <c>Shell.Margin</c> 那一圈是给自绘投影留位（宿主按
/// <see cref="IPopupShadow"/> 把它从锚点偏移里扣掉），并实现 <see cref="IBackdropAware"/>：
/// 有抓屏模糊就铺模糊 + 深色遮罩，没有就铺实心卡底——分层窗口上"没画到"就是能看穿到桌面的洞，
/// 两条分支都必须把整张卡铺满。
/// </para>
/// <para>
/// 【为什么所有取数都在后台线程】<see cref="TrayIconSource"/> 的每一条路都是<b>别人的进程</b>：
/// 远程读 explorer 的按钮数组、若干次跨进程 send、整棵无障碍树遍历，几十到几百毫秒。
/// 面板是"显示一次"的瞬态表面，把它放 UI 线程上就是打开卡顿，最坏情况是常驻进程被拖住。
/// 回投一律走 <see cref="Dispatcher"/>（即 <c>await</c> 的默认续接），每个回调都先问 <c>IsLoaded</c>
/// ——用户可能已经关了面板。
/// </para>
/// <para>
/// 【为什么没有"只读格"这种东西】不能转发点击的图标（认不出 owner、消息号可疑、应用未响应）不会被 <c>IsEnabled=false</c>
/// 处理掉：WPF 的禁用元素不吃鼠标事件，连 ToolTip 都不弹，用户只会看到一个点不出任何反应的方块（死控件）。
/// 这里让格子保持可点，点下去就把"为什么不能转发"写进状态行——每一格都有确定的回应。
/// 视觉上用 0.62 不透明度做提示，不靠颜色解释（解释在 ToolTip 与状态行里）。
/// </para>
/// </summary>
public partial class TrayPanel : UserControl, IBackdropAware, IPopupShadow
{
    /// <summary>一页平铺多少格（与 Core 同源，不在界面里另抄一个常数）。</summary>
    private const int PageSize = TrayLayout.DefaultPageSize;

    private readonly TrayIconSource _source = new();
    private FluentPopupHost? _host;

    private TrayReadResult _result = new();
    private IReadOnlyList<TrayIconItem> _items = [];

    /// <summary>当前页（0 基）。刷新后会被 <see cref="TrayLayout.ClampPage"/> 拉回合法范围。</summary>
    private int _page;

    /// <summary>是否已有一次读取在跑：连点"刷新"会叠出多串跨进程读，最后一串赢者未必是最新的那串。</summary>
    private bool _reading;

    /// <summary>投影留白 = 自己根元素的 Margin（唯一来源，不与 XAML 各说各话）。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    private System.Windows.Threading.DispatcherTimer? _realPixelTimer;

    /// <summary>
    /// 真实托盘像素（P4-T8 截屏路线，实验）：2s 截一次系统通知区并贴到面板顶部。
    /// <para>
    /// 【为什么要判空白】本程序默认隐藏 Windows 任务栏，此时托盘根本不渲染，截到的是纯色条；
    /// 直接贴上去就是一块灰条。这里按像素方差判"是不是空白"，空白就整块隐藏——
    /// 宁可看不见，也不给一块假图。
    /// </para>
    /// </summary>
    private void StartRealPixels()
    {
        _realPixelTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RefreshRealPixels(), Dispatcher);
        _realPixelTimer.Start();
        RefreshRealPixels();
    }

    private void RefreshRealPixels()
    {
        if (!IsLoaded) { _realPixelTimer?.Stop(); return; }

        var source = Services.TrayCapture.Capture();
        if (source is null || IsBlank(source))
        {
            RealPixelsBox.Visibility = Visibility.Collapsed;
            return;
        }

        RealPixelsImage.Source = source;
        RealPixelsBox.Visibility = Visibility.Visible;
    }

    /// <summary>判空白：抽样像素全部接近同一个颜色即视为"托盘没渲染"。</summary>
    private static bool IsBlank(System.Windows.Media.Imaging.BitmapSource source)
    {
        try
        {
            var stride = source.PixelWidth * 4;
            var buffer = new byte[stride * source.PixelHeight];
            source.CopyPixels(buffer, stride, 0);

            int minB = 255, maxB = 0;
            // 每 97 个像素采一个（质数步长避免与图标间距共振），只看单个通道的极差
            for (var i = 0; i + 3 < buffer.Length; i += 97 * 4)
            {
                var b = buffer[i];
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
                if (maxB - minB > 24) return false;   // 有对比度：不是纯色
            }

            return true;
        }
        catch (Exception)
        {
            return true;   // 读不出像素就当空白，不展示
        }
    }

    public TrayPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); StartRealPixels(); };
    }

    /// <summary>宿主注入：需要能收起自己（转发右键、展开系统隐藏区之前都必须先关）。</summary>
    public void AttachHost(FluentPopupHost host) => _host = host;

    /// <summary>
    /// 面板拆除（宿主 Closed 时由打开方调用）。<see cref="TrayIconSource"/> 不驻留句柄
    /// （远程内存在每次读取的 finally 里就还掉了），这里的主要动作是断掉宿主引用，
    /// 别让瞬态对象图被一张已经关闭的弹层拖着。
    /// </summary>
    public void Shutdown()
    {
        _host = null;
        _source.Dispose();
    }

    /// <summary>
    /// 拿到抓屏模糊 → 卡底铺该画刷并在其上压 <c>CardFillBlur</c>；拿不到 → 铺实心 <c>CardFillSolid</c> + 遮罩透明。
    /// 与控制中心/WiFi 面板完全同一段：降级链上没有任何一环会画成黑。
    /// </summary>
    public void OnBackdropReady(ScreenBackdrop? backdrop)
    {
        var blur = backdrop?.Brush;
        if (blur is not null)
        {
            Card.Background = blur;
            Scrim.Background = (Brush)Resources["CardFillBlur"];
            return;
        }
        Card.Background = (Brush)Resources["CardFillSolid"];
        Scrim.Background = Brushes.Transparent;
    }

    // ---------- 数据 ----------

    /// <summary>后台读两路 → await 回投 → 重画。取不到就画空态 + 写明两路各自的原因，绝不留旧数据冒充新的。</summary>
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
                // Read 内部已经把两条路各自的失败变成状态字符串；走到这里说明是意外（内存/环境异常）。
                // 常驻进程不能因为一次枚举炸掉：写回状态行，界面还能用。
                Logger.Warn($"托盘枚举异常: {ex.Message}");
                if (!IsLoaded) return;
                ShowStatus($"读取托盘图标时出了意外：{ex.Message}");
                result = new TrayReadResult();
            }
            if (!IsLoaded) return;   // 面板可能已经关了
            _result = result;
            _items = result.Items;
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

    /// <summary>
    /// 两路各自的结果并排写出来（"这一格为什么点不动""为什么这台机器一个都读不到"的第一手判据）。
    /// 【为什么把它常驻显示而不是塞进日志】这个项目真机上的主要故障形态是"某个 Windows 版本上读不到"，
    /// 而用户能提供给我们的只有截图——把两条路的结果直接写在面板底部，一次截图就是完整证据。
    /// </summary>
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

    /// <summary>一格图标：能裁到真实像素就画它，裁不到就画一个中性空框；下面不放文字（真实托盘本来就没有名字）。</summary>
    private UIElement MakeTile(TrayIconItem item)
    {
        var visual = item.Image is { } img
            // 用 24 DIP 显示 16–24 物理像素的裁图：托盘本来就小，放大会糊，但 24 在 150% 屏上仍是原尺寸附近
            ? new Image { Source = img, Width = 24, Height = 24, Stretch = Stretch.Uniform }
            : MakePlaceholder();

        var tile = new Button
        {
            Content = visual,
            Style = (Style)Resources["TrayTileStyle"],
            ToolTip = BuildTip(item),
            Tag = item,
            // 不能转发的格子压暗一点做提示；解释全在 ToolTip 与点击后的状态行里（不靠颜色当唯一说明）
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
    /// 【为什么把来源写进 ToolTip】两路的可点击能力不同（经典路能转发回它自己的窗口，无障碍树走 Invoke），
    /// 用户遇到"这格点不动"时最先要知道的不是"坏了"，而是"这一格本来是从哪读来的"。
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

    /// <summary>左键：交给那个应用（面板不关——很多应用的默认动作就是显示自己的主窗口，成功时界面自己会失活）。</summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item }) return;
        _ = ForwardAsync(item, TrayClickAction.LeftClick, closeFirst: false);
    }

    /// <summary>
    /// 右键：交回那个应用，让它弹<b>它自己的</b>菜单。
    /// 【为什么必须先拆弹层】本面板由 <see cref="FluentPopupHost"/> 承载，是 Topmost 的顶层窗口；
    /// 那个应用的菜单是它自己的 <c>TrackPopupMenu</c>。两个顶层弹层叠在一起时"谁压住谁"取决于时序，
    /// 观感就是"右键没反应"。所以先关自己再发（同控制中心 <c>CloseThenRun</c> 的时序理由）。
    /// </summary>
    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item }) return;
        e.Handled = true;   // 别让这次右键继续冒泡成顶栏/宿主的上下文处理
        _ = ForwardAsync(item, TrayClickAction.RightClick, closeFirst: true);
    }

    /// <summary>
    /// 转发一次点击。<b>关闭之后的结果不写界面</b>（<c>IsLoaded</c> 守卫）：点击已经发出去、收不回来，
    /// 而用户此刻看到的是那个应用自己的菜单——在这里再弹一句"已转发"反而是干扰。
    /// </summary>
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
    /// "展开系统隐藏区"：让<b>系统自己</b>弹出隐藏图标浮层。
    /// <para>【为什么这一下一定要关自己的面板】宿主要靠 <c>ForceForeground</c> 夺取前台才能键盘可达；
    /// 系统那个浮层出现时同样要拿激活，两个都要前台就是互相顶。这里的取舍是<b>让位给系统</b>：
    /// 面板关闭、浮层展开，用户直接操作系统的隐藏区（本面板不假装能代管它——见
    /// <see cref="TrayIconSource.ShowHiddenIcons"/> 的注释）。</para>
    /// <para>【为什么不做成"自动展开再读一遍"】那等于每次刷新都替用户点一下系统的托盘：
    /// 屏幕上会凭空出现一个系统浮层，观感像失控，而且它还会把我们自己挤掉。</para>
    /// </summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        _host?.Close();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var (ok, error) = _source.ShowHiddenIcons();
            if (ok) Logger.Info("托盘面板：已请求系统展开隐藏图标区");
            else Logger.Warn($"托盘面板：展开隐藏图标区失败 {error}");
            return ok;
        });
    }

    /// <summary>系统通知设置：读不到、点不动、想管"哪些图标允许显示"时的唯一真出路（这个设置页不是我们能代做的）。</summary>
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
            // 常驻进程不该因为一次打不开设置页就炸：原因写回状态行
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
