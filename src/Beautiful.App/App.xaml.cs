using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.Interop;   // 退出信号要用 FindWindowByTitle / PostMessage / 注册消息号
using Beautiful.App.Services;
using Beautiful.App.Shell;
using Beautiful.App.Themes;
using Beautiful.App.TopBar;
using Beautiful.Core.Controls;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Beautiful.App;

/// <summary>
/// WPF 应用壳层（Task 17）：完整启动流程与退出编排。
/// 启动：Logger → 全局异常兜底 → 单实例 Mutex（第二实例弹窗即退）→ SettingsService.Load →
/// 任务栏策略/自启落地 → DockWindow（构造期按设置应用主题；SMTC 经 MediaViewModel 构造期
/// fire-and-forget 初始化，失败仅记日志降级为无胶囊）→ 顶栏 TopBarWindow（TopBarEnabled，
/// Task 8；创建失败仅降级不连坐）→ 切换器 SwitcherService + 可见面板 SwitcherWindow（Task 11/12，
/// 接管开关关闭时不装配面板 → Alt+Tab 回到系统原生）→ --settings 打开设置窗口 → 托盘。
/// 策略落地至托盘就位整体 try/catch：失败还原任务栏/托盘后优雅退出，防僵尸进程。
/// ShutdownMode=OnExplicitShutdown：Dock 被 Alt+F4 等关闭后壳层仍驻留（托盘可重载/退出），
/// 退出只走托盘"退出"或显式 <see cref="Application.Shutdown"/>。
/// 退出链路（ExitApp）：关设置窗 → 收切换面板 + 卸键盘钩子 → 关顶栏（ABE_TOP ABM_REMOVE）
/// → 关 Dock（ABM_REMOVE）→ TaskbarManager.Restore → 托盘 Dispose → 设置兜底落盘 → Shutdown；
/// OnExit 兜底重复清理（全部幂等）。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Beautiful.SingleInstance";
    private const int MaxExceptionDialogs = 5; // 界面异常弹窗上限：防异常循环导致弹窗风暴（超限仍记日志）

    private Mutex? _mutex;
    private bool _ownsMutex;
    private int _exceptionDialogs;
    private TaskbarManager? _taskbar;
    private DockWindow? _dock;

    /// <summary>最小化/最大化动画（v3.2）：持有两条 WinEvent 钩子，退出必须卸。</summary>
    private Animation.MinimizeAnimator? _animator;

    /// <summary>任务栏玻璃壳（v3.2 #6）：盖在原生任务栏上的点击穿透装饰层，只美化不接管。</summary>
    private Services.TaskbarGlassOverlay? _glass;
    private TopBarWindow? _topBar;
    private Switcher.SwitcherService? _switcher;
    private Switcher.SwitcherWindow? _switcherWindow;
    private Launcher.LauncherService? _launcher;
    private Settings.SettingsWindow? _settingsWindow;
    private TrayIconManager? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 日志最先初始化：后续每一步的成败都应有据可查
        Logger.Init();
        Logger.Info($"启动 args=[{string.Join(' ', e.Args)}]");

        // 2. 全局异常兜底先于任何业务代码
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 2.5 `--exit`：让**正在运行的那个实例**走完整退出链路，本实例发完信号就退。
        // 必须排在单实例检查之前——否则它会被当成"第二实例"直接弹窗退出，什么也没发出去。
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            var sent = RequestExitOfRunningInstance();
            Environment.Exit(sent ? 0 : 3);   // 3 = 没有找到在跑的实例（未安装/已退出）
            return;
        }

        // 3. 单实例：互斥体已被持有 → 带 --settings 就把请求转给在跑的实例，否则弹窗提示后退出
        if (!AcquireSingleInstanceMutex())
        {
            // 用户在跑着 Dock 的情况下双击/命令行再开一次带 --settings 的 exe，他要的是那个设置窗口，
            // 不是"已在运行"的模态提示。转发成功就静默退场；失败（没找到窗口）退回原来的提示，
            // 因为此时确实什么都没发生，不吭声只会让人以为双击没反应。
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)
                && PostToRunningInstance(NativeMethods.SettingsRequestMessage))
            {
                Logger.Info("第二实例：已把 --settings 转发给在跑的实例");
                Environment.Exit(0);
                return;
            }
            Logger.Warn("Beautiful 已在运行，第二实例退出");
            MessageBox.Show("Beautiful 已在运行", "Beautiful",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 4. 设置加载：先于任何 VM 构造（DockWindow/MediaViewModel 构造期读设置）
        SettingsService.Load();

        // 5-8. 任务栏策略/自启落地 → Dock 显示 → --settings → 托盘，整体 try/catch 自愈：
        // 该序列任何一步失败若被全局兜底吞掉（Handled=true）会留下"无 Dock 无托盘 + 任务栏被隐藏"
        // 的僵尸进程，故此处就地回滚（还原任务栏 + 释放托盘）后优雅关停。
        _taskbar = new TaskbarManager();
        try
        {
            _taskbar.Apply(SettingsService.PolicyOf(SettingsService.Current));
            SettingsService.SetAutostart(SettingsService.Current.Autostart);

            // 6.2 最小化/最大化动画（v3.2）：先于 Dock 构造并注入——Dock 触发的最小化要"趁窗口还
            // 画着先抓一帧"，事件路径到我们手里时窗口已经 iconic。落点回调查询 _dock 字段是惰性的，
            // 此刻为 null 没关系。它是纯装饰的附属能力，装不上只记日志，绝不连坐主 UI（与顶栏同一处置）。
            if (SettingsService.Current.MinimizeAnimation)
            {
                try
                {
                    _animator = new Animation.MinimizeAnimator(hwnd => _dock?.IconRectPhysical(hwnd));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"最小化动画未启用：{ex.GetType().Name} {ex.Message}");
                }
            }

            // 6. Dock 即主 UI：--minimized 仅标识自启来源（自启语义），Dock 照常显示，超出仅记录
            _dock = new DockWindow(_taskbar, _animator);
            _dock.Show();

            // 6.3 任务栏玻璃壳（v3.2 #6）：必须在任务栏策略落地之后建，否则会给一条已被推出的任务栏化妆
            ApplyGlassState(SettingsService.Current);

            if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                Logger.Info("--minimized（开机自启）：Dock 即主 UI，照常显示");
            Logger.Info("Dock 已显示");

            // 6.5 顶栏（Task 8）：同一受保护序列内创建（失败回滚链路覆盖它）。但它是附属表面，
            // 创建异常仅记日志降级为"无顶栏运行"，不得连坐 Dock/任务栏策略整体退出。
            if (SettingsService.Current.TopBarEnabled) EnsureTopBar();

            // 6.6 切换器（Task 11）：Alt+Tab 接管。同样按"附属能力"处理——钩子装不上只是退回原生
            // Alt+Tab，绝不能连坐主 UI；但退出时必须先卸钩（见 ExitApp/OnExit）。
            // 启动器热键<b>共用</b>这一条 WH_KEYBOARD_LL（钩子回调里两条分支各判各的组合键，见 AltTabHook），
            // 因此装配顺序是"先切器、后启动器"：切器一侧按 KeepHookForLauncher 决定钩子装不装，
            // 启动器只接事件，绝不自己再装第二个全局钩子。
            EnsureSwitcher();

            // 6.7 启动器（v3 T7）：Spotlight 式搜索面板。索引在后台线程扫开始菜单，UI 不等待它。
            EnsureLauncher();

            // 6.8 壁纸取色（v3 T7）：这个开关开着时启动算一次（解码在后台线程，不阻塞启动序）。
            // 关着就什么都不做——"关掉时主题观感与取色功能上线前完全一致"是这条功能的验收点。
            // 取不到色由 WallpaperAccentService 内部回落主题色并留原因，这里不需要再包一层 try。
            if (SettingsService.Current.UseWallpaperAccent)
                _ = WallpaperAccentService.RefreshAsync(true);

            // 7. --settings：设置窗口命令行入口（与托盘"设置"等效；窗口为单例）
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
                OpenSettings();

            // 8. 托盘最后就位（SMTC 已随 Dock 构造期异步初始化，失败仅记日志）
            _tray = new TrayIconManager(
                openSettings: OpenSettings,
                reloadDock: ReloadDock,
                exit: ExitApp,
                applyTheme: ApplyThemeFromTray,
                currentTheme: () => SettingsService.Current.Theme);
            Logger.Info("托盘就绪，启动完成");
        }
        catch (Exception ex)
        {
            Logger.Error("启动失败：回滚任务栏策略并退出（防僵尸进程）", ex);
            if (_tray is not null)
            {
                _tray.Dispose();
                _tray = null;
            }
            _taskbar?.Restore(); // 隐藏策略已落地时必须还原（OnExit 兜底重复调用，幂等）
            Shutdown();          // Dock 已创建时随关停走 OnClosing/OnClosed 清理链路
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底路径（系统会话结束、显式 Shutdown 等）也要清理；与 ExitApp 重复调用均幂等
        _tray?.Dispose();
        _tray = null;
        _animator?.Dispose();   // WinEvent 钩子必须卸载（幂等）
        _animator = null;
        _glass?.Dispose();      // 拆掉盖在原生任务栏上的装饰层（幂等）
        _glass = null;
        DestroyLauncher();  // 覆盖层是瞬态弹层：兜底路径也要收（Dispose 幂等，ExitApp 已走过一次则 no-op）
        DestroySwitcher(); // 键盘钩子必须卸载（幂等）：残留的 WH_KEYBOARD_LL 会在进程退出时由系统回收，
                           // 但显式 Shutdown 而未走 ExitApp 的路径若卡在非 UI 线程，卸载会失败 → 这里至少留日志
        _taskbar?.Restore(); // Hide/AutoHide 策略下退出必须还原系统任务栏（T16 起保留）
        ReleaseMutex();
        Logger.Info($"退出完成 (ExitCode={e.ApplicationExitCode})");
        base.OnExit(e);
    }

    // ---------- 单实例 ----------

    /// <summary>WaitOne(0) 探测互斥体：false=已有实例；AbandonedMutexException=前实例崩溃遗留，视为可获得。</summary>
    private bool AcquireSingleInstanceMutex()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true; // 抛出该异常即代表所有权已转移给本线程
            return true;
        }
    }

    private void ReleaseMutex()
    {
        try
        {
            if (_ownsMutex) _mutex?.ReleaseMutex();
        }
        catch { /* 所有权竞态等：进程即将终止，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }

    // ---------- 托盘动作 ----------

    /// <summary>打开设置窗口（单例：已打开则激活前置）。</summary>
    private void OpenSettings()
    {
        if (_dock is null || _taskbar is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        var win = new Settings.SettingsWindow(_taskbar, _dock.Vm, _dock.MediaVm);
        win.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = win;
        win.Show();
        Logger.Info("设置窗口已打开");
    }

    /// <summary>托盘主题子菜单：与设置窗口同一落地路径（UpdateAndSave → ThemeManager.Apply）。</summary>
    private void ApplyThemeFromTray(string theme)
    {
        Logger.Info($"主题切换（托盘）: {theme}");
        SettingsService.UpdateAndSave(SettingsService.Current with { Theme = theme });
        ThemeManager.Apply(SettingsService.ThemeDefinitionOf(SettingsService.Current), Application.Current);
    }

    /// <summary>重载 Dock：旧实例完整释放（ABM_REMOVE + WindowTracker/SmtcService 于 OnClosed Dispose）后
    /// 新建。先关后开：AppBar 底边条带先归还再申请，避免双 AppBar 同边竞争导致新 Dock 被顶高。
    /// 顶栏随 Dock 一并重建（brief 接口：App 持有其生命周期，重载即按当前设置重放）。
    /// 设置窗口持有旧 Dock 的 VM 引用，一并关闭（下次打开自动基于新 Dock 重建）。</summary>
    private void ReloadDock()
    {
        if (_dock is null || _exiting) return;
        Logger.Info("重载 Dock：开始");
        _settingsWindow?.Close();
        DestroyTopBar(); // 先归还顶部条带再重建（同"先关后开"理由）
        // 切换面板随 Dock 一并重建：背板 tint 只在 HWND 初始化时读一次主题，重载（含换主题）后
        // 必须换新实例才能拿到新的 tint；同时保证重载瞬间屏幕上不留任何旧面板。
        DestroySwitcherWindow();
        var old = _dock;
        _dock = null;
        old.Close(); // 同步走完 OnClosing（ABM_REMOVE）/ OnClosed（tracker/smtc 释放）
        DockWindow? dock = null; // 提升出 try：Show() 半途抛时 catch 需 Close 半构造实例走 OnClosed 解绑（同 EnsureTopBar 纪律）
        try
        {
            // 【终审 I-6】重建必须有就地兜底：此刻旧 Dock/顶栏/面板都已销毁，若 new DockWindow
            // 抛异常且无人接住，异常落进 DispatcherUnhandledException（Handled=true）后 _dock 恒为
            // null——常驻进程只剩托盘图标，而"重载"（本方法 return）与"设置"（依赖 _dock 的 VM）
            // 全部静默 no-op，用户没有任何恢复手段。catch 后至少恢复顶栏与切换面板这两个不依赖
            // Dock 的表面，保证屏幕上有可用入口。
            dock = new DockWindow(_taskbar);
            dock.Show();
            _dock = dock;
            if (SettingsService.Current.TopBarEnabled) EnsureTopBar();
            EnsureSwitcherWindow();
        }
        catch (Exception ex)
        {
            _dock = null;
            // Show() 已走完 OnSourceInitialized（AppBar 已注册、ThemeChanged 已订阅）而其后抛异常时，
            // 引用即将丢弃——必须 Close 走 OnClosing/OnClosed（ABM_REMOVE + 解绑），否则条带与静态订阅泄漏。
            try { dock?.Close(); } catch { /* 自愈路径：Close 失败不掩盖原始异常 */ }
            Logger.Error("重载 Dock 失败：新实例创建异常，旧 Dock 已释放。正在恢复顶栏/切换面板以保留可用界面（可再次重载或退出）", ex);
            try { if (SettingsService.Current.TopBarEnabled) EnsureTopBar(); } catch { /* 逐表面尽力恢复，单点失败不再连坐 */ }
            try { EnsureSwitcherWindow(); } catch { /* 同上 */ }
            if (_topBar is null && _switcherWindow is null)
                MessageBox.Show("Beautiful 重载界面失败，屏幕已无可操作窗口。\n请从系统托盘图标再次“重载”或“退出”，详情见 logs 日志。",
                    "Beautiful", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Logger.Info("重载 Dock：完成");
    }

    // ---------- 顶栏生命周期（Task 8） ----------

    /// <summary>按设置确保顶栏存在（幂等）。创建/显示失败仅记日志降级——顶栏是附属表面，
    /// 不得拖垮 Dock 与任务栏策略（与启动序整体回滚区分：那条保护的是主 UI 链路）。</summary>
    private void EnsureTopBar()
    {
        if (_topBar is not null || _exiting) return;
        TopBarWindow? bar = null; // 提升出 try：catch 需能 Close 已创建实例（Task 8 泄漏修复，见下）
        try
        {
            bar = new TopBarWindow(BuildTopBarContext());
            bar.Show();
            _topBar = bar;
            Logger.Info("顶栏已显示");
        }
        catch (Exception ex)
        {
            _topBar = null;
            // Show() 已完成 OnSourceInitialized（ABE_TOP 条带已申请、ThemeChanged 已订阅、WinEventHook/定时器已装），
            // 但若其后 ApplyTheme/StartForegroundTracking 抛异常，句柄即将随局部变量丢弃——必须 Close() 走 OnClosing
            // （ABM_REMOVE + 停 timer + 解 hook + 解订阅），否则顶部条带永久预留且静态订阅泄漏。Close 幂等且异常安全。
            try { bar?.Close(); } catch { /* 降级路径：Close 失败不掩盖原始异常 */ }
            Logger.Warn($"顶栏创建失败（降级为无顶栏运行）: {ex.Message}");
        }
    }

    /// <summary>组合根：顶栏依赖注入（窗口跟踪器/媒体 VM/系统动作回调均转接到当前 Dock 与 App 生命周期方法）。
    /// 用闭包取 _dock 现值——重载 Dock 后旧顶栏已随 ReloadDock 销毁重建，捕获的始终是当时的 Dock。</summary>
    private TopBarContext BuildTopBarContext() => new()
    {
        Tracker = () => _dock?.Tracker,
        Media = () => _dock?.MediaVm,
        OpenMediaPanel = () => _dock?.OpenMediaPanelFromTopBar(),
        OpenSettings = OpenSettings,
        OpenLauncher = () => _launcher?.Toggle(),
        ReloadDock = ReloadDock,
        Exit = ExitApp,
        ApplyTheme = ApplyThemeFromTray,
        CurrentTheme = () => SettingsService.Current.Theme,
    };

    /// <summary>销毁顶栏并归还 ABE_TOP 条带（幂等；OnClosing 内 ABM_REMOVE）。</summary>
    private void DestroyTopBar()
    {
        if (_topBar is null) return;
        var old = _topBar;
        _topBar = null;
        old.Close();
        Logger.Info("顶栏已销毁（条带已归还）");
    }

    /// <summary>设置页开关的即时生效入口（Task 16 设置 UI 接线用）：落盘 + 显示/隐藏，无需重启。</summary>
    public static void SetTopBarEnabled(bool enabled)
    {
        SettingsService.UpdateAndSave(SettingsService.Current with { TopBarEnabled = enabled });
        (Current as App)?.ApplyTopBarEnabled();
    }

    /// <summary>
    /// 设置页改动 DockIconSize / MagnificationStrength / ReflectionEnabled 后的即时生效入口（Task 16）。
    /// 值的写入与规范化由调用方（SettingsService.Update / UpdateAndSave）负责，本方法只把当前设置
    /// 重新落到运行中的 Dock（重排条目几何 + 覆盖图标尺寸遮罩 + 按需重排条带），无需重启。
    /// Dock 尚未创建时静默跳过（下次创建时 ApplyTheme 会读取同一设置）。
    /// </summary>
    public static void ApplyDockLayout() => (Current as App)?._dock?.RefreshLayoutFromSettings();

    /// <summary>
    /// 设置页改动 <c>TopBarMonitor</c> 后的即时生效入口（v3 T1）：按新的目标显示器重新协商 ABE_TOP
    /// 条带并把顶栏窗口搬过去。顶栏未启用/未创建时静默跳过（下次创建时 OnSourceInitialized 读同一设置）。
    /// </summary>
    public static void ApplyTopBarGeometry() => (Current as App)?._topBar?.RefreshLayoutFromSettings();

    /// <summary>
    /// 把一扇外部窗口带到前台（Dock 右键菜单"切到该窗口"用，v3.1）。
    /// 【为什么开这个口子】前台切换在这台机器上是条多步链路（SetForegroundWindow → 前台锁被压制时
    /// AttachThreadInput → 再不行合成一次 Alt 抬起），已经在切换器里被真机打磨过一份。
    /// Dock 菜单再写一条 SetForegroundWindow 就是第二条激活路径——本仓库栽过的"两条路径各自漂移"
    /// 正是这类重复。这里只把切换器那次尝试借出去，返回它自己的成败判定。
    /// 切换器未装配（启动降级/重载窗口那一刻）时返回 false，由调用方退回 <c>WindowTracker</c> 的既有路径。
    /// </summary>
    public static bool CommitWindowForeground(IntPtr hwnd) => (Current as App)?._switcher?.Commit(hwnd) ?? false;

    /// <summary>
    /// 设置页改动<b>顶栏时钟形态</b>（秒/日期/星期/12 小时制）后的即时生效入口（v3.1 缺陷 3）：
    /// 重算文本并按新形态重排定时器步长（显示秒=对齐秒边界，关秒=对齐分边界）。
    /// 顶栏未启用/未创建时静默跳过——下次创建走 OnSourceInitialized 里的同一个入口读当前设置。
    /// </summary>
    public static void ApplyTopBarClock() => (Current as App)?._topBar?.ApplyClockSettings();

    /// <summary>设置页切换 PreviewEnabled 后的即时生效入口：关闭时立即收起正在显示的预览
    /// （开启无需动作——下一次悬停按新值放行）。让该开关不再是死控件。</summary>
    public static void ApplyPreviewEnabled() => (Current as App)?._dock?.ApplyPreviewSetting();

    // ---------- 控制中心的落地入口（v3 T7） ----------
    //
    // 【为什么是 App 的静态入口而不是面板自己接线】这些开关的"真值 + 落地目标"都只在 App 手里
    //（TaskbarManager 实例、Dock 的媒体 VM），而设置窗口早已用同一形状（SetTopBarEnabled /
    // SetSwitcherEnabled / ApplyDockLayout）。控制中心与设置页因此共用一条写入路径：
    // 值只经 QuickSettings 的纯函数折算一次（有单测），落盘只经 SettingsService.UpdateAndSave 一次，
    // 不会出现"从控制中心改的和从设置页改的行为不一样"。

    /// <summary>
    /// 系统任务栏策略：落盘 + <see cref="TaskbarManager.Apply"/>。
    /// Dock 尚未创建/正在退出时策略照样落盘（下次创建时按 Current 重放），不静默丢弃用户的选择。
    /// </summary>
    public static void SetTaskbarPolicy(string policyId)
    {
        SettingsService.UpdateAndSave(QuickSettings.WithTaskbarPolicy(SettingsService.Current, policyId));
        var app = Current as App;
        app?._taskbar?.Apply(SettingsService.PolicyOf(SettingsService.Current));
        // 策略一换，玻璃壳的"有没有可装饰的任务栏"就变了：切到隐藏必须把装饰一起拆掉
        app?.ApplyGlassState(SettingsService.Current);
    }

    /// <summary>悬停放大开关：强度在"下限 ×1.0（关）"与"默认倍率（开）"之间切换，落地走 Dock 布局重放。</summary>
    public static void SetMagnification(bool on)
    {
        SettingsService.UpdateAndSave(QuickSettings.WithMagnification(SettingsService.Current, on));
        ApplyDockLayout();
    }

    /// <summary>倒影开关：三态设置被这一次点击落成显式值，此后切主题不再冲掉（见 AppSettings.ReflectionEnabled）。</summary>
    public static void SetReflection(bool on)
    {
        SettingsService.UpdateAndSave(QuickSettings.WithReflection(SettingsService.Current, on));
        ApplyDockLayout();
    }

    /// <summary>媒体胶囊开关：落盘 + 即时改 Dock 媒体 VM 的可见旗标（无 SMTC 会话时胶囊本来就隐藏）。</summary>
    public static void SetMediaCapsule(bool on)
    {
        SettingsService.UpdateAndSave(QuickSettings.WithMediaCapsule(SettingsService.Current, on));
        var dock = (Current as App)?._dock;
        if (dock is not null) dock.MediaVm.CapsuleVisible = on;
    }

    /// <summary>
    /// 壁纸取色开关（v3 T7）：落盘 + 立即重放一次取色。设置页与控制中心共用这一个入口
    /// （与放大/倒影/媒体胶囊同一形状：值只经 QuickSettings 折算一次，落地只经这里一次）。
    /// </summary>
    public static void SetWallpaperAccent(bool on)
    {
        SettingsService.UpdateAndSave(QuickSettings.WithWallpaperAccent(SettingsService.Current, on));
        _ = WallpaperAccentService.RefreshAsync(on);
    }

    /// <summary>
    /// 玻璃壳开关（v3.2 #6）：写设置 + 建/拆装饰层。策略为"隐藏"时不建层
    /// （没有可装饰的任务栏，留一条悬空高光比不画更难看），砖块那边已经把这个前提置灰并写明原因。
    /// </summary>
    public static void SetTaskbarGlass(bool on)
    {
        var next = QuickSettings.WithTaskbarGlass(SettingsService.Current, on);
        SettingsService.UpdateAndSave(next);
        (Current as App)?.ApplyGlassState(next);
    }

    /// <summary>按现值决定玻璃壳在不在。策略变化（隐藏↔保留）也要走这里，否则会出现"任务栏没了、装饰还在"。</summary>
    private void ApplyGlassState(Core.Settings.AppSettings s)
    {
        var want = s.TaskbarGlass && QuickSettings.NativeTaskbarVisible(s);
        if (want && _glass is null)
        {
            try
            {
                _glass = new Services.TaskbarGlassOverlay();
            }
            catch (Exception ex)
            {
                // 装饰层是附属能力，装不上只记日志，绝不连坐主 UI
                Logger.Warn($"任务栏玻璃壳未启用：{ex.GetType().Name} {ex.Message}");
                _glass = null;
            }
        }
        else if (!want && _glass is not null)
        {
            _glass.Dispose();
            _glass = null;
            Logger.Info("任务栏玻璃壳已卸载");
        }
        else if (want)
        {
            _glass?.Refresh();
        }
    }


    // ---------- 切换器生命周期（Task 11） ----------

    /// <summary>
    /// 确保切换器服务与可见面板存在（幂等）。服务始终启动以预热 MRU 列表与图标缓存——运行中
    /// 打开接管开关即刻有完整列表，不必在按键路径上冷启动 EnumWindows/磁盘 IO。
    /// 如实说明：顶栏"窗口"菜单用的是 Dock 的 WindowTracker，**并不复用**本服务的 MRU，
    /// 因此进程内存在两条各 2s 的 EnumWindows 对账（形态相同、快照独立，暂未合并）。
    /// 是否吞 Alt+Tab 由 <c>SwitcherEnabled</c> 决定；钩子装不上仅记日志（退回原生行为）。
    /// 面板（Task 12）只在开关打开时装配：关掉接管就没有事件源，面板自然不显示，
    /// 但**不创建**更干净——少一个常驻 HWND，也不会因异常路径留下可见残留。
    /// </summary>
    private void EnsureSwitcher()
    {
        if (_switcher is not null || _exiting) return;
        Switcher.SwitcherService? svc = null; // 提升出 try：catch 必须能 Dispose 半启动实例（见下）
        try
        {
            svc = new Switcher.SwitcherService();
            svc.Hook.EmergencyDisabled += OnSwitcherEmergencyDisabled;
            // 启动器热键与 Alt+Tab **共用这条钩子**（AltTabHook 里两条分支各判各的组合键）。
            // 事件接在组合根而不是让 LauncherService 自己去摸切换器：装/拆都在这个方法里看得见，
            // DestroySwitcher 里有对称的一句解绑（本仓库的长命事件泄漏教训都栽在"不对称"上）。
            svc.Hook.LauncherRequested += OnLauncherHotkey;
            // 关掉 Alt+Tab 接管不等于可以摘钩：启动器还靠它收键。保活旗标必须在 Start 之前就位，
            // 否则"接管关、启动器开"的机器上钩子压根不会装（热键成了死配置）。
            svc.KeepHookForLauncher = SettingsService.Current.LauncherEnabled;
            _switcher = svc;
            svc.Start(SettingsService.Current.SwitcherEnabled);
            EnsureSwitcherWindow();
        }
        catch (Exception ex)
        {
            _switcher = null;
            // Start() 半途而废时 svc 已注册进静态 ForegroundWatcher.Handlers、2s 定时器已被 Dispatcher
            // 持根，且若异常发生在 Hook.Start() 之后还挂着一条无主的 WH_KEYBOARD_LL（继续吞 Alt+Tab）。
            // 只把字段置 null 等于制造一个永远无人 Dispose 的僵尸——必须先释放再丢弃（评审发现 I3）。
            try { svc?.Dispose(); } catch { /* 释放失败不掩盖原始异常 */ }
            Logger.Warn($"切换器启动失败（Alt+Tab 保持系统原生行为）: {ex.Message}");
        }
    }

    /// <summary>
    /// 装配可见切换面板（幂等）。面板构造时不创建 HWND、不显示——首次 OpenRequested 才 Show，
    /// 因此常驻成本只是一个托管对象；关闭接管开关时不装配（原生 Alt+Tab 与原生观感一致）。
    /// </summary>
    private void EnsureSwitcherWindow()
    {
        if (_switcherWindow is not null || _switcher is null || _exiting) return;
        if (!SettingsService.Current.SwitcherEnabled) return;
        try
        {
            _switcherWindow = new Switcher.SwitcherWindow(_switcher);
            Logger.Info($"切换面板已装配（样式={SettingsService.Current.SwitcherStyle}）");
        }
        catch (Exception ex)
        {
            _switcherWindow = null;
            // 面板装不上只是回到"无界面切换"（Task 11 的状态），Alt+Tab 仍然可用；不得连坐服务
            Logger.Warn($"切换面板装配失败（Alt+Tab 仍可用，但无可见界面）: {ex.Message}");
        }
    }

    /// <summary>销毁面板（幂等）：先隐藏再 Close，Close 走 OnClosed 断开对服务与钩子的订阅。</summary>
    private void DestroySwitcherWindow()
    {
        if (_switcherWindow is null) return;
        var old = _switcherWindow;
        _switcherWindow = null;
        try { old.ForceClose(); } catch { /* 隐藏失败仍要 Close */ }
        try { old.Close(); } catch (Exception ex) { Logger.Warn($"切换面板关闭失败: {ex.Message}"); }
    }

    /// <summary>销毁切换器：先收面板（不留残影）→ 卸钩 + 停跟踪（幂等）。必须在窗口关闭链路里最先执行，
    /// 保证进程退出后不再有任何键盘拦截残留。</summary>
    private void DestroySwitcher()
    {
        DestroySwitcherWindow();
        if (_switcher is null) return;
        var old = _switcher;
        _switcher = null;
        try { old.Hook.EmergencyDisabled -= OnSwitcherEmergencyDisabled; } catch { }
        try { old.Hook.LauncherRequested -= OnLauncherHotkey; } catch { }
        old.Dispose();
        Logger.Info("切换器已销毁（键盘钩子已卸载、面板已关闭）");
    }

    // ---------- 启动器生命周期（v3 T7） ----------

    /// <summary>
    /// 确保启动器服务存在并预热索引（幂等）。与切器同档"附属能力"：装配失败只是没有启动器，
    /// 绝不连坐 Dock/顶栏。热键本身挂在切器那条钩子上（见 <see cref="EnsureSwitcher"/>），
    /// 所以这里失败时钩子照常工作，只是按下去没人接。
    /// </summary>
    private void EnsureLauncher()
    {
        if (_launcher is not null || _exiting) return;
        if (!SettingsService.Current.LauncherEnabled)
        {
            Logger.Info("启动器：设置里已关闭，本次运行不装配");
            return;
        }
        try
        {
            _launcher = new Launcher.LauncherService();
            _launcher.PrewarmIndex();
            Logger.Info($"启动器已装配（热键={SettingsService.Current.LauncherHotkey}）");
        }
        catch (Exception ex)
        {
            _launcher = null;
            Logger.Warn($"启动器装配失败（顶栏入口与热键都将无响应）: {ex.Message}");
        }
    }

    /// <summary>热键回调（已由钩子投递到 UI 线程）：开/收两相。启动器被关掉时这一句就是 no-op。</summary>
    private void OnLauncherHotkey() => _launcher?.Toggle();

    /// <summary>销毁启动器（幂等）：先收面板再释放服务。</summary>
    private void DestroyLauncher()
    {
        if (_launcher is null) return;
        var old = _launcher;
        _launcher = null;
        try { old.Dispose(); }
        catch (Exception ex) { Logger.Warn($"启动器销毁异常: {ex.Message}"); }
    }

    /// <summary>
    /// 设置页"启用启动器"的即时生效入口：落盘 + 重定钩子保活策略 + 关掉时收面板。
    /// <b>关功能即收界面</b>：留一个已经禁用的功能的面板在屏幕上，是本项目最忌的"假状态"。
    /// </summary>
    public static void SetLauncherEnabled(bool enabled)
    {
        SettingsService.UpdateAndSave(SettingsService.Current with { LauncherEnabled = enabled });
        var app = Current as App;
        // 关掉功能要**销毁服务**而不是只收面板：只 Close() 的话 _launcher 还活着，
        // 于是顶栏那颗按钮与（若钩子仍在）热键都还能把"已关闭的功能"打开——正是本项目最忌的假状态。
        // 重新打开时下面的 EnsureLauncher 会重建并按当前设置预热索引。
        if (!enabled) app?.DestroyLauncher();
        if (app?._switcher is { } svc)
        {
            svc.KeepHookForLauncher = enabled;
            // 装/摘在这里统一决定：接管关、启动器开 → 钩子仍需在线（Enabled=false 不吞键，只投递热键）；
            // 两个都关 → SetHookEnabled 内部会 StopHook 摘干净。
            svc.SetHookEnabled(SettingsService.Current.SwitcherEnabled);
        }
        if (enabled) app?.EnsureLauncher();   // 从关到开时补装配（此前被关过就没建过服务）
    }

    /// <summary>
    /// 设置页改启动器热键的即时生效入口：与 <see cref="ApplySwitcherHotkey"/> 同一条注册/自愈路径
    /// （落盘 → 卸钩 → 重装），改完不需要重启也不需要重载 Dock。
    /// </summary>
    public static void ApplyLauncherHotkey(string hotkey)
    {
        SettingsService.UpdateAndSave(SettingsService.Current with { LauncherHotkey = hotkey });
        var app = Current as App;
        app?._switcher?.StopHook();
        app?._switcher?.SetHookEnabled(SettingsService.Current.SwitcherEnabled);
    }

    /// <summary>
    /// 应急停用（Win+Alt+K）落地：只关运行态、**不改用户设置**——
    /// 把磁盘上的开关顺手写死会让用户以为"设置里明明开着却不生效"，排查更难。
    /// 钩子已由 AltTabHook 自行请求卸载，这里只做状态同步与指引日志。
    /// </summary>
    private void OnSwitcherEmergencyDisabled()
    {
        // 应急停用的语义是"这条钩子立刻下线"。SetHookEnabled 现在会按 KeepHookForLauncher 决定
        // 钩子是否留在场上（启动器热键共用它），若不先把保活旗标落下，这里就会在卸钩之后
        // **当场把钩子装回去**，应急出口失效。用户按 Win+Alt+K 时要的是"全都回到系统原生"。
        if (_switcher is { } svc) svc.KeepHookForLauncher = false;
        _switcher?.SetHookEnabled(false);
        // 钩子被摘时若面板正显示着，就再不会有人发 Commit/Cancel —— 必须就地收起（不提交，不切窗口）
        _switcherWindow?.ForceClose();
        Logger.Warn("切换器已应急停用：如需重新启用，请在设置中把“窗口切换器”开关**关一次再开**（重载 Dock 不会重挂钩子），或重启程序");
    }

    /// <summary>
    /// 设置页切换器开关的即时生效入口（Task 16 接线用）：落盘 + 装/摘钩 + 装配/收起面板，无需重启。
    /// </summary>
    public static void SetSwitcherEnabled(bool enabled)
    {
        SettingsService.UpdateAndSave(SettingsService.Current with { SwitcherEnabled = enabled });
        var app = Current as App;
        app?._switcher?.SetHookEnabled(enabled);
        if (enabled) app?.EnsureSwitcherWindow();
        else app?.DestroySwitcherWindow(); // 关接管即收起面板：不留任何自绘界面在屏幕上
    }

    /// <summary>
    /// 设置页热键/全屏直通变更的即时生效入口（Task 16 接线用）：落盘 + 重注册触发组合。
    /// 钩子在线时按新热键重装（改的是回调内的修饰键要求，不重装钩子本身也能生效，
    /// 但重装可顺带清掉左右键状态位，避免改键瞬间的会话残留）。
    /// </summary>
    public static void ApplySwitcherHotkey(string hotkey, bool fullscreenPassthrough)
    {
        SettingsService.UpdateAndSave(SettingsService.Current with
        {
            SwitcherHotkey = hotkey,
            FullscreenPassthrough = fullscreenPassthrough
        });
        var app = Current as App;
        app?._switcher?.StopHook();
        app?._switcher?.SetHookEnabled(SettingsService.Current.SwitcherEnabled);
    }

    /// <summary>把 SettingsService.Current.TopBarEnabled 应用到运行中的实例（创建/销毁，均幂等）。</summary>
    private void ApplyTopBarEnabled()
    {
        if (SettingsService.Current.TopBarEnabled) EnsureTopBar();
        else DestroyTopBar();
    }

    /// <summary>
    /// 向在跑的实例发一条注册消息：按标题找它的常驻顶层窗口。
    /// 先找 Dock 再退回顶栏——Dock 是主 UI，只要应用在跑它必然存在；顶栏是用户可关的设置项，
    /// 只认顶栏会让"关掉顶栏"的用户失去 <c>--exit</c> 这条通道（安装器就会在文件被占用时硬装）。
    /// </summary>
    private static bool PostToRunningInstance(ushort message)
    {
        if (message == 0) return false;   // 注册消息号拿不到 = 没有这条通道
        foreach (var title in new[] { "Beautiful Dock", "Beautiful TopBar" })
        {
            var hwnd = NativeMethods.FindWindowByTitle(title);
            if (hwnd != IntPtr.Zero && NativeMethods.PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero))
                return true;
        }
        return false;
    }

    private static bool RequestExitOfRunningInstance() => PostToRunningInstance(NativeMethods.ExitRequestMessage);

    /// <summary>外部（本进程的另一个窗口 / 第二个 exe 实例）请求干净退出。日志由调用点发，避免两处各写一份。</summary>
    internal static void ExitFromExternal() => (Current as App)?.ExitApp();

    /// <summary>外部请求打开设置窗口。<see cref="OpenSettings"/> 自己是单例的，重复请求只会激活。</summary>
    internal static void OpenSettingsFromExternal() => (Current as App)?.OpenSettings();

    /// <summary>托盘"退出"：完整清理链路后显式关停（退出路径幂等，_exiting 防重入）。
    /// 清理全程 try/finally：任何一步抛异常都必须走到 Shutdown（T19 QA）——否则 _exiting 卡死、
    /// 后续退出尝试全部被吞；finally 兜底后 OnExit 仍会幂等重放剩余清理。</summary>
    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Logger.Info("退出：开始清理");
        try
        {
            _settingsWindow?.Close();
            DestroyLauncher();           // 先收覆盖层：它和控制中心同为瞬态弹层，退出流程里不该还有窗口在屏幕上
            DestroySwitcher();         // 先收面板 + 卸键盘钩子：退出过程中任何一次 Alt+Tab 都不该被吞，
                                       // 也绝不能在屏幕上留下一块永不消失的切换面板（Task 12）
            DestroyTopBar();           // 顶栏 OnClosing：ABE_TOP ABM_REMOVE（hwnd 销毁前）
            _dock?.Close();          // DockWindow.OnClosing：AppBar ABM_REMOVE（hwnd 销毁前）
            _taskbar?.Restore();     // Hide/AutoHide 还原系统任务栏
            _tray?.Dispose();        // 移除托盘图标（防幽灵图标）
            _tray = null;
            SettingsService.Save();  // 兜底落盘（防抖尾写/未走设置窗口的变更一并刷盘）
        }
        finally
        {
            Shutdown();
        }
    }

    // ---------- 全局异常兜底 ----------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI 线程未处理异常", e.Exception);
        e.Handled = true; // 尽力存活（真正致命的场景仍可经托盘"退出"干净收尾）
        if (++_exceptionDialogs <= MaxExceptionDialogs)
            MessageBox.Show(
                $"发生界面异常，Beautiful 将继续运行。\n详情见 logs 目录日志。\n\n{e.Exception.Message}",
                "Beautiful", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var detail = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "null");
        Logger.Error($"非 UI 线程未处理异常 (IsTerminating={e.IsTerminating})", detail);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("未观察的任务异常（已吞掉，不影响运行）", e.Exception);
        e.SetObserved();
    }
}
