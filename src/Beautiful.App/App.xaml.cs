using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.Services;
using Beautiful.App.Shell;
using Beautiful.App.Themes;
using Beautiful.App.TopBar;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Beautiful.App;

/// <summary>
/// WPF 应用壳层（Task 17）：完整启动流程与退出编排。
/// 启动：Logger → 全局异常兜底 → 单实例 Mutex（第二实例弹窗即退）→ SettingsService.Load →
/// 任务栏策略/自启落地 → DockWindow（构造期按设置应用主题；SMTC 经 MediaViewModel 构造期
/// fire-and-forget 初始化，失败仅记日志降级为无胶囊）→ 顶栏 TopBarWindow（TopBarEnabled，
/// Task 8；创建失败仅降级不连坐）→ --settings 打开设置窗口 → 托盘。
/// 策略落地至托盘就位整体 try/catch：失败还原任务栏/托盘后优雅退出，防僵尸进程。
/// ShutdownMode=OnExplicitShutdown：Dock 被 Alt+F4 等关闭后壳层仍驻留（托盘可重载/退出），
/// 退出只走托盘"退出"或显式 <see cref="Application.Shutdown"/>。
/// 退出链路（ExitApp）：关设置窗 → 关顶栏（ABE_TOP ABM_REMOVE）→ 关 Dock（ABM_REMOVE）
/// → TaskbarManager.Restore → 托盘 Dispose → 设置兜底落盘 → Shutdown；
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
    private TopBarWindow? _topBar;
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

        // 3. 单实例：互斥体已被持有 → 弹窗提示后退出
        if (!AcquireSingleInstanceMutex())
        {
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

            // 6. Dock 即主 UI：--minimized 仅标识自启来源（自启语义），Dock 照常显示，超出仅记录
            _dock = new DockWindow(_taskbar);
            _dock.Show();
            if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                Logger.Info("--minimized（开机自启）：Dock 即主 UI，照常显示");
            Logger.Info("Dock 已显示");

            // 6.5 顶栏（Task 8）：同一受保护序列内创建（失败回滚链路覆盖它）。但它是附属表面，
            // 创建异常仅记日志降级为"无顶栏运行"，不得连坐 Dock/任务栏策略整体退出。
            if (SettingsService.Current.TopBarEnabled) EnsureTopBar();

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
        var old = _dock;
        _dock = null;
        old.Close(); // 同步走完 OnClosing（ABM_REMOVE）/ OnClosed（tracker/smtc 释放）
        _dock = new DockWindow(_taskbar);
        _dock.Show();
        if (SettingsService.Current.TopBarEnabled) EnsureTopBar();
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

    /// <summary>把 SettingsService.Current.TopBarEnabled 应用到运行中的实例（创建/销毁，均幂等）。</summary>
    private void ApplyTopBarEnabled()
    {
        if (SettingsService.Current.TopBarEnabled) EnsureTopBar();
        else DestroyTopBar();
    }

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
            DestroyTopBar();         // 顶栏 OnClosing：ABE_TOP ABM_REMOVE（hwnd 销毁前）
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
