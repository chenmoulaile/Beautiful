using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Layout;
using Beautiful.Core.Switcher;
using Beautiful.Core.Themes;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes; // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突
using Brush = System.Windows.Media.Brush;      // 同上，System.Drawing.Brush
using ColorConverter = System.Windows.Media.ColorConverter; // 同上，System.Drawing.ColorConverter
using MouseEventArgs = System.Windows.Input.MouseEventArgs; // 同上，System.Windows.Forms.MouseEventArgs
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Beautiful.App.Switcher;

/// <summary>
/// Alt+Tab 可见面板（Task 12）：把 <see cref="SwitcherService"/> 的 MRU 分组渲染成一排应用图标，
/// 高亮当前选择、字幕显示选中应用，松开触发键提交、Esc 取消。
///
/// 【三态机】<c>Hidden → Showing(selection) → Commit | Cancel</c>，两条出口都回到 Hidden：
/// <list type="bullet">
/// <item><description><see cref="SwitcherService.Opened"/>（由 hook 的 OpenRequested 驱动）→ 建列表 +
/// Showing。服务把选择初始化到"当前前台 App"，而首个 <c>NextRequested</c> 与 <c>OpenRequested</c>
/// 在同一次排空里到达（见 <see cref="AltTabHook.Drain"/>），所以首帧渲染时高亮已经落在下一个应用上
/// ——正是 macOS Cmd+Tab 的语义，面板侧不需要"打开即 +1"的特例（那样会切两格）。</description></item>
/// <item><description>Next/Previous → 服务已用 <see cref="WindowListMerger.NextIndex"/> 步进 App 行，
/// 面板在此之上实现 macOS 的"二次落到同一 App 展开窗口子行"（<see cref="Step"/>）。</description></item>
/// <item><description>CommitRequested → 服务 <c>CommitSelected()</c> 后发 Closed → 面板隐藏；
/// CancelRequested → 服务 <c>Cancel()</c> 后发 Closed → 面板隐藏且**不改前台**。</description></item>
/// </list>
///
/// 【为什么不复用 FluentPopupHost】它是**激活式**弹层（Task 2 为了键盘可达专门夺前台：
/// ShowActivated=true + ForceForeground）。切换面板的前提恰好相反：Alt 还按在用户手里，面板夺前台会
/// ①打断系统侧修饰键状态机（原生会话可能提前结束）、②让面板自己成为前台，随后 Commit 的目标就变成
/// 我们的窗口。键盘输入全部来自全局低级钩子（不依赖焦点），因此这里必须
/// <c>ShowActivated=false</c> + <c>WS_EX_NOACTIVATE</c> + <c>Focusable=false</c>。
///
/// 【形态】<see cref="Window.AllowsTransparency"/>=true 的分层窗口（WPF 唯一保证客户端逐像素 alpha 的
/// 模式）：圆角由 <c>Card</c> 自绘（macOS 18），"玻璃"由**显示前抓屏自绘模糊**
/// 提供（<see cref="ScreenBlurBackdrop"/>）。v2 的两条路径已整枝删除：<c>SetWindowRgn</c> 裁形（真机上
/// 根本没生效，面板带硬边）与"非分层 + DWM 系统背板"（半透明卡底下的客户端像素不被合成 → 黑矩形）。
/// 定位全程物理像素 + 目标显示器有效 DPI 缩放（沿用 FluentPopupHost 的混合 DPI 结论），
/// 并按工作区宽度逐级缩小图标，绝不让面板超出屏幕。
///
/// 【不留残影】每条路径都收口到 <see cref="HidePanel"/>：服务 Closed、hook 判定未打开、异常、以及
/// 150ms 的"触发键是否还按着"看门狗（<see cref="Watchdog_Tick"/>）。Alt 的 key-up 若被漏收、或会话
/// 中钩子被应急卸载，都会由看门狗兜住——按住 Alt 却看到一个永不消失的面板是本功能最坏的失败形态。
///
/// 【线程】仅 WPF UI 线程（与 <see cref="SwitcherService"/> 同一约定）。
/// </summary>
public sealed partial class SwitcherWindow : Window
{
    /// <summary>三态机相位。Commit/Cancel 是从 <see cref="Phase.Showing"/> 回到 Hidden 的两条转移，
    /// 不是驻留态（转移动作由 <see cref="SwitcherService"/> 完成，面板只负责"消失"）。</summary>
    private enum Phase { Hidden, Showing }

