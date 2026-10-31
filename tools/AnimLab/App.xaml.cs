using System.IO;
using System.Windows;

namespace Beautiful.AnimLab;

public partial class App : Application
{
    /// <summary>
    /// alpha.16 加固：真机反馈"双击 AnimLab 没有任何窗口出现"。
    /// 旧包把 AnimLab 发布成 framework-dependent（依赖主包目录里的运行时——
    /// apphost 的框架解析走 runtimeconfig，扁平目录里解析不到 shared/ 结构，等于没运行时），
    /// 双击后 apphost 找不到框架直接退场。本包已改自包含发布（package-release.sh）。
    /// <para>
    /// alpha.18 加固：真机反馈"打开 AnimLab 报错：Exception has been thrown by the target of
    /// an invocation"。旧版用 StartupUri——它在 Application.DoStartup 里加载 BAML，构造异常被
    /// XAML 引擎包成 <c>TargetInvocationException</c>，弹窗与日志都只显示外层一句话，
    /// 真正的内部异常（是什么类型、在哪一行）谁也拿不到。改手动建窗口：
    /// 异常在这里被拿到完整内部链并全文落盘；窗口建不出来也弹一个带真实原因的错误窗，
    /// 绝不再"无声失败 + 一句谁也看不懂的话"。
    /// </para>
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
            WriteCrashLog(args.Exception);
            ShowCrashDialog(args.Exception);
            args.Handled = true;   // 调参台不该因一次异常整个退场
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog(ex);
        };

        base.OnStartup(e);

        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            ShowCrashDialog(ex);
            Shutdown(1);   // 窗口建不出来：不留无窗僵尸进程
        }
    }

    /// <summary>弹窗显示【最内层】异常——用户该看到真原因，不是反射包装层的话术。</summary>
    internal static void ShowCrashDialog(Exception ex)
    {
        var root = ex.GetBaseException();
        try
        {
            MessageBox.Show(
                $"AnimLab 遇到错误：\n{root.GetType().Name}: {root.Message}\n\n" +
                $"（完整异常链已写入 {CrashLogPath()}，反馈时请一并附上）",
                "AnimLab", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            /* 弹窗也不行就只剩日志了 */
        }
    }

    internal static string CrashLogPath() =>
        Path.Combine(AppContext.BaseDirectory, CrashLogName);

    /// <summary>完整落盘：ex.ToString() 自带全部内部异常链（旧版只写 ex.Message + 外层栈，内部异常全丢）。</summary>
    internal static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath(), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 日志写不进去（只读目录等）就真没有更多可做的了
        }
    }
}
