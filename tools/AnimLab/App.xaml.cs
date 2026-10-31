using System.IO;
using System.Windows;

namespace Beautiful.AnimLab;

public partial class App : Application
{
    /// <summary>
    /// alpha.16 加固：真机反馈"双击 AnimLab 没有任何窗口出现"。
    /// 旧包把 AnimLab 发布成 framework-dependent（依赖主包目录里的运行时——
    /// apphost 的框架解析走 runtimeconfig，扁平目录里解析不到 shared/ 结构，等于没运行时），
    /// 双击后 apphost 找不到框架直接退场。本包已改自包含发布（package-release.sh），
    /// 这里再加最后一道兜底：任何启动期异常都落盘 + 弹窗，绝不允许"无声失败"。
    /// </summary>
    private const string CrashLogName = "animlab-crash.log";

    protected override void OnStartup(StartupEventArgs e)
    {
        // --params <path>：主程序传参模式（高级用法，见测试清单 F4）
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--params", StringComparison.OrdinalIgnoreCase))
            {
                AppDomain.CurrentDomain.SetData("Beautiful.AnimLab.ParamsPath", e.Args[i + 1]);
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            try
            {
                MessageBox.Show($"AnimLab 遇到错误：\n{args.Exception.Message}\n\n详细见 {CrashLogPath()}",
                    "AnimLab", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* 弹窗也不行就只剩日志了 */ }
            args.Handled = true;   // 调参台不该因一次异常整个退场
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrash(ex);
        };

        base.OnStartup(e);
    }

    internal static string CrashLogPath() =>
        Path.Combine(AppContext.BaseDirectory, CrashLogName);

    private static void WriteCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath(),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // 日志写不进去（只读目录等）就真没有更多可做的了
        }
    }
}