    private const double MinIconSize = 24;   // 缩小到顶（再小图标就糊了）
    private const int WatchdogIntervalMs = 150;
    private const int AbsoluteTimeoutMs = 60_000; // 最后一道保险（主判据是"触发键还按着吗"）：
                                                  // 按住 Alt 慢慢挑 60s 是合法的，所以不能设太短

    private readonly SwitcherService _service;
    private readonly List<AppTileItem> _tiles = new();
    private readonly HashSet<int> _visitedApps = new();
    private DispatcherTimer? _watchdog;
    private int _watchdogTicks;

    private Phase _phase = Phase.Hidden;
    private double _baseIconSize = 56;   // 样式给的图标尺寸（缩小适配前先记下）
    private double _panelRadius = 18;
    private Thickness _bodyPadding = new(16, 13, 16, 13);
    private double _itemGap = 4;
    private double _verticalBias = 0.42; // 面板中心在工作区高度的位置比例（macOS 略高于中线）
    private bool _captionVisible = true;
    private ScreenBackdrop? _backdrop;   // 本次会话的抓屏背板（显示前抓一次，面板长大只重瞄不重抓；null = 实心卡底）
    private bool _disposed;

    // ---- macOS 二次展开（同一 App 多窗口）----
    private int _expandedApp = -1;   // -1 = 未展开
    private int _windowCursor;       // 展开后子行的选中位（服务被 App 步进占用，面板自己记住）
    private int _subSteps;

    public SwitcherWindow(SwitcherService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        InitializeComponent();
        _service = service;

        // 渲染层只订阅服务（状态单一来源）+ hook 的 OpenRequested（空列表时服务不发 Opened，
        // 面板必须自己保证"不显示/隐藏"，否则会出现空面板闪一下或旧面板滞留）。
        service.Opened += ServiceOpened;
        service.SelectionChanged += SelectionChanged;
        service.Closed += ServiceClosed;
        service.Hook.OpenRequested += HookOpenRequested;
        service.Hook.NextRequested += HookNext;
        service.Hook.PreviousRequested += HookPrevious;
    }

    // ---------- 会话事件（全部 try/catch：任何异常都必须把面板关掉） ----------

    private void ServiceOpened()
    {
        if (_disposed) return;
        try
        {
            var apps = _service.Apps;
            // 空列表服务已拦住；只有一个应用且它只有一个窗口 → 无处可切，不闪面板
            if (apps.Count == 0 || (apps.Count == 1 && apps[0].Windows.Count < 2))
            {
                HidePanel();
                return;
            }
            BuildTiles(apps);
            ApplyStyle();
            _visitedApps.Clear();
            _expandedApp = -1;
            _windowCursor = 0;
            _subSteps = 0;
            WindowRow.Visibility = Visibility.Collapsed;
            _visitedApps.Add(_service.SelectedAppIndex); // 起始应用算"到过"：绕回来即展开它的窗口
            SyncSelection();
            ShowPanel();
        }
        catch (Exception ex)
        {
            Logger.Warn($"切换面板显示失败: {ex.Message}");
            HidePanel();
        }
    }

    private void SelectionChanged()
    {
        if (_disposed || _phase != Phase.Showing) return;
        try { SyncSelection(); }
        catch (Exception ex) { Logger.Warn($"切换面板高亮刷新失败: {ex.Message}"); HidePanel(); }
    }

    private void ServiceClosed()
    {
        if (_disposed) return;
        try { HidePanel(); }
        catch (Exception ex) { Logger.Warn($"切换面板关闭失败: {ex.Message}"); ForceHideUnsafe(); }
    }

    private void HookOpenRequested()
    {
        // 服务在列表为空时不发 Opened（也不置 IsOpen）：此时面板必须保持/回到隐藏态。
        if (_disposed || _service.IsOpen) return;
        HidePanel();
    }

    private void HookNext() { if (!_disposed) Step(true); }
    private void HookPrevious() { if (!_disposed) Step(false); }

