using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using Beautiful.Core.StageManager;
using static Beautiful.App.Interop.NativeMethods;
using Rect = System.Windows.Rect;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 FluentMenu.xaml.cs 同法）：
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度·左侧舞台条窗口（wp-macos-parity r5）：贴所在显示器左缘常驻，收起态只露
/// <see cref="CollapsedSliverDip"/> 的贴边小条，鼠标靠近整条滑出，离开/失焦/点击卡片切换成功后
/// 延时收回；Esc（窗口持有焦点时）立即收起。
/// <para>
/// 【宿主形态（真机取证裁定，勿改回）】本窗口是 WPF <b>分层</b>窗口（AllowsTransparency=True，
/// 与 Dock/顶栏/切换器同款路径）——Finder/QuickLookWindow.xaml 头注释实测：这台真机
/// （Win11 26300）上 WPF <b>非分层</b>窗口整机黑屏，只有分层窗口出画面。而 DWM 实况缩略图
/// 不能以分层窗口为目标（ThumbnailPreview 头注释），所以实况画面落在
/// <see cref="StageThumbHostWindow"/>（纯 Win32 玻璃弹层，owner=本窗口，浮在缩略图列正上方，
/// 与 ThumbnailPreview/DwmLiveGhost 的宿主同一血统）。卡片 DataTemplate 里的 <c>ThumbArea</c>
/// 只是占位底色：DWM 正常合成时被实况画面覆盖，不合成（注册失败/该环境不支持）时露出来
/// 充当"图标位图缺失"的占位——两种结局都不产生黑框。
/// </para>
/// <para>
/// 【布局数学（DIP）】卡片宽 <see cref="CardWidthDip"/>；每卡 = 应用行 28 + 缩略区 100 + 卡内
/// 收尾 7 + 描边 1.5×2 + 下边距 8 = 146；窗口高 = 上下内边距 16 + n×146，再夹到所在屏工作区。
/// 一次可见卡数 = <see cref="StageGrouper.DefaultMaxGroups"/> 与工作区高度的较小者（超出按
/// MRU 丢弃，不做滚动）。缩略弹层的位置/尺寸<b>不手抄常量</b>：每次都量测各 <c>ThumbArea</c>
/// 的实际布局矩形取并集——模板改版这里自动跟随。
/// </para>
/// <para>
/// 【自动隐藏（光标轮询，不用 WPF 鼠标事件）】缩略弹层是独立 HWND，光标移到它上面时
/// WPF 的 MouseLeave 会误报"离开了条"；轮询 <c>GetCursorPos</c> 对（本窗口 ∪ 弹层）屏幕
/// 矩形做几何判定（100ms 一拍，展开态才做并集判定，收起态只判贴边小条带），对 airspace
/// 与 z 序免疫。失焦（含点击卡片切换成功）另有 <c>Deactivated</c> 延时收起接住。
/// 【Esc 的诚实边界】本窗口不主动抢键盘焦点，只有用户点过卡片或条上空白（窗口被激活）后
/// Esc 才收得到；"悬停展开→按 Esc"收不起（焦点在原应用里）——那条路径靠鼠标离开延时。
/// 不装全局键盘钩子（Esc 不值得一条 WH_KEYBOARD_LL）。
/// </para>
/// <para>
/// 【线程模型】仅限 WPF UI 线程（由 StageManagerService 保证）。
/// </para>
/// </summary>
public partial class StageBarWindow : Window
{
    // ---------- 视觉/交互常量（DIP） ----------
    internal const double CardWidthDip = 196;
    internal const double CollapsedSliverDip = 6;
    private const double CardSlotHeightDip = 146;   // 应用行 28 + 缩略区 100 + 收尾 7 + 描边 1.5×2 + 下边距 8
    private const double StripVerticalPadDip = 16;  // 根 Border 上下 Padding 合计
    private const double StripTopInsetDip = 8;      // 距工作区上沿的间隙

    private const int PollIntervalMs = 100;         // 自动隐藏的光标轮询步长
    private const int MouseLeaveCollapseMs = 700;   // 光标离开整条后收回的延时
    private const int DeactivatedCollapseMs = 400;  // 失焦（含切换成功）后收回的延时

