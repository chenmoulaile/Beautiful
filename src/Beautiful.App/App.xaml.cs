// UseWPF + UseWindowsForms 同时启用时，ImplicitUsings 会引入 System.Windows.Forms，
// 导致 Application 二义性（CS0104），此处用别名显式指向 WPF 的 Application。
using Application = System.Windows.Application;

namespace Beautiful.App;

/// <summary>
/// WPF 应用入口（Task 17 将移除 StartupUri 并接管启动流程）。
/// </summary>
public partial class App : Application
{
}