    /// <summary>
    /// 步进渲染。App 行的步进已由服务在事件顺序上先完成（服务构造函数先于面板订阅同一事件源），
    /// 面板只做两件事：将高亮同步过去，以及 macOS 的"二次落到同一 App → 展开该 App 的窗口子行"。
    /// 展开态下 App 行被服务步进走的那一格会被 <see cref="SwitcherService.Select"/> 拉回展开的 App，
    /// 改为在该 App 的窗口内步进；子行绕完一圈自动收起，把键盘交还 App 行（不会卡死在展开态）。
    /// </summary>
    private void Step(bool forward)
    {
        if (_phase != Phase.Showing) return;
        try
        {
            var apps = _service.Apps;
            if (apps.Count == 0) { HidePanel(); return; }

            if (_expandedApp >= 0 && _expandedApp < apps.Count)
            {
                var app = apps[_expandedApp];
                int n = app.Windows.Count;
                if (n < 2) { CollapseExpansion(); }
                else
                {
                    _subSteps++;
                    if (_subSteps > n - 1) CollapseExpansion(); // 已遍历完全部窗口 → 收起
                    else
                    {
                        _windowCursor = WindowListMerger.NextIndex(_windowCursor, n, !forward);
                        _service.Select(_expandedApp, _windowCursor);
                    }
                }
            }
            else
            {
                int idx = _service.SelectedAppIndex;
                if (idx >= 0 && idx < apps.Count)
                {
                    var app = apps[idx];
                    if (app.Windows.Count > 1 && (_visitedApps.Contains(idx) || apps.Count == 1))
                        ExpandExpansion(idx, app);
                    else _visitedApps.Add(idx);
                }
            }
            SyncSelection();
        }
        catch (Exception ex)
        {
            Logger.Warn($"切换面板步进失败: {ex.Message}");
            HidePanel();
        }
    }

    private void ExpandExpansion(int index, SwitcherApp app)
    {
        _expandedApp = index;
        _windowCursor = 0;
        _subSteps = 0;
        // 必须用 _tiles 里的那份实例：ItemsSource 换成新副本的话，SyncSelection 就点不亮任何子瓦片
        WindowRow.ItemsSource = _tiles[index].SubTiles;
        WindowRow.Visibility = Visibility.Visible;
        Reposition(); // 子行出现 → 面板变高，重新居中并换区域
        Logger.Info($"切换面板：展开 {app.AppName} 的 {app.Windows.Count} 个窗口");
    }

    private void CollapseExpansion()
    {
        if (_expandedApp < 0) return;
        _expandedApp = -1;
        _windowCursor = 0;
        _subSteps = 0;
        WindowRow.Visibility = Visibility.Collapsed;
        WindowRow.ItemsSource = null;
        Reposition();
    }

    /// <summary>鼠标/其它路径直接结束会话并隐藏（不提交）：宿主在关闭开关/应急停用时调用。</summary>
    public void ForceClose()
    {
        if (_disposed) return;
        try { _service.Cancel(); } catch { /* 服务可能已释放：面板自己隐藏即可 */ }
        HidePanel();
    }

    // ---------- 列表构建 ----------

