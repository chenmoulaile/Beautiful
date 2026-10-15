using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.MediaWidget;
using Beautiful.App.Preview;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Dock;
using Beautiful.Core.Layout;
using Beautiful.Core.Themes;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;   // 与 System.Windows.Forms.MouseEventArgs 歧义（UseWindowsForms）
using DragEventArgs = System.Windows.DragEventArgs;           // 拖放落位（v3 T5）：消歧 System.Windows.Forms.DragEventArgs
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace Beautiful.App.Dock;

/// <summary>
/// 底部居中悬浮 Dock：AppBar 注册（ABM_NEW → ABM_QUERYPOS → ABM_SETPOS，ABE_BOTTOM 全宽条带）
/// + macOS 式放大（Task 6 重写）：条目交给 <see cref="DockSurface"/> 自绘——连续高斯放大场
/// （真实物理光标坐标，非离散悬停索引）+ CompositionTarget.Rendering（vsync）驱动的指数阻尼，
/// 逐帧只写 Canvas.Left/Top 与 RenderTransform，面板内零 Margin/LayoutTransform 改写。
/// 条带几何（v3 T1）：<b>上报 shell 的预留带 = 可见面板高</b>，真实窗口矩形在其之上再多出
/// <see cref="StripHeadroomDip"/> 的透明带（Background={x:Null} 分层窗口不参与命中 → 点击穿透），
/// 放大后的图标才不被窗口上沿裁掉、而工作区只让出面板那么多。
/// 主题由 ThemeManager 驱动（Task 13，订阅时序见终审 I-6 注释）：OnSourceInitialized 显式 ApplyTheme(当前)
/// 起步并订阅 ThemeChanged 重设亚克力/间距/放大参数（IconSize、Spacing、MaxScale、ShowMagnification 写入 DockSurface）。
/// </summary>
public partial class DockWindow : AcrylicWindow
{
    // 面板可见高度与条带顶部透明余量随图标尺寸/主题几何/放大强度动态计算（见 VisiblePanelDip / StripHeadroomDip），
    // 不再是固定常量——图标尺寸是设置项（32..72），macOS 放大后图标会向上生长，窗口必须容得下。
    // 【v3 T4：竖向几何全部主题驱动】行内余量（DockSurface.RowInsetDip）与面板内边距取自 ThemeDefinition，
    // 于是"面板贴图标"成为原生各自的值而不是统一 20+12：IconSize=44 时 macOS 行 54 / 面板 66 DIP
    // （原 64 / 76；倒影显式开启时仍复原为 83.2 / 95.2，见 DockRowMath 推导）。
    // 算术本体在 Core（Dock.DockRowMath，单测覆盖），本窗口只负责"把主题参数喂进去 + 读结果"：
    // 条带预留 = 可见面板高、窗口 = 面板 + 放大余量、媒体面板锚定、点击穿透几何都读同一来源，
    // 不允许出现第二条算术路径（v3 §1.C 的裁定，T1 刚建立的不变量）。
    private const double MinStripHeadroomDip = 22;
    private const double SigmaInPitches = 2.0;  // 高斯 σ（单位 = 一个图标间距；控制器裁定：不随主题变化）
        // 尺度说明（评审发现：旧注释撒谎）：Core 的场是标准差形式 exp(-d²/(2σ²))，v1 的 Magnifier 是
        // exp(-(d/σ)²)，同写 2.0 时本实现的实际影响半径比 v1 宽 √2 倍（底板鼓包约宽 26%）。
        // 刻意保留 2.0：半径更宽 → 相邻项倍率差更小、斜率更缓，正是"放大别看起来粗糙"要的方向；
        // 若要 1:1 复刻 v1 视觉尺度，把本常量改成 2.0/√2 ≈ 1.4142 即可（DockSurface.Sigma 同一口径）。


    private readonly WindowTracker _tracker = new();
    private readonly DockViewModel _vm;
    // 组合根（本任务最简路径）：DockWindow 持有 SmtcService + MediaViewModel，
    // MediaViewModel 构造期自启动 SMTC；MediaPanel 惰性创建（胶囊点击时）。
    // App.OnStartup / DockViewModel 保持不动（媒体条目由 DockViewModel 的 suffix 占位提供）。
    private readonly SmtcService _smtc;
    private readonly MediaViewModel _mediaVm;
    private readonly TaskbarManager? _taskbar; // App 注入：TaskbarCreated（explorer 重建）后重放策略
    private MediaPanel? _mediaPanel;
    private DateTime? _panelAutoHiddenAtUtc; // 面板最近一次"失焦自动收起"时刻（胶囊点击的 toggle 语义用）
    // 图标尺寸/放大强度滑杆拖动时会连续触发排布刷新；条带重排（AppBar 协商 + SetWindowPos）成本较高，
    // 用一次性防抖把拖动期间的多次请求合并成落定后的一次重排（排布参数本身每帧即时写入，图标视觉跟手）。
    private DispatcherTimer? _repositionDebounce;
    // v3 T6：图标烘焙参数（尺寸/DPI/主题/方式）变更后要重挂条目绑定（=重建容器 = 重烘一排图标）。
    // 滑杆连续拖动时合并成一次，绝不让"每步重烘"进到帧预算里（P95 门）。
    private DispatcherTimer? _iconRebindDebounce;

    // ---------- 悬停实时预览（Task 13）----------
    // _preview 惰性创建（首次悬停到窗口条目），随 Dock 关闭 Dispose（内部注销缩略图句柄 + 销毁宿主窗口）。
    // _previewDwell 是 ~350ms 的一次性驻留计时器：悬停满 350ms 才弹预览，离开/点击即停。
    // _previewPending 记录正在等待弹出的条目，用于"移动到新条目重置驻留""点击取消""tick 复核光标仍在该条目"。
    private ThumbnailPreview? _preview;
    private DispatcherTimer? _previewDwell;
    private DockItem? _previewPending;

    private IntPtr _hwnd;
    private uint _appbarMsg;
    private uint _taskbarCreatedMsg;
    private NativeMethods.APPBARDATA _abd;
    private NativeMethods.RECT _screenRect; // **窗口**物理矩形（含顶部放大余量），非 AppBar 预留带——物理光标校验用，见 IsCursorOutsideWindow
    private PhysicalRect _windowTarget;     // 最近一次协商出的目标窗口矩形：shell 重排条带时用它重申落位（见 AssertWindowRect）
    private PhysicalRect _monitorRect;      // 本 Dock 所在显示器的 rcMonitor（媒体面板水平钳制用，副屏原点可为负/非 0）
    /// <summary>
    /// 本 Dock 所在显示器的**有效缩放**（MDT_EFFECTIVE_DPI/96，与条带几何同一个来源）。
    /// v3 T6 把它同时作为图标烘焙的 DPI 输入：位图边长 = IconSizeDIP·本值，所以"Dock 搬到 200% 的副屏"
    /// 必须重烘一次，否则 150% 那套位图会被再次拉伸（正是"图标劣质"的第二条来源）。
    /// </summary>
    private double? _monitorScale;
    private bool _appbarRegistered;
    private bool _closing; // OnClosing 起置位：阻止关闭前挂起的 BeginInvoke 重注册在 ABM_REMOVE 后复活 AppBar
    private HwndSource? _source;

