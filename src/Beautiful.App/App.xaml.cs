// UseWPF + UseWindowsForms 同时启用时，ImplicitUsings 会引入 System.Windows.Forms，
// 导致 Application 二义性（CS0104），此处用别名显式指向 WPF 的 Application。
using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;

namespace Beautiful.App;

/// <summary>
/// WPF 应用入口。临时启动：仅显示 DockWindow（Task 17 将移除并正式接管启动流程）。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // TEMP(T15 验证)：Trace → stderr，live 冒烟时可在后台输出里看到媒体管线诊断；T17 接正式 Logger 后移除
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;
        new Dock.DockWindow().Show();
    }
}
