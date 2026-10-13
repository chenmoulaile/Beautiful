// UseWPF + UseWindowsForms 同时启用时，ImplicitUsings 会引入 System.Windows.Forms，
// 导致 Application 二义性（CS0104），此处用别名显式指向 WPF 的 Application。
using System.Diagnostics;
using System.Windows;
using Beautiful.App.Services;
using Application = System.Windows.Application;

namespace Beautiful.App;

/// <summary>
/// WPF 应用入口。启动流程（Task 16）：加载设置 → DockWindow（构造期按设置应用主题）→
/// 应用任务栏策略 / 开机自启注册表 →（--settings 临时入口）打开设置窗口。
/// ShutdownMode=OnMainWindowClose：Dock 即主窗，关闭即退出（设置窗口随壳层一并结束）。
/// </summary>
public partial class App : Application
{
    private TaskbarManager? _taskbar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // TEMP(T15 验证)：Trace → stderr，live 冒烟时可在后台输出里看到媒体管线诊断；T17 接正式 Logger 后移除
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;

        SettingsService.Load();       // 先于任何 VM 构造（MediaViewModel/DockWindow 构造期读设置）
        _taskbar = new TaskbarManager();

        var dock = new Dock.DockWindow(_taskbar);
        dock.Show();

        // 任务栏策略与开机自启按设置落地（与 settings.json 对齐，注册表由 SetAutostart 维护）
        _taskbar.Apply(SettingsService.PolicyOf(SettingsService.Current));
        SettingsService.SetAutostart(SettingsService.Current.Autostart);

        // TEMP(T16)：--settings 命令行打开设置窗口；T17 托盘菜单接管正式入口
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            new Settings.SettingsWindow(_taskbar, dock.Vm, dock.MediaVm).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbar?.Restore(); // Hide/AutoHide 策略下退出必须还原系统任务栏
        base.OnExit(e);
    }
}