    private readonly StageManagerService _service;
    private StageThumbStrip? _thumbs;               // SourceInitialized 后才建（需要弹层 hwnd）
    private StageThumbHostWindow? _thumbHost;       // 实况缩略图的玻璃弹层（同上）
    private DispatcherTimer? _collapseTimer;
    private DispatcherTimer? _pollTimer;            // 自动隐藏的光标轮询（首次显示后常开，代价 100ms 一查）
    private long? _outsideSinceMs;                  // 光标连续离开整条的起点（TickCount64 ms）
    private bool _expanded;
    private bool _suppressed;                       // 真全屏让位：整窗退场（含贴边小条与弹层）
    private bool _shownAtLeastOnce;
    private bool _closed;

    public StageBarWindow(StageManagerService service)
    {
        InitializeComponent();
        _service = service ?? throw new ArgumentNullException(nameof(service));
        // 首帧防闪烁：Show() 创建 hwnd 前先把自己挪到屏外，Place() 随后用 SetWindowPos 精确落位
        //（WPF 默认 CenterScreen 会让小条在屏幕中央闪一帧，绝不允许）。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000;
        Deactivated += (_, _) => ScheduleCollapse(DeactivatedCollapseMs);
    }

    /// <summary>宿主窗口句柄（服务做全屏让位判定用；未初始化返回 Zero）。</summary>
    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    /// <summary>是否处于全屏让位态（诊断用）。</summary>
    public bool IsSuppressed => _suppressed;

    /// <summary>是否处于展开态（诊断用）。</summary>
    public bool IsExpanded => _expanded;

    // ---------- 生命周期 / 显隐 ----------

    /// <summary>首次显示：非激活显出收起的贴边小条（不夺焦点，ShowActivated=False 兜住）。</summary>
    public void ShowCollapsedSliver()
    {
        if (_closed) return;
        if (!IsVisible)
        {
            Show();   // ShowActivated=False：不激活、不抢前台；构造期已把 Left 挪到屏外，无闪烁
            Place(expanded: false);
            _shownAtLeastOnce = true;
            StartPoll();
        }
        else
        {
            Place(expanded: false);
        }
    }