    public DockWindow(TaskbarManager? taskbar = null)
    {
        _taskbar = taskbar;
        _smtc = new SmtcService();
        _mediaVm = new MediaViewModel(_smtc, Dispatcher); // 先于 InitializeComponent：XAML 模板可能经 RelativeSource 绑定 MediaVm
        InitializeComponent();
        // 分隔符初值取**设置指向的那个主题**（与下面 ThemeManager.Apply 同一表达式），不读 ThemeManager.Current：
        // 此刻本窗口还没应用主题，读 Current 会拿到上一/默认主题 → 首帧少画/多画一道细线。
        _vm = new DockViewModel(_tracker,
            separatorsEnabled: SettingsService.ThemeDefinitionOf(SettingsService.Current).DockSeparatorsEnabled);
        DataContext = _vm;
        WireSurface();
        // 【终审 I-6】ThemeChanged 静态订阅移出构造函数（原 :94）：构造期任何一步抛异常都会留下
        // 一个半构造、无 HWND 却永久挂在静态事件上的僵尸实例——与 TopBarWindow/EnsureTopBar、
        // SwitcherService 已修过的同类泄漏一致。订阅移到 OnSourceInitialized（Show/Handle 生成时），
        // 初值主题在那里显式 ApplyTheme(ThemeManager.Current)；解绑在 OnClosed，两者对称。
        // 任务栏策略切换/explorer 重启重放（TaskbarManager.Apply/Restore → PolicyApplied）后，
        // AppBar 条带需重新协商：Hide 释放了底部条带预留 → Dock 贴真底；Keep → 让位在任务栏上方。
        if (_taskbar is not null) _taskbar.PolicyApplied += OnTaskbarPolicyApplied;
        // 初始主题：设置项驱动（settings.json 的 Theme，缺省鸿蒙；设置窗口切换即持久化）
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
        Loaded += OnWindowLoaded;
    }

    /// <summary>Dock 数据源（设置窗口编辑固定应用后经此重载固定块）。</summary>
    public DockViewModel Vm => _vm;

    /// <summary>媒体 VM（XAML 媒体条目模板经 RelativeSource 绑定到本属性）。</summary>
    public MediaViewModel MediaVm => _mediaVm;

    /// <summary>窗口跟踪器（顶栏"窗口"菜单数据源/激活动作，Task 9）。</summary>
    public WindowTracker Tracker => _tracker;

    /// <summary>顶栏媒体迷你项点击：展开 Dock 媒体面板（无会话时面板自身按 HasSession 折叠，不展开）。
    /// 复用既有 PositionMediaPanel 落位（面板锚在 Dock 胶囊上方），顶栏仅提供触发入口。</summary>
    public void OpenMediaPanelFromTopBar()
    {
        if (!_mediaVm.HasSession) return;
        ShowMediaPanel();
    }

    /// <summary>DockSurface 接线：光标取样 + 左键激活 + 右键条目菜单 + 悬停预览（Task 13）+ 拖放固定/排序（v3 T5）。</summary>
    private void WireSurface()
    {
        Surface.Sigma = SigmaInPitches;
        Surface.CursorProvider = SurfaceCursorXDip;   // 真实物理光标（含 AppBar 矩形守卫），见放大场注释
        Surface.Activated = ActivateItem;
        Surface.ContextRequested = OpenItemContextMenu;
        // 悬停预览：MouseMove 判定当前条目并起/重置 350ms 驻留；离开条目行即收起。
        Surface.MouseMove += OnSurfaceMouseMove;
        Surface.MouseLeave += (_, _) => CancelPreview();

        // 内部拖拽（排序 / 转固定 / 甩出取消固定）的完成回调：策略在窗口，几何与状态机在渲染器。
        Surface.DragStarted = _ => CancelPreview();   // 拖动时绝不弹悬停预览
        Surface.ItemDroppedInside = OnItemDroppedInside;
        Surface.ItemDraggedOut = OnItemDraggedOut;

        // 外部拖入（Explorer/桌面 → Dock）：Surface 是分层窗口里唯一实底可命中的元素，Drop 目标挂它。
        // 分层 + topmost + 非激活窗口接 shell 拖放全程不 Activate（drop 不抢焦点，各处理器都不调激活）。
        Surface.AllowDrop = true;
        Surface.DragOver += OnSurfaceDragOver;
        Surface.Drop += OnSurfaceDrop;
        Surface.DragLeave += (_, _) => Surface.HideInsertionIndicator();
    }

    // ---------- 拖放固定 / 排序 / 取消固定（v3 T5）----------

