using System;
using System.Collections.Generic;
using System.ComponentModel;
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
/// 台前调度·左侧舞台条窗口（wp-macos-parity r6）：每块显示器一条，贴<strong>自己那块屏</strong>的
/// 左缘常驻（归属屏在构造时定死，落位按屏句柄重取几何），收起态只露
/// <see cref="CollapsedSliverDip"/> 的贴边小条，鼠标靠近整条<strong>渐进滑出</strong>（时长/缓动走
/// 主题动效 token <c>MacDockSlideMs</c>/<c>MacEaseOut</c>，可在设置里关回瞬时两态），离开/失焦/
/// 点击卡片切换成功后延时收回；Esc 全局收起（见下）。
/// <para>
/// 【宿主形态（真机取证裁定，勿改回）】本窗口是 WPF <b>分层</b>窗口（AllowsTransparency=True，
/// 与 Dock/顶栏/切换器同款路径）——Finder/QuickLookWindow.xaml 头注释实测：这台真机
/// （Win11 26300）上 WPF <b>非分层</b>窗口整机黑屏，只有分层窗口出画面。而 DWM 实况缩略图
/// 不能以分层窗口为目标（ThumbnailPreview 头注释），r6 曾改走独立玻璃弹层
/// （StageThumbHostWindow，owner=本窗口）——round5 真机验收实拍该通道在 26300 上
/// <b>注册/写属性 HRESULT 全 0 却不合成像素</b>（缩略矩形纯黑，验收日志无一条注册失败；
/// round2 在 24H2 上记录过同一失效模式）。卡片画面遂改走 <see cref="StageFramePump"/> 静帧链
/// （PrintWindow 抓帧 → 行模型 Frame → WPF ThumbArea 里的 Image，StillPreviewWindow 同一条
/// 被真机验证过出画面的保底路），卡片点击全部回到 WPF 自身：ThumbArea 现在承载 WPF Image，
/// 抓到帧画帧，没抓到露占位底色，绝不黑框。
/// </para>
/// <para>
/// 【布局数学（DIP）】卡片宽 <see cref="CardWidthDip"/>；每卡 = 应用行 28 + 缩略区 100 + 卡内
/// 收尾 7 + 描边 1.5×2 + 下边距 8 = 146；窗口高 = 上下内边距 16 + n×146，再夹到所在屏工作区。
/// 一次可见卡数 = <see cref="StageGrouper.DefaultMaxGroups"/> 与工作区高度的较小者（超出按
/// MRU 丢弃，不做滚动）。
/// </para>
/// <para>
/// 【滑动动画（r6）】Expand/Collapse 不再瞬时两态：起止 X（物理 px）之间按
/// <see cref="StageSlideMotion"/>（Core 纯函数，cubic-bezier 求值）逐帧 SetWindowPos 插值，
/// 静帧泵在展开期按 1s 节拍刷新画面（收起即停）。动画可被反向打断（滑出途中收回 = 从当前位置
/// 折返）；动画期间光标轮询不再触发"贴边展开"，
/// 防止"收起滑行扫过光标 → 反复展开"的自激振荡。几何在 <see cref="Place"/> 单点收口，
/// 动画只是让其中的 X 按 elapsed 插值——自家分层窗口，动画安全。
/// </para>
/// <para>
/// 【自动隐藏（光标轮询，不用 WPF 鼠标事件）】缩略弹层与二级弹层都是独立 HWND，光标移到
/// 它们上面时 WPF 的 MouseLeave 会误报"离开了条"；轮询 <c>GetCursorPos</c> 对
/// （本窗口 ∪ 缩略弹层 ∪ 二级弹层）屏幕矩形做几何判定（100ms 一拍，展开态才做并集判定，
/// 收起态只判贴边小条带），对 airspace 与 z 序免疫。失焦（含点击卡片切换成功）另有
/// <c>Deactivated</c> 延时收起接住。
/// </para>
/// <para>
/// 【Esc 的收起路径（r6 重定）】主路是<b>全局</b>的：服务在任一条展开（或二级弹层在场）时挂一条
/// WH_KEYBOARD_LL（<see cref="LowLevelKeyboardHook"/>，收起即卸；回调只认 Esc 且<b>永远放行</b>
/// 不吞键——Esc 同时属于前台应用，我们只搭车收条），"悬停展开→焦点还在原应用"时按 Esc 也能收。
/// 本窗口自己的 OnPreviewKeyDown 保留为兜底（钩子装配失败/被系统摘掉时，点过条的场景仍可收）。
/// 与 AltTabHook 的会话内 Esc 不冲突：那条钩子吞它的键，这条放行，链式各判各的。
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
    private const int AnimFrameMs = 16;             // 滑动动画的帧步长（~60fps）
    private const int FallbackSlideMs = 260;        // token 缺席时的时长回落值 = MacTokens.DockSlideMs

    private readonly StageManagerService _service;
    private readonly IntPtr _monitor;               // 归属屏（构造定死；落位/卡数上限都按它算）
    private StageFramePump? _frames;                // 卡片静帧泵（展开期跑；round5 缺陷 4 后替代 DWM 实况通道）
    private StageGroupFlyoutWindow? _flyout;        // 二级弹层：同应用多窗的窗口列（r6）
    private string? _flyoutGroupId;                 // 当前弹层对应的组（AppId；快照里消失即收）
    private DispatcherTimer? _collapseTimer;
    private DispatcherTimer? _pollTimer;            // 自动隐藏的光标轮询（首次显示后常开，代价 100ms 一查）
    private DispatcherTimer? _animTimer;            // 滑动动画帧驱动（动画期间才跑）
    private long? _outsideSinceMs;                  // 光标连续离开整条的起点（TickCount64 ms）
    private bool _expanded;
    private bool _suppressed;                       // 真全屏让位：整窗退场（含贴边小条与弹层）
    private bool _shownAtLeastOnce;
    private bool _closed;

    // 滑动动画的运行态（_animating 期间 Place/ApplyFrame 的 X 一律取插值）。
    private bool _animating;
    private double _animFromPx, _animToPx;
    private long _animStartMs;
    private int _animDurationMs;
    private StageEase _animEase = StageEase.Default;

    public StageBarWindow(StageManagerService service, IntPtr monitor)
    {
        InitializeComponent();
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _monitor = monitor;
        // 首帧防闪烁：Show() 创建 hwnd 前先把自己挪到屏外，Place() 随后用 SetWindowPos 精确落位
        //（WPF 默认 CenterScreen 会让小条在屏幕中央闪一帧，绝不允许）。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000;
        Deactivated += (_, _) => ScheduleCollapse(DeactivatedCollapseMs);
    }

    /// <summary>宿主窗口句柄（服务做全屏让位判定用；未初始化返回 Zero）。</summary>
    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    /// <summary>归属显示器句柄（诊断用）。</summary>
    public IntPtr Monitor => _monitor;

    /// <summary>是否处于全屏让位态（诊断用）。</summary>
    public bool IsSuppressed => _suppressed;

    /// <summary>是否处于展开态（诊断 / 服务 Esc 钩子武装判定用）。</summary>
    public bool IsExpanded => _expanded;

    /// <summary>二级弹层是否在场（服务的 Esc 钩子武装判定用）。</summary>
    public bool IsFlyoutOpen => _flyout is { IsOpen: true };

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

    /// <summary>全屏让位开关（服务在对账里按屏驱动）：让位 = 整窗隐藏 + 两层弹层隐藏 + 注销全部缩略句柄。</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            StopSlide();
            _collapseTimer?.Stop();
            _outsideSinceMs = null;
            CloseFlyout();
            _frames?.Stop();
            Hide();
            Logger.Info("台前调度：前台为真全屏，舞台条让位");
        }
        else
        {
            _expanded = false;
            ShowCollapsedSliver();
        }
        _service.NotifyExpansionChanged();
    }

    /// <summary>服务的销毁入口（幂等）：绕开收起定时器直接关窗。</summary>
    public void ForceClose()
    {
        if (_closed) return;
        StopSlide();
        _collapseTimer?.Stop();
        _pollTimer?.Stop();
        _flyout?.Dispose();
        _flyout = null;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        StopSlide();
        _collapseTimer?.Stop();
        _collapseTimer = null;
        _pollTimer?.Stop();
        _pollTimer = null;
        _frames?.Dispose();    // 静帧泵停拍销毁（进行中的一拍自然收尾，迟到帧落旧实例无害）
        _frames = null;
        _flyout?.Dispose();
        _flyout = null;
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 静帧泵惰性建（展开首拍才需要；见 FrameTargets）。
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

    /// <summary>轮询判据：收起态只判贴边小条带（展开入口；动画期间不触发，防自激振荡）；
    /// 展开态判（窗口 ∪ 缩略弹层 ∪ 二级弹层）矩形（收回出口）。</summary>
    private void PollCursor()
    {
        if (_closed || _suppressed || !_shownAtLeastOnce) return;
        if (!GetCursorPos(out var cursor)) return;
        var (x, y) = (cursor.X, cursor.Y);

        if (!_expanded)
        {
            if (!_animating && CollapsedSliverContains(x, y)) Expand();
            return;
        }

        if (WindowRectContains(WindowHandle, x, y)
            || _flyout?.ScreenRectContains(x, y) == true)
        {
            _outsideSinceMs = null;   // 还在条上（含两层弹层）：复位离开计时
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
    /// 本屏分组快照 → 卡片。可见卡数按<strong>归属屏</strong>工作区高度再夹一道；收起态只换数据不贴缩略
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

        // FrameTargets 的数据源（与 ItemsSource 同序同长；无论是否重绑都指向最新一拍）。
        _items = items;

        // 二级弹层的组在快照里消失（窗口全关/换屏）：立即收掉，别留一份指向空组的僵尸列。
        if (_flyoutGroupId is not null)
        {
            var found = false;
            foreach (var item in items)
                if (item.AppId == _flyoutGroupId) { found = true; break; }
            if (!found) CloseFlyout();
        }

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
            EnsureFramePump().Start();   // 展开期重开一拍：新卡片/新句柄立刻拿到首帧
    }

    /// <summary>上拍卡片集合签名（去抖：对账不改变内容时不重建视觉树）。</summary>
    private string? _lastItemsSignature;

    /// <summary>当前卡片集合（与 ItemsSource 同序同长；FrameTargets 的数据源）。</summary>
    private List<StageGroupItem> _items = new();

    // ---------- 展开 / 收起（r6：渐进滑动，可反向打断） ----------

    private void Expand()
    {
        if (_closed || _suppressed || _expanded) return;
        if (_items.Count == 0) return;   // 没有可显示的窗口：小条保留，展开无意义
        _expanded = true;
        _collapseTimer?.Stop();
        if (!IsVisible) Show();
        BeginSlide(toExpanded: true);
        EnsureFramePump().Start();   // 展开首拍：卡片画面立刻到位，不等第一个节拍
        _service.NotifyExpansionChanged();
    }

    private void Collapse()
    {
        if (_closed || !_expanded) return;
        _expanded = false;
        _collapseTimer?.Stop();
        _outsideSinceMs = null;
        CloseFlyout();
        // 静帧泵的停拍挪到滑动完成（EndSlide）：滑行途中画面仍在刷新，收完才退场。
        BeginSlide(toExpanded: false);
        _service.NotifyExpansionChanged();
    }

    /// <summary>服务的全局 Esc 入口：无条件收起（未展开为 no-op；与本地路径同一收口）。</summary>
    public void CollapseFromEsc() => Collapse();

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

    // ---------- 滑动动画 ----------

    /// <summary>
    /// 启动滑动（或瞬时落位）：设置项关、起止相同、几何取不到 → 直接 <see cref="EndSlide"/> 收口；
    /// 否则从当前 X（含动画中途，反向打断 = 从插值处折返）向目标态插值。
    /// </summary>
    private void BeginSlide(bool toExpanded)
    {
        var area = MonitorGeometry.AreaForMonitor(_monitor);
        var hwnd = WindowHandle;
        if (area is null || hwnd == IntPtr.Zero)
        {
            // 拔屏瞬间/句柄未就绪：无几何可谈，保持现状，下一拍对账回来。
            return;
        }
        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var widthPx = StripMath.ToPhysical(CardWidthDip, scale);
        var sliverPx = StripMath.ToPhysical(CollapsedSliverDip, scale);
        var toPx = TargetLeftPx(toExpanded, monitor, widthPx, sliverPx);
        var fromPx = GetWindowRect(hwnd, out var r) ? (double)r.Left : toPx;

        var animate = SettingsService.Current.StageManagerSlideAnimation && fromPx != toPx;
        if (!animate)
        {
            _animating = false;
            ApplyFrame(toPx);
            EndSlide();
            return;
        }
        var (durationMs, ease) = SlideMotionOf();
        _animFromPx = fromPx;
        _animToPx = toPx;
        _animStartMs = Environment.TickCount64;
        _animDurationMs = durationMs;
        _animEase = ease;
        _animating = true;
        if (_animTimer is null)
        {
            _animTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(AnimFrameMs)
            };
            _animTimer.Tick += (_, _) => AnimFrame();
        }
        _animTimer.Stop();
        _animTimer.Start();
        ApplyFrame(CurrentAnimLeftPx());   // 首帧立即贴上，不等第一拍（否则起止间闪一帧两态）
    }

    /// <summary>动画时长/缓动走主题动效 token（MacDockSlideMs / MacEaseOut）；键缺席回落定稿值。</summary>
    private (int DurationMs, StageEase Ease) SlideMotionOf()
    {
        var ms = TryFindResource("MacDockSlideMs") as int? ?? FallbackSlideMs;
        if (ms <= 0) ms = FallbackSlideMs;   // token 异常不产生除零/永动（Progress 侧另有兜底）
        if (!StageEase.TryParseCurve(TryFindResource("MacEaseOut") as string, out var ease))
            ease = StageEase.Default;        // = token EaseOut 的定稿值
        return (ms, ease);
    }

    /// <summary>当前插值 X（物理 px）：elapsed → 归一进度（缓动）→ 起止线性插值。</summary>
    private double CurrentAnimLeftPx()
    {
        var elapsed = (int)(Environment.TickCount64 - _animStartMs);
        var progress = StageSlideMotion.Progress(elapsed, _animDurationMs, _animEase);
        return StageSlideMotion.Interpolate(_animFromPx, _animToPx, progress);
    }

    private void AnimFrame()
    {
        if (!_animating) { _animTimer?.Stop(); return; }
        var elapsed = (int)(Environment.TickCount64 - _animStartMs);
        var done = elapsed >= _animDurationMs;
        ApplyFrame(done ? _animToPx : CurrentAnimLeftPx());
        if (done) EndSlide();
    }

    private void EndSlide()
    {
        _animating = false;
        _animTimer?.Stop();
        if (!_expanded)
        {
            // 收起完成：贴边小条上没有缩略矩形，此刻才停拍（滑行途中画面仍刷新）。
            _frames?.Stop();
        }
    }

    /// <summary>无动画的强制停摆（让位/关窗）：动画态复位，缩略注销交由调用方语义决定。</summary>
    private void StopSlide()
    {
        _animating = false;
        _animTimer?.Stop();
    }

    // ---------- 定位（SetWindowPos 物理像素，与 VolumeOsdWindow 同一手法） ----------

    /// <summary>把条贴到<strong>归属屏</strong>左缘：<paramref name="expanded"/> 决定滑出还是只露小条。
    /// 动画期间 X 取当前插值（外部重贴只更新高/顶，不打断滑行）。</summary>
    private void Place(bool expanded)
    {
        var area = MonitorGeometry.AreaForMonitor(_monitor);
        if (area is null) return;   // 归属屏刚被拔出：跳过本次定位、保持上次几何（下一拍对账回来）

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var widthPx = StripMath.ToPhysical(CardWidthDip, scale);
        var sliverPx = StripMath.ToPhysical(CollapsedSliverDip, scale);
        var insetPx = StripMath.ToPhysical(StripTopInsetDip, scale);
        var heightPx = StripMath.ToPhysical(Height, scale);
        var leftPx = _animating ? CurrentAnimLeftPx() : TargetLeftPx(expanded, monitor, widthPx, sliverPx);
        ApplyGeometry(leftPx, monitor, insetPx, widthPx, heightPx);
    }

    /// <summary>目标 X：展开 = 工作区左缘；收起 = 滑到屏外只留贴边小条。</summary>
    private static double TargetLeftPx(bool expanded, MonitorArea monitor, double widthPx, double sliverPx)
        => expanded ? monitor.Work.Left : monitor.Work.Left - widthPx + sliverPx;

    /// <summary>按给定 X 落位（顶/宽/高按归属屏现算），并让缩略玻璃弹层跟随平移。</summary>
    private void ApplyFrame(double leftPx)
    {
        var area = MonitorGeometry.AreaForMonitor(_monitor);
        if (area is null) return;
        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var insetPx = StripMath.ToPhysical(StripTopInsetDip, scale);
        var widthPx = StripMath.ToPhysical(CardWidthDip, scale);
        var heightPx = StripMath.ToPhysical(Height, scale);

        // 工作区内再夹一道：高度不超过工作区（UpdateGroups 已按 DIP 夹过，这里是防御性兜底）。
        var maxHPx = monitor.Work.Height - 2 * insetPx;
        if (heightPx > maxHPx) heightPx = Math.Max(1, maxHPx);

        ApplyGeometry(leftPx, monitor, insetPx, widthPx, heightPx);
    }

    /// <summary>SetWindowPos 收口（X 由调用方决定，其余按归属屏现算）。</summary>
    private void ApplyGeometry(double leftPx, MonitorArea monitor, double insetPx, double widthPx, double heightPx)
    {
        var hwnd = WindowHandle;
        if (hwnd == IntPtr.Zero) return;
        var topPx = monitor.Work.Top + insetPx;

        // SWP_SHOWWINDOW 只在窗口本就该可见时带上：对被隐藏的窗口（全屏让位中）落位
        // 不能顺手把它点亮——显隐只由 SetSuppressed/ShowCollapsedSliver 管。
        var flags = SwpNoactivate | (IsVisible ? SwpShowWindow : 0u);
        SetWindowPos(hwnd, HwndTopmost, (int)Math.Round(leftPx), (int)Math.Round(topPx),
            (int)Math.Round(widthPx), (int)Math.Round(heightPx), flags);
    }

    /// <summary>按归属屏工作区算一次可见卡上限（放不下就少显示，不做滚动）。</summary>
    private int MaxCardsByWorkArea()
    {
        var area = MonitorGeometry.AreaForMonitor(_monitor);
        var workHDip = area is { } m && m.DpiScale > 0
            ? StripMath.ToDip(m.Work.Height, m.DpiScale)
            : SystemParameters.WorkArea.Height;
        var available = workHDip - StripVerticalPadDip - 2 * StripTopInsetDip;
        if (available <= 0) return 0;
        return Math.Max(1, (int)(available / CardSlotHeightDip));
    }

    // ---------- DWM 实况缩略图（玻璃弹层） ----------

    /// <summary>
    /// 静帧泵惰性建（展开首拍才需要）。目标集每拍现场取 <see cref="FrameTargets"/>——
    /// 卡片集合随快照对账重建，泵绝不持有过期行。
    /// </summary>
    private StageFramePump EnsureFramePump()
        => _frames ??= new StageFramePump(Dispatcher, TimeSpan.FromSeconds(1), FrameTargets);

    /// <summary>
    /// 本拍抓帧目标：每张卡片（组缩略候选窗口句柄 → <see cref="StageGroupItem.Frame"/>）。
    /// 句柄失效/为零的槽位跳过；apply 闭包绑抓帧时刻的实例，迟到帧落旧实例自然丢弃。
    /// </summary>
    private IReadOnlyList<(IntPtr Hwnd, Action<BitmapSource?> Apply)> FrameTargets()
    {
        var list = new List<(IntPtr, Action<BitmapSource?>)>(_items.Count);
        foreach (var group in _items)
        {
            var item = group;
            if (item.Handle != IntPtr.Zero)
                list.Add((item.Handle, frame => item.Frame = frame));
        }
        return list;
    }

    private double WindowDpiScale()
    {
        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        return matrix is { M11: > 0 } ? matrix.Value.M11 : 1.0;
    }

    // ---------- 二级弹层（同应用多窗，r6） ----------

    /// <summary>
    /// 点击多窗卡片 → 展开/收起该组的窗口列弹层（再点同一组 = 收起；点别的组 = 换列）。
    /// 单窗卡片不走这里：直接切换（与 alpha.28 行为一致）。
    /// </summary>
    private void ToggleFlyout(StageGroupItem group, Border? card)
    {
        if (_closed || _suppressed || !_expanded) return;
        if (_flyout is { IsOpen: true })
        {
            var same = _flyout.GroupId == group.AppId;
            CloseFlyout();
            if (same) return;
        }
        OpenFlyout(group, card);
    }

    private void OpenFlyout(StageGroupItem group, Border? card)
    {
        var area = MonitorGeometry.AreaForMonitor(_monitor);
        if (area is null) return;
        if (_flyout is null)
        {
            try { _flyout = new StageGroupFlyoutWindow(_service); }
            catch (Exception ex)
            {
                _flyout = null;
                Logger.Warn($"台前调度：二级弹层装配失败（本组退化为单代表窗点击）: {ex.Message}");
                return;
            }
            _flyout.SwitchedToWindow += () => ScheduleCollapse(DeactivatedCollapseMs);
        }

        var monitor = area.Value;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        // 行数按归属屏工作区高度再夹一道（弹层不做滚动；超出按 MRU 丢弃）。
        var workHDip = StripMath.ToDip(monitor.Work.Height, scale);
        var maxRows = (int)((workHDip - 2 * StageFlyoutPlan.EdgeMarginDip - StageGroupFlyoutWindow.PadVerticalDip)
                            / StageGroupFlyoutWindow.RowSlotHeightDip);
        maxRows = Math.Clamp(maxRows, 1, StageGroupFlyoutWindow.MaxRows);
        var rows = StageGroupFlyoutWindow.BuildRows(group, maxRows, _service.GetIcon(group.AppId));
        if (rows.Count == 0) return;

        // 落位：条展开右缘 + 间隙，纵向对齐被点卡片顶（Core 纯函数钳制进工作区）。
        var workDipLeft = StripMath.ToDip(monitor.Work.Left, scale);
        double anchorTopDip = StripTopInsetDip;
        if (card is not null)
        {
            var topLeft = card.TransformToAncestor(this).Transform(new Point(0, 0));
            anchorTopDip += topLeft.Y;
        }
        var heightDip = StageGroupFlyoutWindow.PadVerticalDip + rows.Count * StageGroupFlyoutWindow.RowSlotHeightDip;
        var placement = StageFlyoutPlan.Place(
            monitor.Work, scale,
            workDipLeft + CardWidthDip, anchorTopDip,
            StageGroupFlyoutWindow.FlyoutWidthDip, heightDip);

        _flyoutGroupId = group.AppId;
        _flyout.Open(rows, group.AppId, new Rect(
            monitor.Work.Left + StripMath.ToPhysical(placement.LeftDip, scale),
            monitor.Work.Top + StripMath.ToPhysical(placement.TopDip, scale),
            StripMath.ToPhysical(StageGroupFlyoutWindow.FlyoutWidthDip, scale),
            StripMath.ToPhysical(heightDip, scale)));
        _service.NotifyExpansionChanged();
    }

    /// <summary>收起二级弹层（幂等；条收起/让位/关窗都会走到）。</summary>
    private void CloseFlyout()
    {
        if (_flyout is null) return;
        _flyout.CloseQuiet();
        _flyoutGroupId = null;
        _service.NotifyExpansionChanged();
    }

    // ---------- 交互 ----------

    private void GroupCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: StageGroupItem group }) return;
        if (group.WindowCount > 1)
        {
            // 同应用多窗（r6）：卡片点击 = 二级展开该组全部窗口，再点选具体窗口切换。
            ToggleFlyout(group, sender as Border);
            return;
        }
        _service.SwitchTo(group.Handle);
        // 切换成功前台会移走 → Deactivated 收起接住；这里再排一道，防前台切换被系统压制时
        // 条常驻不收（光标轮询是第三道兜底）。
        ScheduleCollapse(DeactivatedCollapseMs);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc 收起的兜底路（主路 = 服务的全局 LL 钩子，见类注释）：窗口持有焦点时才收得到。
        if (e.Key == Key.Escape && _expanded)
        {
            Collapse();
            e.Handled = true;
        }
    }

    /// <summary>舞台条卡片的数据模型（快照 → UI 的唯一映射；图标由服务缓存供给）。</summary>
    public sealed class StageGroupItem : INotifyPropertyChanged
    {
        public StageGroupItem(StageGroup group, BitmapSource? icon)
        {
            ArgumentNullException.ThrowIfNull(group);
            Handle = group.ThumbnailHandle;
            AppId = group.AppId;
            AppName = group.AppName;
            IsCurrent = group.IsCurrent;
            WindowCount = group.Windows.Count;
            Windows = group.Windows;
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
        public IReadOnlyList<StageWindow> Windows { get; }   // 二级弹层的数据源（MRU 序）
        public BitmapSource? Icon { get; }
        public bool HasIcon => Icon is not null;
        public string CountLabel { get; }

        private BitmapSource? _frame;

        /// <summary>
        /// 卡片缩略候选窗口的静帧（<see cref="StageFramePump"/> 抓取落地；null = 还没抓到，
        /// ThumbArea 露占位底色）。INPC：泵在后台抓帧、回 UI 线程落这一个属性，卡片画面原地更新。
        /// </summary>
        public BitmapSource? Frame
        {
            get => _frame;
            set
            {
                if (ReferenceEquals(_frame, value)) return;
                _frame = value;
                OnPropertyChanged(nameof(Frame));
                OnPropertyChanged(nameof(HasFrame));
            }
        }

        public bool HasFrame => Frame is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