    /// <summary>全屏让位开关（服务在对账里驱动）：让位 = 整窗隐藏 + 弹层隐藏 + 注销全部缩略句柄。</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            _collapseTimer?.Stop();
            _outsideSinceMs = null;
            _thumbs?.Clear();
            _thumbHost?.Hide();
            Hide();
            Logger.Info("台前调度：前台为真全屏，舞台条让位");
        }
        else
        {
            _expanded = false;
            ShowCollapsedSliver();
        }
    }

    /// <summary>服务的销毁入口（幂等）：绕开收起定时器直接关窗。</summary>
    public void ForceClose()
    {
        if (_closed) return;
        _collapseTimer?.Stop();
        _pollTimer?.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _collapseTimer?.Stop();
        _collapseTimer = null;
        _pollTimer?.Stop();
        _pollTimer = null;
        _thumbs?.Dispose();     // 全数注销缩略图句柄（不变量：注册次数 == 注销次数）
        _thumbs = null;
        _thumbHost?.Dispose();  // 弹层窗口销毁（无缩略句柄、无 GDI 资源，随窗即净）
        _thumbHost = null;
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = WindowHandle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            // 实况缩略图的玻璃弹层：owner=本窗口（恒浮于其上），显隐由本类显式管理。
            _thumbHost = new StageThumbHostWindow(hwnd, HandleThumbClick);
            _thumbs = new StageThumbStrip(_thumbHost.Handle);
        }
        catch (Exception ex)
        {
            try { _thumbHost?.Dispose(); } catch { /* 降级路径：Dispose 失败不掩盖原始异常 */ }
            _thumbHost = null;
            _thumbs = null;
            Logger.Warn($"台前调度：缩略图弹层装配失败（卡片退化为占位底色）: {ex.Message}");
        }
    }

    // ---------- 自动隐藏（光标轮询） ----------

    private void StartPoll()
    {
        if (_pollTimer is not null) return;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += (_, _) => PollCursor();
        _pollTimer.Start();
    }

    /// <summary>轮询判据：收起态只判贴边小条带（展开入口）；展开态判（窗口 ∪ 弹层）矩形（收回出口）。</summary>
    private void PollCursor()
    {
        if (_closed || _suppressed || !_shownAtLeastOnce) return;
        if (!GetCursorPos(out var cursor)) return;
        var (x, y) = (cursor.X, cursor.Y);

        if (!_expanded)
        {
            if (CollapsedSliverContains(x, y)) Expand();
            return;
        }

        if (WindowRectContains(WindowHandle, x, y) || _thumbHost?.ScreenRectContains(x, y) == true)
        {
            _outsideSinceMs = null;   // 还在条上（含缩略弹层）：复位离开计时
            return;
        }
        var now = Environment.TickCount64;
        _outsideSinceMs ??= now;
        if (now - _outsideSinceMs.Value >= MouseLeaveCollapseMs)
        {
            _outsideSinceMs = null;
            Collapse();
        }
    }

    /// <summary>收起态的展开入口：光标落在贴边小条带（窗口矩形最右 <see cref="CollapsedSliverDip"/>）。</summary>
    private bool CollapsedSliverContains(int x, int y)
    {
        if (!GetWindowRect(WindowHandle, out var r)) return false;
        var sliverPx = StripMath.ToPhysical(CollapsedSliverDip, WindowDpiScale());
        return x >= r.Right - sliverPx && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    private static bool WindowRectContains(IntPtr hwnd, int x, int y)
        => hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r)
           && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // ---------- 快照入口（服务调用） ----------

    /// <summary>
    /// 新分组快照 → 卡片。可见卡数按工作区高度再夹一道；收起态只换数据不贴缩略
    /// （下次展开统一 Sync）。卡片序列与上拍完全一致时不重设 ItemsSource——2s 一拍的对账
    /// 不应让展开中的条整列重建（悬停态闪断、缩略矩形无谓重贴）。
    /// </summary>
    public void UpdateGroups(IReadOnlyList<StageGroup> groups)
    {
        if (_closed || _suppressed) return;

        var maxByHeight = MaxCardsByWorkArea();
        var visible = Math.Min(groups.Count, maxByHeight);

        var items = new List<StageGroupItem>(visible);
        for (int i = 0; i < visible; i++)
            items.Add(new StageGroupItem(groups[i], _service.GetIcon(groups[i].AppId)));

        // SyncThumbs 的数据源（与 ItemsSource 同序同长；无论是否重绑都指向最新一拍）。
        _items = items;

        // 卡片集合无变化就不重绑：签名 = 逐卡（句柄, 是否当前, 计数标签）。
        var signature = string.Join('|', items.ConvertAll(c => $"{c.Handle.ToInt64():X},{c.IsCurrent},{c.CountLabel}"));
        if (signature != _lastItemsSignature)
        {
            _lastItemsSignature = signature;
            Cards.ItemsSource = items;
        }

        // 高度先于布局定型：窗口高 = 上下内边距 + 可见卡数×槽高（无滚动，见类注释）。
        var newHeight = StripVerticalPadDip + visible * CardSlotHeightDip;
        if (Math.Abs(Height - newHeight) > 0.5)
        {
            Height = newHeight;
            Place(_expanded);   // 展开态重贴位置/尺寸；收起态小条高度也跟上
        }
        if (_expanded)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncThumbs);
    }

    /// <summary>上拍卡片集合签名（去抖：对账不改变内容时不重建视觉树）。</summary>
    private string? _lastItemsSignature;

    /// <summary>当前卡片集合（与 ItemsSource 同序同长；SyncThumbs 的数据源）。</summary>
    private List<StageGroupItem> _items = new();

    // ---------- 展开 / 收起 ----------

    private void Expand()
    {
        if (_closed || _suppressed || _expanded) return;
        if (_items.Count == 0) return;   // 没有可显示的窗口：小条保留，展开无意义
        _expanded = true;
        _collapseTimer?.Stop();
        if (!IsVisible) Show();
        Place(expanded: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncThumbs);
    }

    private void Collapse()
    {
        if (_closed || !_expanded) return;
        _expanded = false;
        _collapseTimer?.Stop();
        _outsideSinceMs = null;
        _thumbs?.Clear();        // 贴边小条上没有缩略矩形：先注销再收（不留悬空句柄）
        _thumbHost?.Hide();
        Place(expanded: false);
    }

    private void ScheduleCollapse(int delayMs)
    {
        if (_closed || !_expanded) return;
        if (_collapseTimer is null)
        {
            _collapseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _collapseTimer.Tick += CollapseTimer_Tick;   // 订阅只在创建时挂一次（重复订阅会连环收回）
        }
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer?.Stop();
        Collapse();
    }

    // ---------- 定位（SetWindowPos 物理像素，与 VolumeOsdWindow 同一手法） ----------

    /// <summary>按光标所在显示器把条贴到左缘：<paramref name="expanded"/> 决定滑出还是只露小条。</summary>
    private void Place(bool expanded)
    {
        var area = GetCursorPos(out var cursor)
            ? MonitorGeometry.AreaForPoint(cursor.X, cursor.Y)
            : MonitorGeometry.AreaForPoint(1, 1);
        if (area is null) return;

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var widthPx = StripMath.ToPhysical(CardWidthDip, scale);
        var sliverPx = StripMath.ToPhysical(CollapsedSliverDip, scale);
        var insetPx = StripMath.ToPhysical(StripTopInsetDip, scale);
        var heightPx = StripMath.ToPhysical(Height, scale);

        var hwnd = WindowHandle;
        if (hwnd == IntPtr.Zero) return;

        // 工作区内再夹一道：高度不超过工作区（UpdateGroups 已按 DIP 夹过，这里是防御性兜底）。
        var maxHPx = monitor.Work.Height - 2 * insetPx;
        if (heightPx > maxHPx) heightPx = Math.Max(1, maxHPx);

        var leftPx = expanded
            ? monitor.Work.Left
            : monitor.Work.Left - widthPx + sliverPx;   // 收起：滑到屏外只留贴边小条
        var topPx = monitor.Work.Top + insetPx;

        // SWP_SHOWWINDOW 只在窗口本就该可见时带上：对被隐藏的窗口（全屏让位中）落位
        // 不能顺手把它点亮——显隐只由 SetSuppressed/ShowCollapsedSliver 管。
        var flags = SwpNoactivate | (IsVisible ? SwpShowWindow : 0u);
        SetWindowPos(hwnd, HwndTopmost, leftPx, topPx, widthPx, heightPx, flags);
        if (!expanded) _thumbHost?.Hide();   // 收起态弹层必藏（展开时 SyncThumbs 重新定位并显出）
    }

    /// <summary>按当前光标所在屏的工作区算一次可见卡上限（放不下就少显示，不做滚动）。</summary>
    private int MaxCardsByWorkArea()
    {
        var area = GetCursorPos(out var cursor)
            ? MonitorGeometry.AreaForPoint(cursor.X, cursor.Y)
            : MonitorGeometry.AreaForPoint(1, 1);
        var workHDip = area is { } m && m.DpiScale > 0
            ? StripMath.ToDip(m.Work.Height, m.DpiScale)
            : SystemParameters.WorkArea.Height;
        var available = workHDip - StripVerticalPadDip - 2 * StripTopInsetDip;
        if (available <= 0) return 0;
        return Math.Max(1, (int)(available / CardSlotHeightDip));
    }

    // ---------- DWM 实况缩略图（玻璃弹层） ----------

    /// <summary>
    /// 量测各卡 <c>ThumbArea</c> 的实际布局矩形（窗口客户区 DIP）→ 取并集定位玻璃弹层 →
    /// 把每张卡片矩形换算成弹层客户区物理像素交给登记表与命中表对账（展开态调用）。
    /// </summary>
    private void SyncThumbs()
    {
        if (_thumbs is null || _thumbHost is null || _closed || _suppressed || !_expanded) return;

        // 1) 量测：卡片矩形（窗口客户区、DIP）。
        var cardRects = new List<(IntPtr Source, Rect ClientDip)>();
        for (int i = 0; i < _items.Count; i++)
        {
            var group = _items[i];
            if (group.Handle == IntPtr.Zero) continue;   // 槽位仍推进（下标 i 与容器一一对应）
            if (Cards.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter)
                continue;
            if (presenter.ContentTemplate?.FindName("ThumbArea", presenter) is not Border thumbBorder)
                continue;

            var topLeft = thumbBorder.TransformToAncestor(this).Transform(new Point(0, 0));
            if (thumbBorder.ActualWidth < 4 || thumbBorder.ActualHeight < 4)
                continue;   // 还没完成布局：等下一次 Sync
            cardRects.Add((group.Handle, new Rect(topLeft.X, topLeft.Y,
                thumbBorder.ActualWidth, thumbBorder.ActualHeight)));
        }
        if (cardRects.Count == 0)
        {
            _thumbHost.Hide();
            _thumbs.Clear();
            return;
        }

        // 2) 并集 → 弹层屏幕物理矩形（窗口物理原点 + 并集偏移）。
        var bbox = cardRects[0].ClientDip;
        foreach (var (_, rect) in cardRects) bbox.Union(rect);
        var dpi = WindowDpiScale();
        if (!GetWindowRect(WindowHandle, out var barRect))
        {
            _thumbHost.Hide();
            return;
        }
        var hostRect = new Rect(
            barRect.Left + bbox.X * dpi,
            barRect.Top + bbox.Y * dpi,
            bbox.Width * dpi,
            bbox.Height * dpi);
        _thumbHost.Place(hostRect);

        // 3) 卡片矩形 → 弹层客户区物理像素（同一份表喂 DWM 注册与点击命中）。
        var hostRects = new List<(IntPtr Source, Rect ClientPx)>(cardRects.Count);
        foreach (var (source, rect) in cardRects)
        {
            hostRects.Add((source, new Rect(
                (rect.X - bbox.X) * dpi,
                (rect.Y - bbox.Y) * dpi,
                rect.Width * dpi,
                rect.Height * dpi)));
        }
        _thumbHost.SetHitRects(hostRects);

        var ok = _thumbs.Sync(hostRects);
        // 只在"数量变化 / 有失败 / 失败恢复"时记日志：展开期间 2s 一拍，逐拍记会刷满日志。
        var snapshot = (hostRects.Count, ok, _thumbs.RegisteredCount);
        if (_lastThumbLog != snapshot)
        {
            _lastThumbLog = snapshot;
            Logger.Info($"台前调度：缩略图对账 期望 {snapshot.Item1} 张，注册 {snapshot.Item3} 张" +
                        $"{(ok ? string.Empty : "（部分失败，卡片退化为占位底色）")}");
        }
    }

    /// <summary>上次缩略图对账日志的快照（去抖：只在变化时记）。</summary>
    private (int Count, bool Ok, int Registered)? _lastThumbLog;

    private double WindowDpiScale()
    {
        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        return matrix is { M11: > 0 } ? matrix.Value.M11 : 1.0;
    }

    // ---------- 交互 ----------

    private void GroupCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: StageGroupItem group }) return;
        _service.SwitchTo(group.Handle);
        // 切换成功前台会移走 → Deactivated 收起接住；这里再排一道，防前台切换被系统压制时
        // 条常驻不收（光标轮询是第三道兜底）。
        ScheduleCollapse(DeactivatedCollapseMs);
    }

    /// <summary>缩略弹层上点击了某张实况缩略图（玻璃弹层转发回来）。</summary>
    private void HandleThumbClick(IntPtr sourceHwnd)
    {
        if (_closed) return;
        _service.SwitchTo(sourceHwnd);
        ScheduleCollapse(DeactivatedCollapseMs);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc 收起（诚实边界见类注释：窗口持有焦点时才收得到——这是"点过条"之后的退出路，
        // 不是全局热键）。
        if (e.Key == Key.Escape && _expanded)
        {
            Collapse();
            e.Handled = true;
        }
    }

    /// <summary>舞台条卡片的数据模型（快照 → UI 的唯一映射；图标由服务缓存供给）。</summary>
    internal sealed class StageGroupItem
    {
        public StageGroupItem(StageGroup group, BitmapSource? icon)
        {
            ArgumentNullException.ThrowIfNull(group);
            Handle = group.ThumbnailHandle;
            AppId = group.AppId;
            AppName = group.AppName;
            IsCurrent = group.IsCurrent;
            WindowCount = group.Windows.Count;
            Icon = icon;
            CountLabel = group.Windows.Count > 1
                ? $"{group.AppName}（{group.Windows.Count} 个窗口）"
                : group.AppName;
        }

        public IntPtr Handle { get; }        // 卡片缩略候选窗口（点击切换的目标）
        public string AppId { get; }
        public string AppName { get; }
        public bool IsCurrent { get; }
        public int WindowCount { get; }
        public BitmapSource? Icon { get; }
        public bool HasIcon => Icon is not null;
        public string CountLabel { get; }
    }
}