    /// <summary>
    /// 外部拖过的连续事件：只做"取一次 FileDrop + 探针 + 移动指示条 + 设效果"，绝不重建条目列表、
    /// 不每事件分配（GetData 跨进程可能抛或返残缺，全程守卫）。回收站格上恒为 None（Dock 不接删除动词）。
    /// </summary>
    private void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var probe = Surface.ProbeDrop(e.GetPosition(Surface));
                if (probe.OverTrash) Surface.HideInsertionIndicator();
                else
                {
                    Surface.ShowInsertionIndicator(probe.PinnedSlot);
                    e.Effects = DragDropEffects.Copy;
                }
            }
            else Surface.HideInsertionIndicator();
        }
        catch (Exception ex)
        {
            // 源进程在拖拽中途退出 / 数据不可达：静默降级为 None，绝不让拖放异常波及 AppBar 常驻窗口。
            e.Effects = DragDropEffects.None;
            Logger.Warn($"Dock 拖放 DragOver 取数据失败: {ex.Message}");
        }
        e.Handled = true;
    }

    /// <summary>外部放下：解析 FileDrop → 逐个落位固定；回收站格上丢弃为 no-op（不删除任何文件）。</summary>
    private void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        Surface.HideInsertionIndicator();
        string[]? files = null;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                files = e.Data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 拖放 Drop 取数据失败: {ex.Message}");
            return;
        }
        if (files is null || files.Length == 0) return;

        var probe = Surface.ProbeDrop(e.GetPosition(Surface));
        if (probe.OverTrash)
        {
            Logger.Info($"拖到回收站：{files.Length} 项，执行 no-op（设计：Dock 不调用 shell 删除动词）");
            return;
        }
        var slot = probe.PinnedSlot;
        foreach (var file in files)
        {
            switch (_vm.DropPinAt(file, slot, out var reason))
            {
                case DockViewModel.PinDropResult.Inserted:
                    Logger.Info($"拖入固定：{file} → 固定块槽 {slot}"); slot++; break;
                case DockViewModel.PinDropResult.MovedExisting:
                    Logger.Info($"拖入重复：{file} 已固定 → 移到槽 {slot}（不加第二条）"); slot++; break;
                default:
                    Logger.Warn($"拖入被拒：{reason}"); break;
            }
        }
    }

    /// <summary>内部拖拽落在条带内：固定项→排序；运行窗口项→落在固定块区间才转固定，否则取消；回收站格→no-op。</summary>
    private void OnItemDroppedInside(DockItem dragged, DockSurface.DropProbe probe)
    {
        if (probe.OverTrash)
        {
            Logger.Info($"拖到回收站：{dragged.Label}，执行 no-op（设计：不删除文件、不取消固定）");
            return;
        }
        switch (dragged.Kind)
        {
            case DockItemKind.Pinned:
                var from = Surface.PinnedIndexOf(dragged);
                if (from >= 0 && _vm.MovePinned(from, probe.PinnedSlot))
                    Logger.Info($"Dock 排序：{dragged.Label} 固定槽 {from} → {probe.PinnedSlot}");
                break;

            case DockItemKind.Window:
                // 裁定（macOS 语义）：把运行窗口的图标拖进**固定块区间**即在该位置钉住它；
                // 落在运行区/后缀区则取消——瞬时窗口的顺序由系统跟踪，重排无意义。
                if (!probe.WithinPinnedRegion || dragged.LaunchPath.Length == 0)
                {
                    Logger.Info("拖拽运行窗口到固定块外：取消（不固定、不排序瞬时窗口）");
                    break;
                }
                var r = _vm.DropPinAt(dragged.LaunchPath, probe.PinnedSlot, out var why);
                Logger.Info(r switch
                {
                    DockViewModel.PinDropResult.Inserted => $"拖拽转固定：{dragged.LaunchPath} → 固定块槽 {probe.PinnedSlot}",
                    DockViewModel.PinDropResult.MovedExisting => $"拖拽转固定：{dragged.Label} 已在固定块 → 移到槽 {probe.PinnedSlot}",
                    _ => $"拖拽转固定被拒：{why}",
                });
                break;
        }
    }

    /// <summary>甩出 Dock（释放点在条带外）：固定项→取消固定（macOS flick-out）；运行窗口项→无操作（本就没钉）。</summary>
    private void OnItemDraggedOut(DockItem dragged)
    {
        if (dragged.Kind == DockItemKind.Pinned)
        {
            _vm.Unpin(dragged.LaunchPath);
            Logger.Info($"甩出 Dock 取消固定：{dragged.Label}");
        }
    }

    // ---------- 生命周期 / AppBar ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _appbarMsg = NativeMethods.RegisterWindowMessage("AppBarMessage");
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        RegisterAndPositionAppBar();
        // 主题初值显式落地 + 静态订阅放最后（终审 I-6，与 OnClosed 解绑对称）：构造期 ThemeManager.Apply
        // 只更新全局资源字典，本窗口的亚克力/间距/放大参数从这里开始由 ThemeChanged 驱动，不再硬编码。
        ApplyTheme(ThemeManager.Current);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    // ---------- 主题引擎（Task 13） ----------

    private void OnThemeChanged(ThemeDefinition theme) => ApplyTheme(theme);

    /// <summary>
    /// 主题切换落地：窗口级底色策略 + 间距参数；放大开关与 MaxScale 由动画/悬停路径
    /// 直读 ThemeManager.Current；底色/圆角/图标尺寸/字体/指示样式走字典 DynamicResource 即时生效。
    /// <para>
    /// 【v3 T2 裁定：常驻表面不做抓屏，只做自绘底 + best-effort 系统模糊】本窗口的点击穿透依赖 XAML 的
    /// <c>Background="{x:Null}"</c>（未绘制区域不参与命中测试 → 面板两侧的条带空白可点到底下的桌面/
    /// 图标），而 WPF 里"未绘制区域真的透明"只有 <c>AllowsTransparency=true</c>（分层窗口）能做到。
    /// Dock 又是常驻表面：实时抓屏会把已画好的面板像素抓进背板（鬼影），为抓屏先隐藏一帧又是可见闪烁，
    /// 所以它不调用 <see cref="ScreenBlurBackdrop"/>，而是"DockPanelRoot 自绘 ThemeDockBackground + 1px 亮边"
    /// 作为唯一可见底，另发一次旧 <c>SetWindowCompositionAttribute</c> 请求（<see cref="AcrylicWindow.ApplyTintFallback"/>）
    /// 作增强：系统认账就有真模糊，不认账看到的仍是自绘底，两种结果都不落黑。
    /// 真模糊的抓屏路径只给"显示一次"的瞬态表面（切换器 / 菜单弹层 / 媒体面板）。
    /// </para>
    /// </summary>
    private void ApplyTheme(ThemeDefinition theme)
    {
        ApplyTintFallback(Color.FromArgb(theme.AcrylicTint.A, theme.AcrylicTint.R,
                                         theme.AcrylicTint.G, theme.AcrylicTint.B),
                          theme.BlurEnabled);
        // 排布参数：底色/圆角/字体走 DynamicResource，尺寸/间距/放大写入 DockSurface。
        // IconSize/MaxScale 由设置项驱动（DockIconSize / MagnificationStrength），主题只提供"是否放大"与间距，
        // 见 ApplySurfaceLayoutParams。尺寸变化会改条带高度 → 顺带重排 AppBar（若尚未注册则 no-op，
        // OnSourceInitialized 的 RegisterAndPositionAppBar 会用同一套几何完成首帧定位）。
        ApplySurfaceLayoutParams();
        // 分隔符开关也是主题参数（macOS 有 / GNOME 与鸿蒙没有），"插在哪两块之间"由 Core 规则推导，
        // 渲染器里没有任何 if (theme == mac) —— 这就是"不同风格的 dock 完美还原原生显示方式"的落点。
        _vm.ApplySeparatorPolicy(theme.DockSeparatorsEnabled);
        Surface.Refresh();
        ScheduleReposition();
    }

    /// <summary>
    /// 把当前设置（已规范化）落到 DockSurface 与主题字典：图标尺寸、放大强度、倒影开关。
    /// 主题只决定"是否放大"（macOS 才有放大）与间距；强度与尺寸是用户配置。放大关闭时 MaxScale 归 1、
    /// DockSurface 场恒回落 1（仍走阻尼，不冻结）。图标宽高 + 遮罩几何经 <see cref="ThemeManager.ApplyIconSize"/>
    /// 覆盖主题字典的 ThemeIconSize/ThemeIconClip，使条目模板的图片随 DockIconSize 即时缩放。
    /// <para>
    /// 【v3 T6 的顺序有讲究】先 <see cref="ApplyIconBake"/>（落烘焙参数，其中包含"形状归位图还是归 XAML"）
    /// 再 ThemeManager.ApplyIconSize（据此注入或移除 <c>ThemeIconClip</c>）。反过来就会用上一轮的属主决定
    /// 这一轮的 clip，切换主题的同一帧里出现"双遮罩"或"没人遮罩"。
    /// </para>
    /// </summary>
    private void ApplySurfaceLayoutParams()
    {
        var s = SettingsService.Current;
        var theme = ThemeManager.Current;
        var icon = (double)s.DockIconSize;
        Surface.IconSize = icon;
        Surface.Spacing = theme.ItemSpacing;
        // 行内余量（主题 DockRowInsetDip）：面板贴不贴图标由它决定，写死 20 时三主题都是"半空黑框"。
        Surface.RowInsetDip = theme.DockRowInsetDip;
        Surface.ShowMagnification = theme.ShowMagnification;
        Surface.MaxScale = theme.ShowMagnification ? s.MagnificationStrength : 1.0;
        // 倒影：设置项是**三态**（null = 跟随主题默认，现代 macOS/GNOME/鸿蒙都关），用户显式开过的
        // true/false 永远优先于主题——切主题不会把用户的选择冲掉，也不再让 macOS 默认长出 31 DIP 倒影带。
        Surface.ReflectionEnabled = s.ReflectionEnabledFor(theme.ReflectionDefault);
        // 面板内边距：XAML 里 Surface.Margin / DockPanelRoot.Padding 是同一组主题值，
        // VisiblePanelDip() 算的也是它 —— 视觉与"上报 shell 的预留带"必须来自同一个数（v3 §1.C）。
        var padding = new Thickness(theme.DockPanelPaddingXDip, theme.DockPanelPaddingYDip,
                                    theme.DockPanelPaddingXDip, theme.DockPanelPaddingYDip);
        Surface.Margin = padding;
        DockPanelRoot.Padding = padding;
        // 烘焙参数（主题形状 / 尺寸 / DPI / IconTreatment）→ 决定 ThemeIconClip 的属主，必须在下面两步之前
        ScheduleIconRebind();
        // 图标宽高 + 遮罩 + 分隔符几何（线粗/高度/盒宽）随尺寸与主题重注入主题字典（冻结/数值，非逐帧路径）。
        ThemeManager.ApplyIconSize(icon, Application.Current);
    }

    // ---------- 图标烘焙（v3 T6） ----------

    /// <summary>
    /// 本 Dock 所在显示器的缩放（物理/DIP）。首选 <see cref="_monitorScale"/>（MonitorGeometry 按锚点屏
    /// 取的 MDT_EFFECTIVE_DPI/96，与条带几何同源）；AppBar 还没协商过（构造期）时用 WPF 自己的 DPI，
    /// 两者都拿不到才按 1.0 —— <see cref="IconBakeContext.Configure"/> 只在真的变化时动作，
    /// 所以这里的兜底序列不会造成反复重烘。
    /// </summary>
    private double CurrentDpiScale()
    {
        if (_monitorScale is { } m && m > 0) return m;
        if (_hwnd == IntPtr.Zero) return 1.0;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return dpi > 0 ? dpi : 1.0;
        }
        catch (InvalidOperationException)
        {
            return 1.0;   // PresentationSource 尚未就绪
        }
    }

    /// <summary>
    /// 图标烘焙参数落地 + 需要时重挂条目绑定。
    /// <para>
    /// 【为什么把重建防抖】图标尺寸滑杆每步都会改 DPI·尺寸这一组键；立即重建 = 每步都重烘一排图标
    /// （UI 线程几十毫秒），既浪费也会把 frametime 的 P95 顶起来。防抖到落定之后一次：
    /// 拖动期间 <c>ThemeIconSize</c> 已即时改了方框大小，图片被高质量拉伸着看（观感略软但不是错的尺寸），
    /// 松手后立刻换成 1:1 的烘焙位图。
    /// </para>
    /// </summary>
    private void ScheduleIconRebind()
    {
        var changed = IconBakeContext.Configure(ThemeManager.Current, Surface.IconSize,
                                                CurrentDpiScale(), SettingsService.Current.IconTreatment);
        if (changed) ThemeManager.ApplyIconSize(Surface.IconSize, Application.Current);  // 属主可能翻转 → 重落 clip
        if (!changed) return;

        _iconRebindDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _iconRebindDebounce.Stop();
        _iconRebindDebounce.Tick -= OnIconRebindTick;
        _iconRebindDebounce.Tick += OnIconRebindTick;
        _iconRebindDebounce.Start();
    }

    private void OnIconRebindTick(object? sender, EventArgs e)
    {
        _iconRebindDebounce?.Stop();
        if (_closing) return;
        Surface.RebindIconContainers();   // 模板重实例化 → 绑定按新的烘焙参数重新求值
        Surface.Refresh();
    }

    // ---------- 条带几何（随图标尺寸/放大强度动态；预留带 ≠ 窗口矩形） ----------

    /// <summary>
    /// 可见面板高 = 行高（IconSize + 主题行内余量，开启倒影时另加倒影余量，见 DockSurface.RowHeightDip）
    /// + 主题上下内边距各一（XAML 里 Surface.Margin / DockPanelRoot.Padding 由 <see cref="ApplySurfaceLayoutParams"/>
    /// 写成同一组主题值，所以这里不是第二条算术路径而是同一来源的两次消费）。
    /// <b>这是上报 shell 的 AppBar 预留带高度</b>（v3 §1.C：停靠区只预留 Dock 本体）。
    /// IconSize=44 时：macOS 66 DIP、Linux 70、鸿蒙 72（v2 统一 76，倒影开启 95.2）。
    /// </summary>
    private double VisiblePanelDip()
        => DockRowMath.PanelHeightDip(Surface.RowHeightDip, ThemeManager.Current.DockPanelPaddingYDip);

    /// <summary>
    /// 窗口在面板之上的透明余量，供放大后的图标向上生长不被窗口上沿裁切。
    /// 无放大主题恒取 <see cref="MinStripHeadroomDip"/>（=22，与 v1 实测一致）；macOS 下按
    /// 图标尺寸与当前倍率算越出量。旧写法是 <c>IconSize·(MaxScale−1) − 16</c>，那个 16 是
    /// "旧统一行内余量 20 的一半 + 旧的 6 DIP 面板边距"；v3 T4 把行内余量交给主题（macOS 10）之后
    /// 常量就不成立了——面板收紧 10 DIP，放大后的图标顶边会被窗口上沿切掉一截。
    /// 现在越出量由 <see cref="DockRowMath.MagnificationHeadroomDip"/> 用同一组主题值算：
    /// IconSize=44/MaxScale=1.8/余量 10/边距 6 → 24.2 → 取 25（≥22 的下限保住"贴底无缝"的实测结论）。
    /// <para>
    /// v3 T1 起这截余量<b>只在窗口矩形里</b>（<c>StripMath.WindowRect</c> 把窗口在预留带之上放大），
    /// 不再进 ABM_SETPOS 的上报值 —— 窗口可以越出自己的 AppBar 预留带，预留带只决定工作区。
    /// 于是 macOS 语义成立：放大后的图标越界生长到桌面/窗口之上，工作区不多让一个像素。
    /// </para>
    /// </summary>
    private double StripHeadroomDip()
    {
        var theme = ThemeManager.Current;
        if (!theme.ShowMagnification) return MinStripHeadroomDip;
        return DockRowMath.MagnificationHeadroomDip(Surface.IconSize, Surface.MaxScale,
                                                    theme.DockRowInsetDip, theme.DockPanelPaddingYDip,
                                                    MinStripHeadroomDip);
    }

    /// <summary>
    /// 设置页改动 DockIconSize/MagnificationStrength 后的即时生效入口（App.ApplyDockLayout 调用）：
    /// 重落排布参数 + 重排条带（图标尺寸变了面板/条带要跟着长，否则大图标被窗口上沿裁切）。
    /// </summary>
    public void RefreshLayoutFromSettings()
    {
        ApplySurfaceLayoutParams();
        Surface.Refresh();
        ScheduleReposition();
    }

    /// <summary>
    /// 请求重排条带：立即返回，真正的 <see cref="PositionAsAppBar"/> 延后到防抖到期执行。
    /// AppBar 尚未注册（构造早期）时到期重排也无害——OnSourceInitialized 的首帧定位已用同一套几何。
    /// 惰性建计时器（DockWindow 未必走到滑杆路径，避免构造期多分配一个计时器）。
    /// </summary>
    private void ScheduleReposition()
    {
        _repositionDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _repositionDebounce.Stop();
        _repositionDebounce.Tick -= OnRepositionDebounceTick;
        _repositionDebounce.Tick += OnRepositionDebounceTick;
        _repositionDebounce.Start();
    }

    private void OnRepositionDebounceTick(object? sender, EventArgs e)
    {
        _repositionDebounce?.Stop();
        if (_closing) return;
        PositionAsAppBar();
    }


    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _tracker.Start();
        // 动画驱动态（CompositionTarget.Rendering 钩子）由 DockSurface 自管：光标进入面板即 Wake，
        // 全部收敛且无悬停目标时自行摘钩——此处不再有 15ms DispatcherTimer。
        // 渲染层级一次性入日志：tier 0=软件渲染、1=部分加速、2=硬件加速。
        // 本机实测 tier=2（硬件加速），所以 frametime.ps1 里 ~30ms 的帧节奏**不是**软件渲染造成的：
        // 它来自 AllowsTransparency 分层窗口的呈现路径（WPF 光栅到内存位图 → DWM 逐帧合成）在这台 VM
        // 上的下限。排查"掉帧是代码还是环境"时先看这一行，别把 tier=2 误读成降级。
        var tier = RenderCapability.Tier >> 16;
        Logger.Info($"Dock 就绪：WPF 渲染 tier={tier}（0=软件渲染，2=硬件加速）" +
                    $" magnification={ThemeManager.Current.ShowMagnification} maxScale={ThemeManager.Current.MaxScale}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;                             // 先于 ABM_REMOVE：作废队列中尚未执行的 BeginInvoke 重注册
        _repositionDebounce?.Stop();                 // 作废挂起的条带重排（OnClosing 后不再 ABM_SETPOS）
        _iconRebindDebounce?.Stop();                 // 作废挂起的图标重烘（OnClosing 后不再重建可视树）
        _previewDwell?.Stop();                       // 作废挂起的预览驻留
        _preview?.Dispose();                         // 注销缩略图句柄 + 销毁宿主窗口（Task 13，必走，防句柄泄漏）
        _preview = null;
        _mediaPanel?.Close();                        // 面板随 Dock 一起关闭（Closed 处理器会清空引用）
        Surface.Shutdown();                           // 摘掉 CompositionTarget.Rendering 钩子
        UnregisterAppBar();                           // ABM_REMOVE 必须在 hwnd 销毁前
        _source?.RemoveHook(WndProc);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged; // 解除主题订阅，防止闭包引用已关闭窗口
        if (_taskbar is not null) _taskbar.PolicyApplied -= OnTaskbarPolicyApplied;
        _vm.Detach();
        _tracker.Dispose();
        _mediaVm.Dispose();                          // 停定时器 + 解除 SmtcService 事件订阅
        _smtc.Dispose();
        base.OnClosed(e);
    }

    private void RegisterAndPositionAppBar()
    {
        if (_closing) return;                         // 关闭竞态：TaskbarCreated/WM_DISPLAYCHANGE 挂起的重注册不再执行
        if (_appbarRegistered) UnregisterAppBar();    // 重注册路径：先 ABM_REMOVE
        _abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _appbarMsg
        };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref _abd);
        _appbarRegistered = true;
        Logger.Info($"AppBar ABM_NEW 完成 hwnd=0x{_hwnd.ToInt64():X}");
        PositionAsAppBar();
        ScheduleHideSettle();
    }

    /// <summary>
    /// Hide 策略下 AppBar 注册/重注册会引发 shell 重布局，Win11 可能把任务栏摆回屏幕内、甚至把
    /// autohide 状态位改回去（= 重新占用底部条带）：T+0.8s / T+3s 各重申一次（重释放条带 + 移出窗口）
    /// 并重新协商条带位置。非 Hide 策略 Reassert 为 no-op，不调度。
    /// </summary>
    private void ScheduleHideSettle()
    {
        if (_taskbar is not { Current: Services.TaskbarPolicy.Hide }) return;
        foreach (var delayMs in new[] { 800, 3000 })
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_closing) return;
                _taskbar.Reassert();
                PositionAsAppBar();
            };
            timer.Start();
        }
    }

    /// <summary>任务栏策略变更落地（Apply/Restore）后重申策略并重新协商 AppBar 条带。</summary>
    private void OnTaskbarPolicyApplied()
    {
        if (_closing || !_appbarRegistered) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_appbarRegistered) return;
            _taskbar?.Reassert();
            PositionAsAppBar();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void UnregisterAppBar()
    {
        if (!_appbarRegistered) return;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref _abd);
        _appbarRegistered = false;
    }

    /// <summary>
    /// ABE_BOTTOM 条带定位（v3 T1，裁定见 v3-plan §1.C）：<b>上报 shell 的预留带 = 可见面板高</b>
    /// （<see cref="VisiblePanelDip"/>），真实窗口矩形 = 预留带 + 顶部放大余量
    /// （<see cref="StripHeadroomDip"/>）。放大后的图标照样向上越界生长到桌面之上，但那截透明带
    /// 不再从工作区里被划走——v2 把"面板+余量"整条报给 ABM_SETPOS，IconSize=44 时工作区白让了
    /// 22 DIP（用户"我只需要 dock 的大小不需要额外的覆盖"的工作区侧含义）。
    /// 窗口像素尺寸与 v2 完全一致，所以 DockSurface 的可用高度假设、面板 VerticalAlignment=Bottom
    /// 贴条带底、两侧点击穿透全部原样保留。
    /// <para>
    /// 【几何来源】本窗口所在（或设置指定）显示器的 <c>rcMonitor</c> 与该屏有效 DPI
    /// （<see cref="MonitorGeometry"/>），不再是 <c>GetSystemMetrics</c> 的主屏尺寸。
    /// </para>
    /// <para>
    /// 【底边仍以 shell 给的可用底边为准】（Task 7 实测口径，勿回退）：Keep/AutoHide 策略下那是任务栏
    /// 上沿（Dock 悬在其上方，符合预期）；Hide 策略下 <see cref="TaskbarManager"/> 已用
    /// ABM_SETSTATE(ABS_AUTOHIDE) 释放掉任务栏的条带预留，QUERYPOS 返回的 bottom 就是**真显示器底边**，
    /// 于是窗口底边 == rcMonitor.Bottom，Dock 无缝贴底（实测 gap 48px → 0px）。
    /// </para>
    /// <para>
    /// 【顺序硬约束】先写 WPF 的 Left/Top/Width/Height（DIP）并 UpdateLayout，再落物理 SetWindowPos：
    /// 反过来的话 WPF 会在随后的布局帧里按自己的 DIP→物理换算覆盖我们的物理矩形（高 DPI 屏上就是
    /// 一条缝）。定位完回读 GetWindowRect 落一行校验日志——真机仍有偏差时那行就是判因证据。
    /// </para>
    /// </summary>
    private void PositionAsAppBar()
    {
        if (!_appbarRegistered) return;
        var target = MonitorGeometry.ForEdge(_hwnd, StripEdge.Bottom, VisiblePanelDip(), StripHeadroomDip(),
                                             SettingsService.Current.DockMonitor);
        if (target is null) return;   // 显示器取不到（拔屏瞬间）：保持上次几何，不写非法矩形
        var t = target.Value;
        _monitorRect = t.MonitorRect;                       // 媒体面板水平钳制要用**本屏**左右缘
        // v3 T6：本屏有效缩放同时是图标烘焙的 DPI 输入。换屏 / 改缩放 / 拔屏都会重走这里，
        // 于是这一处就是"DPI 变了就重烘"的唯一入口（Configure 内部比对，值没变就零动作）。
        _monitorScale = t.DpiScale;
        ScheduleIconRebind();
        var panelH = t.Reserve.Height;                        // 预留带高（上报 shell，决定工作区）
        var headroomH = t.Window.Height - t.Reserve.Height;   // 向上越界生长的透明余量（不占工作区）

        _abd.uEdge = NativeMethods.AbeBottom;
        _abd.rc = MonitorGeometry.ToNative(t.Reserve);        // 该屏全宽 + 面板高（SM_CXSCREEN 已退役）
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref _abd);
        var available = MonitorGeometry.ToCore(_abd.rc);      // shell 允许我们占到的底边（= 既有 AppBar 的上沿）
        // 修正：底边让位既有 AppBar/任务栏，高度以期望面板高为准
        var reserve = StripMath.Reserve(available, panelH, StripEdge.Bottom);
        var expect = $"({reserve.Left},{reserve.Top})-({reserve.Right},{reserve.Bottom})";
        _abd.rc = MonitorGeometry.ToNative(reserve);
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref _abd);
        var placed = MonitorGeometry.ToCore(_abd.rc);         // shell 最终落地的预留带
        var actual = $"({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom})";
        var window = StripMath.WindowRect(placed, headroomH, StripEdge.Bottom);
        // 贴底判据（Task 7 实测口径，日志即证据）：available.Bottom == rcMonitor.Bottom ⇔ 任务栏条带
        // 预留已释放 ⇔ Dock 真贴底；未释放（Keep / 释放失败）时 available.Bottom = 任务栏上沿，
        // Dock 让位在其上方，此时"间隙"即用户看到的空档。
        var gap = t.MonitorRect.Bottom - available.Bottom;
        Logger.Info($"AppBar QUERYPOS 可用底边={available.Bottom}/rcMonitor底={t.MonitorRect.Bottom}" +
                    $" {(gap == 0 ? "→ 真屏幕底边（间隙 0px，条带预留已释放）" : $"→ 任务栏仍占 {gap}px")}" +
                    $"（策略={_taskbar?.Current.ToString() ?? "N/A"}）monitor=0x{t.Monitor.ToInt64():X}" +
                    $" dpi={t.DpiScale:0.##} 面板={panelH}px 余量={headroomH}px");
        // 单独成行的 "AppBar SETPOS rc=(l,t)-(r,b)"：scripts/frametime.ps1 正是按这条正则取 Dock 条带
        // 矩形（v2 把 SETPOS 挤在 QUERYPOS 那行里，正则从来没命中过，探针只能猜 "$b.Bottom-100"）。
        Logger.Info($"AppBar SETPOS rc={actual} 期望={expect} {(actual == expect ? "一致" : "不一致")}");
        // 面板 DIP 一并落日志：真机反馈"dock 还有一段黑框"时，这一行就能判定是面板没贴图标
        // （行高 ≈ IconSize + 主题余量）还是别的问题，不必再靠截图目测。
        Logger.Info($"Dock 工作区 预留高={placed.Height}px（=面板 {VisiblePanelDip():F1}DIP，行高 {Surface.RowHeightDip:F1}DIP，" +
                    $"图标 {Surface.IconSize:F0}DIP，倒影 {(Surface.ReflectionEnabled ? "开" : "关")}）" +
                    $"窗口高={window.Height}px 净得={window.Height - placed.Height}px");

        Left = StripMath.ToDip(window.Left, t.DpiScale);
        Top = StripMath.ToDip(window.Top, t.DpiScale);
        Width = Math.Max(1, StripMath.ToDip(window.Width, t.DpiScale));
        Height = Math.Max(1, StripMath.ToDip(window.Height, t.DpiScale));
        UpdateLayout();   // 让上面四个 DIP 属性的重排先落地，物理矩形才是最后一步
        _windowTarget = window;
        // _screenRect 用**目标窗口矩形**（含顶部放大余量），不是预留带：光标命中范围必须覆盖那截
        // 透明带，否则光标一越出面板上沿就判"离窗"、场回落、图标缩回 —— 放大自激振荡。
        _screenRect = MonitorGeometry.ToNative(window);
        ApplyPhysicalRect(window);

        // 回读校验（v3 §1.C）：真机若仍偏移，这一行日志就是判因证据。
        if (NativeMethods.GetWindowRect(_hwnd, out var read))
        {
            var offset = read.Bottom - t.MonitorRect.Bottom;
            Logger.Info($"Dock 几何 window.bottom={read.Bottom} rcMonitor.bottom={t.MonitorRect.Bottom}" +
                        $" {(offset == 0 ? "→ 贴底OK" : $"→ 偏移 {offset}px（策略未释放/被抢占）")}" +
                        $" 预留={actual} 窗口=({read.Left},{read.Top})-({read.Right},{read.Bottom})");
        }
        else
        {
            Logger.Warn($"Dock 几何 回读 GetWindowRect 失败，无法校验贴底（目标 ({window.Left},{window.Top})-({window.Right},{window.Bottom})）");
        }
    }

    /// <summary>把目标矩形落到物理层（写窗口位置的唯一出口，重申路径复用它）。</summary>
    private void ApplyPhysicalRect(PhysicalRect window)
        => NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, window.Left, window.Top, window.Width, window.Height,
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);

    /// <summary>
    /// 把窗口矩形重申为 <see cref="_windowTarget"/>：只 <c>SetWindowPos</c>，<b>不</b>重发
    /// <c>ABM_SETPOS</c>（预留带没变 → 不再触发 shell 重排 → 不构成"重申 → 广播 → 再重申"的环，
    /// 与"WM_SETTINGCHANGE 不可在此重注册"同一条纪律）。已等于目标时静默返回。
    /// 与顶栏同一套护栏：shell 重排条带时会把 AppBar 窗口摆到"当前工作区"边线上，
    /// 对 Dock 就是把底边让给自己已经预留的那 102px（贴底无缝当场变成"底部悬空"）。
    /// </summary>
    private void AssertWindowRect()
    {
        if (_closing || !_appbarRegistered) return;
        if (_windowTarget.Width <= 0 || _windowTarget.Height <= 0) return;   // 尚未定位过（首帧前）
        if (!NativeMethods.GetWindowRect(_hwnd, out var now)) return;
        if (now.Left == _windowTarget.Left && now.Top == _windowTarget.Top &&
            now.Right == _windowTarget.Right && now.Bottom == _windowTarget.Bottom) return;
        Logger.Info($"Dock 几何 窗口被移到 ({now.Left},{now.Top})-({now.Right},{now.Bottom})" +
                    $" ≠ 目标 ({_windowTarget.Left},{_windowTarget.Top})-({_windowTarget.Right},{_windowTarget.Bottom})" +
                    $"（偏移 {now.Bottom - _windowTarget.Bottom}px）→ 重申贴底");
        ApplyPhysicalRect(_windowTarget);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appbarMsg != 0 && msg == _appbarMsg)
        {
            handled = true;
            // ABN_POSCHANGED：shell 重排了条带 → 重申窗口落位（空间被真正抢占由重注册流程覆盖）。
            if (wParam.ToInt32() == NativeMethods.AbnPosChanged) Dispatcher.BeginInvoke(AssertWindowRect);
            return IntPtr.Zero;
        }
        // 任何来源的位置变化都比对一次；BeginInvoke 排在本次落位之后，自己写的移动只命中静默分支。
        if (msg == NativeMethods.WmWindowPosChanged) Dispatcher.BeginInvoke(AssertWindowRect);
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            handled = true; // explorer 重建任务栏 → ABM_REMOVE + 重注册；任务栏策略（隐藏/自动隐藏）一并重放
            _taskbar?.HandleTaskbarCreated();
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
            return IntPtr.Zero;
        }
        // WM_DISPLAYCHANGE：分辨率/显示器拓扑变化 → 重新协商条带。
        // 注意 WM_SETTINGCHANGE 不可在此重注册：ABM_SETPOS 改变工作区会再触发 WM_SETTINGCHANGE
        // 广播，形成"注册 → 广播 → 再注册"的无限循环（QA 实测 6 秒 63 次重注册）。
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RegisterAndPositionAppBar);
        }
        return IntPtr.Zero;
    }

    // ---------- 放大场光标取样 ----------

    /// <summary>
    /// 物理光标是否已不在本窗口矩形内；AppBar 未就绪 / GetCursorPos 失败时视为"在窗内"（交由取样继续判断）。
    /// <para>
    /// <b>比较基准是 <see cref="_screenRect"/> = 窗口矩形，不是 AppBar 预留带矩形</b>（v3 T1 起两者不等：
    /// 预留带只有面板高，窗口还含顶部放大余量）。收紧到预留带 = 光标一越出面板上沿就判"离窗"、放大场
    /// 回落、图标缩回、光标又落回面板 —— 反复自激振荡，放大功能直接不可用。
    /// </para>
    /// <para>
    /// 保留 v1 守卫：WPF 会在布局变化后按"最后已知位置"合成 MouseMove，若以事件坐标为准，
    /// 光标跳跃离窗后这些陈旧事件会把放大状态钉住。v2 更进一步——动画每帧直接取真实光标，
    /// 事件只作"唤醒"信号，因此陈旧事件连唤醒语义都没有（离场即在下一帧回落）。
    /// 空闲摘钩改由 <see cref="DockSurface"/> 按条目行的水平影响范围判定（评审发现 #1）。
    /// </para>
    /// </summary>
    private bool IsCursorOutsideWindow(NativeMethods.POINT pt)
    {
        if (!_appbarRegistered) return false;
        return pt.X < _screenRect.Left || pt.X >= _screenRect.Right
            || pt.Y < _screenRect.Top || pt.Y >= _screenRect.Bottom;
    }

    /// <summary>
    /// DockSurface 的光标取样：真实物理光标 → 窗口 DIP（PointFromScreen，PerMonitorV2 下随当前 DPI）
    /// → surface 本地 DIP（TranslatePoint）。不在窗口内 / 源未就绪时返回 null = 无悬停目标（场回落 1）。
    /// 连续 X 交给 <see cref="MagnificationField"/>，scale 因而随光标平滑变化，
    /// 不再出现 v1"悬停索引切换 → 整排图标一起跳"的卡顿。
    /// 每帧只调一次 GetCursorPos（评审发现 #1 的伴随浪费：旧写法守卫与取样各调一次）。
    /// </summary>
    private double? SurfaceCursorXDip()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return null;
        if (IsCursorOutsideWindow(pt)) return null;
        try
        {
            var inWindow = PointFromScreen(new Point(pt.X, pt.Y)); // 物理 px → 窗口 DIP
            return TranslatePoint(inWindow, Surface).X;             // 窗口 DIP → surface 本地 DIP
        }
        catch (InvalidOperationException)
        {
            return null; // PresentationSource 尚未就绪（显示前）
        }
    }

    private void ActivateItem(DockItem item)
    {
        // 点击 = 取消挂起的预览弹出（brief：clicking cancels the pending show），并收起已在显示的预览。
        CancelPreview();
        switch (item.Kind)
        {
            case DockItemKind.Window:
                var wi = item.WindowHandle is { } h
                    ? _tracker.Windows.FirstOrDefault(w => w.Handle == h)
                    : null;
                if (wi is not null) _tracker.ActivateOrMinimize(wi);
                break;

            case DockItemKind.RecycleBin:
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
                }
                catch (Exception ex) { Logger.Warn($"打开回收站失败: {ex.Message}"); }
                break;

            case DockItemKind.Pinned:
                if (item.LaunchPath.Length == 0) break;
                try
                {
                    Process.Start(new ProcessStartInfo(item.LaunchPath) { UseShellExecute = true });
                    // 启动弹跳（Task 15）：BounceEnabled 时该项在 DockSurface 现有 vsync 循环里跑
                    // ~600ms 的 ElasticOut 纵向弹跳；重复点击不叠加（StartBounce 重置进度）。
                    if (SettingsService.Current.BounceEnabled) Surface.StartBounce(item);
                }
                catch (Exception ex) { Logger.Warn($"启动 {item.LaunchPath} 失败: {ex.Message}"); }
                break;

            case DockItemKind.Media:
                ToggleMediaPanel();
                break;
        }
    }

    // ---------- 右键菜单：固定 / 取消固定（FluentMenu，Task 15） ----------

    /// <summary>
    /// 右键条目在光标处弹 <see cref="FluentMenu"/>（自绘高级弹层，观感与托盘/顶栏菜单一致；
    /// v2 Task 15 前这里是原生 ContextMenu——"菜单无高级感"不满的残留路径，已随本次迁移删除）。
    /// 语义不变：Window 项可"固定到 Dock"（写其 exe LaunchPath），Pinned 项可"取消固定"，
    /// 均经 <see cref="DockViewModel"/> 写回 dock.json；媒体/回收站无固定语义不弹。
    /// 仍走"代码后置手动开关"而非 ContextMenuService：实测后者在本 AppBar 窗口把事件路由到
    /// ItemsControl 层（Source=Surface），容器菜单永远不打开。条目命中由 DockSurface.HitTestItem
    /// 用放大后矩形给出；锚点 Surface.PointToScreen 返回物理像素（FluentMenu.Show 口径一致）。
    /// </summary>
    private void OpenItemContextMenu(DockItem item, Point anchorInSurface)
    {
        var canPin = item is { Kind: DockItemKind.Window, LaunchPath.Length: > 0 };
        var canUnpin = item.Kind == DockItemKind.Pinned;
        if (!canPin && !canUnpin) return;

        var items = new List<MenuItemView>();
        if (canPin)
            items.Add(new MenuItemView
            {
                Id = "pin", Label = "固定到 Dock", Glyph = "",
                Invoke = () => _vm.PinExecutable(item.LaunchPath), // 固定当前窗口的 exe 路径（去重，写回 dock.json）
            });
        if (canUnpin)
            items.Add(new MenuItemView
            {
                Id = "unpin", Label = "取消固定", Glyph = "",
                Invoke = () => _vm.Unpin(item.LaunchPath),
            });

        var p = Surface.PointToScreen(anchorInSurface);
        FluentMenu.Show(new FluentPopupHost(), new Point(p.X, p.Y), items, i => i.Invoke?.Invoke());
    }

    // ---------- 悬停实时预览（Task 13）----------

    private const int PreviewDwellMs = 350;
    private const double PreviewMaxWDip = 340, PreviewMaxHDip = 240;

    /// <summary>鼠标在 DockSurface 上移动：命中条目变化时重置 350ms 驻留计时；无预览目标即收起当前预览。
    /// 开关关（PreviewEnabled=false）直接收起并不起计时——这是让设置里的预览开关真正生效的读取点。</summary>
    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (Surface.IsDragging) return;   // 内部拖拽进行中：预览让位给指示条，绝不叠加缩略图
        if (!SettingsService.Current.PreviewEnabled)
        {
            if (_preview is { IsShowing: true } || _previewPending is not null) CancelPreview();
            return;
        }

        var hit = Surface.HitTestItem(e.GetPosition(Surface));
        // 同一目标：不重复计时（_previewPending 已置，无论"等待中"还是"已显示"都保持不动）。
        // 例外：预览已收起且驻留计时已停（如源窗口死亡被看门狗隐藏）——此时 pending 是陈旧值，
        // 若仍早退则光标不离开该图标就永远无法再次唤出预览（Task 13 评审 Minor），清空后落入
        // 下方流程对同一条目重新起驻留。
        if (hit is not null && _previewPending is not null && hit.Id == _previewPending.Id
            && (_preview is { IsShowing: true } || _previewDwell?.IsEnabled == true))
            return;

        // 目标改变（含移到空白 / 回收站 / 媒体）：先收起旧的，再决定是否对新目标起驻留。
        _previewDwell?.Stop();
        if (_preview is { IsShowing: true }) _preview.Hide();
        _previewPending = null;

        if (hit is null || !PreviewSourceFor(hit, out _)) return;   // 无可预览窗口 → 不弹

        _previewPending = hit;
        _previewDwell ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewDwellMs) };
        _previewDwell.Stop();
        _previewDwell.Tick -= OnPreviewDwellTick;
        _previewDwell.Tick += OnPreviewDwellTick;
        _previewDwell.Start();
    }

    private void OnPreviewDwellTick(object? sender, EventArgs e)
    {
        _previewDwell?.Stop();
        var pending = _previewPending;
        if (pending is null) return;
        if (!SettingsService.Current.PreviewEnabled) { _previewPending = null; return; }

        // tick 时按真实光标复核（MouseMove 事件坐标可能陈旧）：光标仍悬在同一条目才弹。
        if (!TryItemUnderCursor(out var current) || current?.Id != pending.Id)
        {
            _previewPending = null;
            return;
        }
        if (!PreviewSourceFor(pending, out var src)) { _previewPending = null; return; }
        ShowPreviewFor(pending, src);   // 显示后保留 _previewPending，同条目后续 MouseMove 走上面的早退分支
    }

    /// <summary>取消驻留并收起预览（鼠标离开 / 点击 / 开关关闭 时调用）。</summary>
    private void CancelPreview()
    {
        _previewDwell?.Stop();
        _previewPending = null;
        _preview?.Hide();
    }

    /// <summary>预览开关变更的即时生效入口（App.ApplyPreviewEnabled 调用）：
    /// 关→立即收起正在显示的预览；开→无需动作（下次悬停按新值放行）。这让 PreviewEnabled 不再是死开关。</summary>
    public void ApplyPreviewSetting()
    {
        if (!SettingsService.Current.PreviewEnabled) CancelPreview();
    }

    /// <summary>用真实物理光标复核命中的条目（与放大场同一取样口径：GetCursorPos → Surface.PointFromScreen →
    /// HitTestItem），避免陈旧 MouseMove 把预览钉在已离开的条目上。</summary>
    private bool TryItemUnderCursor(out DockItem? item)
    {
        item = null;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        try { item = Surface.HitTestItem(Surface.PointFromScreen(new Point(pt.X, pt.Y))); }
        catch (InvalidOperationException) { return false; }   // PresentationSource 尚未就绪
        return true;
    }

    /// <summary>把预览弹在条目放大后矩形上方并水平居中、钳到工作区。坐标换算复用 <see cref="PositionMediaPanel"/>
    /// 的同一路径：Surface 本地 DIP → PointToScreen 得物理 px → 除以本显示器 PixelsPerDip 得屏幕 DIP。</summary>
    private void ShowPreviewFor(DockItem item, IntPtr sourceHwnd)
    {
        if (Surface.GetItemBounds(item) is not { } b || b.Width <= 0) { _preview?.Hide(); return; }
        _preview ??= new ThumbnailPreview();

        var dpi = VisualTreeHelper.GetDpi(Surface).PixelsPerDip;
        if (dpi <= 0) dpi = 1;
        _preview.DpiScale = dpi;
        _preview.Title = item.Label;

        var tl = Surface.PointToScreen(new Point(b.X, b.Y));
        var br = Surface.PointToScreen(new Point(b.X + b.Width, b.Y + b.Height));
        var anchorDip = new Rect(tl.X / dpi, tl.Y / dpi, (br.X - tl.X) / dpi, (br.Y - tl.Y) / dpi);
        _preview.Show(sourceHwnd, anchorDip, PreviewMaxWDip, PreviewMaxHDip);
    }

    /// <summary>解析条目对应的可预览源窗口句柄：Window→其句柄；Pinned→该 exe 最近的一个可见非最小化窗口；
    /// RecycleBin/Media→无（不预览）。</summary>
    private bool PreviewSourceFor(DockItem item, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        switch (item.Kind)
        {
            case DockItemKind.Window:
                var h = item.WindowHandle ?? IntPtr.Zero;
                if (h != IntPtr.Zero && NativeMethods.IsWindow(h) && !NativeMethods.IsIconic(h))
                {
                    hwnd = h;
                    return true;
                }
                return false;

            case DockItemKind.Pinned:
                hwnd = MostRecentWindowForExe(item.LaunchPath);
                return hwnd != IntPtr.Zero;

            default:   // RecycleBin / Media：不预览
                return false;
        }
    }

    /// <summary>查找该启动项 exe 当前最近的一个可预览窗口（逆序=最近插入优先，取首个非最小化可见者）。
    /// .lnk 快捷方式先解析目标 exe，再按进程映像基础名比对（LaunchPath 常为 .lnk，窗口 ExePath 是真实 exe）。</summary>
    private IntPtr MostRecentWindowForExe(string launchPath)
    {
        if (string.IsNullOrWhiteSpace(launchPath)) return IntPtr.Zero;
        var target = ExeBaseName(ResolveExePath(launchPath));
        if (target.Length == 0) return IntPtr.Zero;

        var list = _tracker.Windows;
        IntPtr fallback = IntPtr.Zero;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var w = list[i];
            if (!NativeMethods.IsWindow(w.Handle)) continue;
            if (!string.Equals(ExeBaseName(w.ExePath), target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!NativeMethods.IsIconic(w.Handle) && NativeMethods.IsWindowVisible(w.Handle))
                return w.Handle;
            if (fallback == IntPtr.Zero) fallback = w.Handle;   // 只剩最小化窗口：交 Show 的 IsIconic 兜底（不弹）
        }
        return fallback;
    }

    private static string ResolveExePath(string launchPath)
        => launchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLink.Resolve(launchPath).TargetPath
            : launchPath;

    private static string ExeBaseName(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return string.Empty; }
    }

    // ---------- 媒体面板（实况窗展开） ----------

    private void ToggleMediaPanel()
    {
        if (!_mediaVm.HasSession) return; // 无会话：胶囊已折叠（占位命中不展开）
        if (_mediaPanel is { IsVisible: true })
        {
            _mediaPanel.Hide();
            _panelAutoHiddenAtUtc = null;
            return;
        }
        // 点击胶囊时面板会先因失焦自动 Hide（Deactivated 先于本 MouseUp），
        // 刚刚自动收起（<600ms）→ 本次点击视为"收起"而非"再次展开"
        if (_panelAutoHiddenAtUtc is { } at && (DateTime.UtcNow - at).TotalMilliseconds < 600)
        {
            _panelAutoHiddenAtUtc = null;
            return;
        }
        ShowMediaPanel();
    }

    private void ShowMediaPanel()
    {
        try
        {
            if (_mediaPanel is null)
            {
                _mediaPanel = new MediaPanel(_mediaVm) { Owner = this };
                // 仅在"失焦自动收起"开启时记录时间戳：关闭开关后面板不因失焦隐藏，
                // 若仍记录会把 <600ms 的 toggle 抑制窗口错误应用到点击展开
                _mediaPanel.Deactivated += (_, _) =>
                {
                    if (_mediaVm.PanelAutoHideOnDeactivate) _panelAutoHiddenAtUtc = DateTime.UtcNow;
                };
                _mediaPanel.Closed += (_, _) => { _mediaPanel = null; _panelAutoHiddenAtUtc = null; };
            }
            PositionMediaPanel();
            // 背板抓屏必须在 Show 之前（此刻面板不可见 → 抓到的是胶囊上方那块桌面的干净内容）：
            // 显示之后再抓就会把面板自己已有的像素抓进背板（鬼影）。矩形用 Left/Top/Width/Height，
            // PositionMediaPanel 刚写完这四个属性，所以抓屏区与最终落位一致。
            _mediaPanel.RefreshBackdrop();
            _mediaPanel.Show();      // Show 默认激活 → Deactivated 自动收起链路生效
            _mediaPanel.Activate();
            _mediaPanel.ScrollToCurrentLine();
        }
        catch (Exception ex)
        {
            // 防御：媒体面板是附属功能，任何异常不得拖垮 Dock 本体
            _mediaPanel?.Close();
            _mediaPanel = null;
            Logger.Warn($"打开媒体面板失败: {ex}");
        }
    }

    /// <summary>媒体条目（恒为 suffix 末位，仍按 Kind 扫描以稳健）；无则 null。</summary>
    private DockItem? MediaItemOrNull()
    {
        for (var i = _vm.Items.Count - 1; i >= 0; i--)
            if (_vm.Items[i].Kind == DockItemKind.Media) return _vm.Items[i];
        return null;
    }

    /// <summary>面板置于胶囊上方并水平居中（钳制在<b>本 Dock 所在显示器</b>内）；胶囊中心经 PointToScreen
    /// （物理 px）→ DIP 换算。停靠副屏时钳制边界跟着走（副屏原点可为负数或 1920+，主屏假设会把面板
    /// 整个夹到错误的屏上去）。</summary>
    private void PositionMediaPanel()
    {
        const double panelW = 420, panelH = 520, edgeMargin = 8, gap = 6;
        var panel = _mediaPanel;
        if (panel is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1;

        double centerDipX = Left + Width / 2; // 兜底：Dock 中心
        var mediaItem = MediaItemOrNull();
        if (mediaItem is not null && Surface.GetItemBounds(mediaItem) is { } bounds && bounds.Width > 0)
        {
            // 放大后的胶囊矩形中心 → 屏幕物理 px → DIP（与 v1 同一路径，容器改由 DockSurface 提供）
            var screenPt = Surface.PointToScreen(new Point(bounds.X + bounds.Width / 2, bounds.Y));
            centerDipX = screenPt.X / dpi;
        }

        // 左右边界取 rcMonitor（几何唯一来源，v3 T1）；AppBar 尚未定位过时 _monitorRect 为默认零矩形，
        // 与 v2 的"主屏 0..SM_CXSCREEN"行为一致，不会更差。
        var monLeft = _monitorRect.Left / dpi;
        var monRight = _monitorRect.Right / dpi;
        var leftMin = monLeft + edgeMargin;
        var left = Math.Clamp(centerDipX - panelW / 2, leftMin, Math.Max(leftMin, monRight - panelW - edgeMargin));
        // 窗口顶部有动态的放大余量（供放大图标生长），面板真正的上沿在 Top + StripHeadroomDip()
        // （= 预留带上沿）：用它算间距，余量尺寸变化时面板与 Dock 的视觉间隙保持一致（v2 观感零变化）。
        var top = Math.Max(edgeMargin, Top + StripHeadroomDip() - panelH - gap);
        panel.Left = left;
        panel.Top = top;
    }
}