    private void BuildTiles(IReadOnlyList<SwitcherApp> apps)
    {
        _tiles.Clear();
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            _tiles.Add(new AppTileItem(i, app.AppName, _service.GetIcon(app.AppId), SubTilesOf(app)));
        }
        AppRow.ItemsSource = null;
        AppRow.ItemsSource = _tiles;
    }

    /// <summary>窗口子行瓦片：标题 + 该 App 图标（同 App 的窗口共享图标，够用且省一次提取）。</summary>
    private List<WindowTileItem> SubTilesOf(SwitcherApp app)
    {
        var list = new List<WindowTileItem>(app.Windows.Count);
        var icon = _service.GetIcon(app.AppId);
        for (int i = 0; i < app.Windows.Count; i++)
            list.Add(new WindowTileItem(i, WindowTitleOf(app.Windows[i].Title), icon));
        return list;
    }

    /// <summary>字幕与子行都用同一份"去噪"标题：多行标题压成一行，过长由 TextTrimming 省略。</summary>
    private static string WindowTitleOf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "窗口";
        var one = raw.Replace('\n', ' ').Replace('\r', ' ');
        while (one.Contains("  ")) one = one.Replace("  ", " ");
        return one.Trim();
    }

    private void SyncSelection()
    {
        int app = _service.SelectedAppIndex, win = _service.SelectedWindowIndex;
        for (int i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].IsSelected = i == app;
            if (_tiles[i].SubTiles is null) continue;
            foreach (var s in _tiles[i].SubTiles)
                s.IsSelected = i == app && _expandedApp == app && s.Index == win;
        }
        if (_captionVisible) UpdateCaption(app, win);
    }

    private void UpdateCaption(int appIndex, int winIndex)
    {
        if (appIndex < 0 || appIndex >= _tiles.Count) { Caption.Text = ""; return; }
        var tile = _tiles[appIndex];
        string text = tile.AppName;
        if (tile.SubTiles.Count > 1)
        {
            if (_expandedApp == appIndex)
                text = $"{tile.AppName} — {tile.SubTiles[Math.Clamp(winIndex, 0, tile.SubTiles.Count - 1)].Title}";
            else text = $"{tile.AppName} ({tile.SubTiles.Count})"; // 提示：再按一次 Tab 展开窗口
        }
        Caption.Text = text;
    }

    // ---------- 样式 ----------

    /// <summary>
    /// macOS 卡片样式（v4 唯一样式）：居中大圆角亚克力 + 56px 图标 + 字幕 + 原样图标。
    /// 旧的 Linux 覆盖条/鸿蒙胶囊分支已随主题收敛删除；<c>SwitcherStyle</c> 旧值由 Normalized 自愈。
    /// </summary>
    private void ApplyStyle()
    {
        var accent = (Brush)Resources["SwitcherAccent"];
        _baseIconSize = 56; _panelRadius = 18; _itemGap = 4; _verticalBias = 0.42;
        _captionVisible = true;
        _bodyPadding = new Thickness(16, 13, 16, 13);
        accent = HexBrush("#0A84FF", accent);
        Resources["SwitcherTilePadding"] = new Thickness(7);
        Resources["SwitcherTileRadius"] = new CornerRadius(14);
        Resources.Remove("SwitcherIconClip"); // macOS 图标原样，不遮罩
        Resources["SwitcherAccent"] = accent;
        Resources["SwitcherIconSize"] = _baseIconSize;

        // 形状唯一来源：自绘半径随样式。Scrim/Tint/Glow 是 Card 的**子 Border**——WPF 的 CornerRadius
        // 只裁剪 Border 自己的背景，不裁剪子元素；这三层若不带同一半径，其矩形背景会在"该透明的
        // 圆角缺口"里照画不误（真机取证：圆角被画回直角深色块）。必须与 Card 同步赋值。
        var cardRadius = new CornerRadius(_panelRadius);
        Card.CornerRadius = cardRadius;
        Scrim.CornerRadius = cardRadius;
        TintLayer.CornerRadius = cardRadius;
        GlowLayer.CornerRadius = cardRadius;
        Body.Margin = _bodyPadding;
        Caption.Visibility = _captionVisible ? Visibility.Visible : Visibility.Collapsed;
        AppRow.ItemContainerStyle = GapContainerStyle(_itemGap);
        WindowRow.ItemContainerStyle = GapContainerStyle(Math.Max(2, _itemGap - 1));
        // 先把卡底落成**实心**渐变：背板抓屏晚于此处，ShowPanel 抓到背板后再由 ApplyCardFill
        // 换成模糊画刷。默认值必须是"什么都不依赖"的实心底，这样任何一条降级路径
        // （抓屏失败 / 环境开关关闭）都不会出现未绘制的透明洞。
        Card.Background = (Brush)Resources["CardFillSolid"];
        Scrim.Background = Brushes.Transparent;
    }

    /// <summary>条目间距（模板外的样式差异，直接用容器 Style 给 Margin，免得多套一层资源间接层）。</summary>
    private static Style GapContainerStyle(double gap)
    {
        var style = new Style(typeof(ContentPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(gap, 0, gap, 0)));
        return style;
    }

    /// <summary>#RRGGBB → 冻结画刷；解析失败回退传入的默认画刷（样式绝不因色值写错而崩）。</summary>
    private static Brush HexBrush(string hex, Brush fallback)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch { return fallback; }
    }

    // ---------- 显示 / 隐藏 / 定位 ----------

    private void ShowPanel()
    {
        if (_phase == Phase.Showing && IsVisible) { Reposition(); return; }
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // 【抓屏必须在首次 Show 之前】此刻本窗口尚不可见，取回的是"面板下方"的干净内容；
        // 显示之后再抓就会把面板自己的像素抓进背板（鬼影）。矩形由与 Reposition 同一套测量算出，
        // 因此落位后只是"重瞄"同一张已抓好的图，面板长大也不会重抓、也不会重新缩放。
        // BEAUTIFUL_NOBLUR=1 / 抓屏失败时 _backdrop=null → 卡底维持 ApplyStyle 落的实心渐变。
        _backdrop = ScreenBlurBackdrop.ForPanel(ComputePanelRect(hwnd));
        if (!IsVisible) Show(); // 非激活式：Show 不会夺前台（ShowActivated=false + WS_EX_NOACTIVATE）
        _phase = Phase.Showing;
        _expandedApp = -1;
        Reposition();
        StartWatchdog();
    }

    private void HidePanel()
    {
        StopWatchdog();
        _phase = Phase.Hidden;
        _expandedApp = -1;
        _windowCursor = 0;
        _subSteps = 0;
        _visitedApps.Clear();
        // 会话结束即弃掉背板：下次打开必须按"那时的屏幕内容"重抓（留着旧图 = 陈旧背景），
        // 同时释放那张 1/4 分辨率位图。
        _backdrop = null;
        if (WindowRow.Visibility == Visibility.Visible)
        {
            WindowRow.Visibility = Visibility.Collapsed;
            WindowRow.ItemsSource = null;
        }
        if (IsVisible) Hide();
    }

    /// <summary>最后的兜底：连 Hide() 都抛异常时（Dispatcher 关停竞态）只改状态，让调用方继续走。</summary>
    private void ForceHideUnsafe()
    {
        _phase = Phase.Hidden;
        _backdrop = null;
        StopWatchdog();
        try { if (IsVisible) Hide(); } catch { /* 进程正在退出 */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // 非激活 + 不进 alt-tab 列表 + 不进任务栏（三件套，缺一即可能被系统当成可切换窗口）。
        // 这是 Alt 键状态机的前提（键盘输入全部来自全局低级钩子，不依赖焦点），不要"顺手"改成激活式。
        var ex = GetWindowLongPtr(hwnd, GwlpExStyle);
        SetWindowLongPtr(hwnd, GwlpExStyle, ex | WsExNoactivate | WsExToolwindow);
    }

    /// <summary>
    /// 卡底两层：抓屏模糊（Card.Background）+ 深色遮罩（Scrim，负责文字/图标字幕的对比度）。
    /// <para>
    /// 每次重排都按**当前面板矩形**重瞄：同一张已抓好的图，长大只是露出旁边已抓好的内容，
    /// 既不重抓屏也不重新缩放（这就是捕获区带余量的意义）。
    /// 任何一条分支都必须把整张卡铺满：本窗口是分层窗口，"没画到"= 真透明 = 直接看穿到桌面，
    /// 那正是 v2 真机上"面板矩形里露背景"的故障形态。背板不可用（抓屏失败 / <c>BEAUTIFUL_NOBLUR=1</c>
    /// / 瞄准越界）时一律回落实心渐变 <c>CardFillSolid</c>。
    /// </para>
    /// </summary>
    private void ApplyCardFill(in PhysicalRect panelPx)
    {
        Brush? blur = _backdrop is { } b && b.Aim(panelPx) ? b.Brush : null;
        Card.Background = blur ?? (Brush)Resources["CardFillSolid"];
        // Scrim 铺的是**深色遮罩**而不是模糊画刷本身（与 FluentMenu.OnBackdropReady 同一两层材质）：
        // 直接把模糊赋给 Scrim 等于没有任何东西压暗它，卡浮在亮窗口上就被冲成浅灰、白字幕失去对比度。
        // 实心底分支保持透明——CardFillSolid 本身已是深色，再压一层只会闷。
        Scrim.Background = blur is not null ? (Brush)Resources["CardFillBlur"] : Brushes.Transparent;
    }

    /// <summary>
    /// 按内容测量窗口尺寸并落位：目标显示器 = 前台窗口所在屏（用户在哪个屏就在哪切），
    /// 物理尺寸 = 测量的 DIP × 该屏有效 DPI（混合 DPI 结论同 FluentPopupHost）；
    /// 超出工作区宽度则逐级缩小图标后重测，直到装得下（最小 24px）。
    /// 落位后把背板**重瞄**到新的卡片矩形（同一张已抓好的图，不重抓屏、不重新缩放）。
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var target = ComputePanelRect(hwnd);
        SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height, SwpNoactivate);
        ApplyCardFill(target);
    }

    /// <summary>
    /// 测量并算出面板的**目标物理矩形**（同时将 WPF 侧 <c>Width/Height</c> 定形）。
    /// 与 <see cref="Reposition"/> 拆开的唯一理由：抓屏要在窗口可见之前拿到矩形，而那次还不能 SetWindowPos。
    /// 两次调用之间没有内容变化 ⇒ 结果恒等（测量与图标缩小都是确定性纯算）。
    /// </summary>
    private PhysicalRect ComputePanelRect(IntPtr hwnd)
    {
        var (work, scale) = TargetMonitorOf(hwnd);
        double maxWidth = Math.Max(240, work.Width * 0.92);

        double icon = _baseIconSize;
        Size desired;
        for (var guard = 0; guard < 6; guard++)
        {
            SetIconSize(icon);
            Body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desired = Body.DesiredSize;
            if (desired.Width * scale + _bodyPadding.Left + _bodyPadding.Right <= maxWidth &&
                (desired.Height + _bodyPadding.Top + _bodyPadding.Bottom) * scale <= work.Height)
                break;
            icon = Math.Max(MinIconSize, icon * 0.8);
        }

        double wDip = desired.Width + _bodyPadding.Left + _bodyPadding.Right;
        double hDip = desired.Height + _bodyPadding.Top + _bodyPadding.Bottom;
        double wPx = Math.Round(wDip * scale), hPx = Math.Round(hDip * scale);

        // Width/Height 交给 WPF（布局按 DIP 自洽），位置/尺寸最后由 SetWindowPos 强制覆盖为物理像素
        Width = wDip;
        Height = hDip;
        var x = (int)Math.Round(work.X + (work.Width - wPx) / 2);
        var yCenter = work.Y + work.Height * _verticalBias;
        var y = (int)Math.Round(yCenter - hPx / 2);
        y = Math.Max((int)(work.Y + 8), Math.Min(y, (int)(work.Y + work.Height - hPx - 8)));
        x = Math.Max((int)(work.X + 8), x);
        return new PhysicalRect(x, y, x + (int)wPx, y + (int)hPx);
    }

    /// <summary>图标尺寸变化后同步：macOS 图标原样不遮罩。</summary>
    private void SetIconSize(double size)
    {
        Resources["SwitcherIconSize"] = size;
        Resources.Remove("SwitcherIconClip");
    }

    /// <summary>目标显示器工作区（物理像素）与其有效 DPI 缩放；异常一律退化为主屏 + 窗口 DPI。</summary>
    private (Rect Work, double Scale) TargetMonitorOf(IntPtr hwnd)
    {
        var pt = new POINT();
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && GetWindowRect(fg, out var r))
            pt = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        else if (GetCursorPos(out var c)) pt = c;

        var mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var work = mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi)
            ? new Rect(mi.rcWork.Left, mi.rcWork.Top,
                       mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top)
            : new Rect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)); // SM_CXSCREEN/SM_CYSCREEN 兜底

        double scale = 0;
        if (mon != IntPtr.Zero &&
            GetDpiForMonitor(mon, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        if (scale <= 0) scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        return (work, scale);
    }

    // ---------- 防卡死看门狗 ----------

    private void StartWatchdog()
    {
        _watchdogTicks = 0;
        if (_watchdog is null)
        {
            _watchdog = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(WatchdogIntervalMs)
            };
            _watchdog.Tick += Watchdog_Tick;
        }
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        if (_watchdog is null) return;
        _watchdog.Stop();
    }

    /// <summary>
    /// 触发键是否还按着（<c>GetAsyncKeyState</c> 是系统权威状态）。松开被漏收、或会话中途钩子被
    /// 应急卸载/开关关闭时，钩子不会再发 Commit/Cancel——没有这道兜底就会留下"按住 Alt 也不消失"的
    /// 残影面板。这里**只隐藏不提交**：按键会话早已不存在，替用户切窗口反而是意外行为。
    /// </summary>
    private void Watchdog_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _phase != Phase.Showing) { StopWatchdog(); return; }
        if (++_watchdogTicks * WatchdogIntervalMs >= AbsoluteTimeoutMs)
        {
            Logger.Warn($"切换面板：显示超过 {AbsoluteTimeoutMs / 1000}s（疑似按键会话丢失），强制隐藏且不提交");
            HidePanel();
            return;
        }
        if (TriggerKeyDown()) return;
        Logger.Info("切换面板：触发键已松开但会话未提交（按键漏收），隐藏且不切换");
        HidePanel();
    }

    /// <summary>按 <c>SwitcherHotkey</c> 解析出需要检测的修饰键 VK 集合（与 AltTabHook 的解析口径一致）。</summary>
    private bool TriggerKeyDown()
    {
        var hotkey = (SettingsService.Current.SwitcherHotkey ?? "alt+tab").ToLowerInvariant();
        bool alt = false, ctrl = false, win = false;
        foreach (var raw in hotkey.Split('+', '-', ' '))
        {
            switch (raw.Trim())
            {
                case "alt": case "option": case "menu": alt = true; break;
                case "ctrl": case "control": ctrl = true; break;
                case "win": case "windows": case "meta": case "super": case "cmd": win = true; break;
            }
        }
        if (!alt && !ctrl && !win) alt = true;
        if (alt && (IsDown(VkLMenu) || IsDown(VkRMenu))) return true;
        if (ctrl && (IsDown(VkLControl) || IsDown(VkRControl))) return true;
        if (win && (IsDown(VkLWin) || IsDown(VkRWin))) return true;
        return !(alt || ctrl || win);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;

    // ---------- 鼠标 ----------

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_phase != Phase.Showing || sender is not FrameworkElement { DataContext: AppTileItem tile }) return;
        // 悬停移动高亮：已展开的 App 保留它的窗口位，其它 App 回到首个窗口
        try { _service.Select(tile.Index, tile.Index == _expandedApp ? _windowCursor : 0); }
        catch (Exception ex) { Logger.Warn($"切换面板悬停选择失败: {ex.Message}"); HidePanel(); }
    }

    private void Tile_MouseClick(object sender, MouseButtonEventArgs e)
    {
        if (_phase != Phase.Showing || sender is not FrameworkElement { DataContext: AppTileItem tile }) return;
        try
        {
            _service.Select(tile.Index, tile.Index == _expandedApp ? _windowCursor : 0);
            _service.CommitSelected(); // → Closed → HidePanel()
        }
        catch (Exception ex) { Logger.Warn($"切换面板点击提交失败: {ex.Message}"); HidePanel(); }
    }

    private void SubTile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_expandedApp < 0 || sender is not FrameworkElement { DataContext: WindowTileItem sub }) return;
        try { _windowCursor = sub.Index; _service.Select(_expandedApp, sub.Index); }
        catch (Exception ex) { Logger.Warn($"切换面板子行悬停失败: {ex.Message}"); HidePanel(); }
    }

    private void SubTile_MouseClick(object sender, MouseButtonEventArgs e)
    {
        if (_expandedApp < 0 || sender is not FrameworkElement { DataContext: WindowTileItem sub }) return;
        try
        {
            _service.Select(_expandedApp, sub.Index);
            _service.CommitSelected();
        }
        catch (Exception ex) { Logger.Warn($"切换面板子行点击失败: {ex.Message}"); HidePanel(); }
    }

    // ---------- 生命周期 ----------

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    /// <summary>断开全部订阅（服务与钩子都活得比面板久：面板 Close 后不得留下事件引用）。</summary>
    private void Detach()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatchdog();
        if (_watchdog is not null) { _watchdog.Tick -= Watchdog_Tick; _watchdog = null; }
        _service.Opened -= ServiceOpened;
        _service.SelectionChanged -= SelectionChanged;
        _service.Closed -= ServiceClosed;
        var hook = _service.Hook;
        hook.OpenRequested -= HookOpenRequested;
        hook.NextRequested -= HookNext;
        hook.PreviousRequested -= HookPrevious;
        _tiles.Clear();
        AppRow.ItemsSource = null;
        WindowRow.ItemsSource = null;
    }
}

/// <summary>App 行瓦片视图数据：Icon 来自服务缓存（面板不提取图标，键路径零磁盘 IO）。</summary>
internal sealed class AppTileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    internal AppTileItem(int index, string appName, ImageSource? icon, List<WindowTileItem> subTiles)
    {
        Index = index;
        AppName = appName;
        Icon = icon;
        SubTiles = subTiles;
    }

    public int Index { get; }
    public string AppName { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    public List<WindowTileItem> SubTiles { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>窗口子行瓦片视图数据（同一 App 的一个窗口：标题 + 该 App 图标）。</summary>
internal sealed class WindowTileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    internal WindowTileItem(int index, string title, ImageSource? icon)
    {
        Index = index; Title = title; Icon = icon;
    }

    public int Index { get; }
    public string Title { get; }
    public ImageSource? Icon { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
