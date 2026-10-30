using System;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.Interop;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// Dock 进度条的数据源（Phase 7-T1）：白名单轮询 Explorer 的拷贝对话框。
/// <para>
/// 【为什么只读拷贝对话框】D:\MacDock（同作者 C++ 版）实测过：<c>ITaskbarList3</c> 的进度状态
/// 在目标窗口上**不留任何可枚举属性**，跨进程读不到；vtable hook 属禁止项。唯一可用的是
/// MSAA/UIA 读 Explorer 拷贝对话框（窗口类 <c>OperationStatusWindow</c>，标题为空，
/// 其进度条元素的可访问值形如 <c>56%</c>）。本项目用 UIA 的 Value/RangeValue 模式取同一个值，
/// 比手写 MSAA vtable 稳。
/// </para>
/// <para>
/// 【白名单】只映射到 <c>explorer.exe</c>（文件资源管理器）。Chrome 下载等入口留给后续白名单，
/// 没接的就不画——空进度条比假进度条好。
/// </para>
/// <para>
/// 【开销】700ms 一次 EnumWindows；只有发现匹配窗口时才做一次 UIA 查询。
/// 没有拷贝时只花一次窗口枚举（微秒级）。
/// </para>
/// </summary>
internal sealed class TaskbarProgressService : IDisposable
{
    /// <summary>Explorer 拷贝对话框的窗口类（实测；标题为空，不能按标题找）。</summary>
    private const string CopyDialogClass = "OperationStatusWindow";

    /// <summary>读到的进度归到哪个 Dock 条目（按 exe 文件名匹配）。</summary>
    private const string TargetExe = "explorer.exe";

    private static readonly Dictionary<string, (double, DockProgressState)> Empty = new(StringComparer.OrdinalIgnoreCase);

    private readonly DockSurface _surface;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    /// <summary>连续几次没找到对话框才清除进度条（防"对话框换阶段/重建"时进度条闪烁）。</summary>
    private int _misses;

    /// <summary>上一次上报的进度（用于"变化才写日志"，避免 300ms 一条刷屏）。</summary>
    private double _lastFraction = double.NaN;

    /// <summary>已 dump 过进度条结构的对话框句柄（每个对话框只 dump 一次）。</summary>
    private IntPtr _dumpedDialog = IntPtr.Zero;

    public TaskbarProgressService(DockSurface surface, Dispatcher dispatcher)
    {
        _surface = surface;
        // 300ms：拷贝对话框自身大约每 100–200ms 更新一次，700ms 会明显"慢半拍"；
        // 没有对话框时这一拍只是一次 EnumWindows（微秒级），有对话框时多一次 UIA 读（毫秒级）。
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
    }

    /// <summary>采样一次并刷新 Dock。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var (found, fraction) = Probe(out var dialog);
            if (!found)
            {
                // 防抖：一次性没找到不等于拷贝结束（对话框换阶段时可能瞬时消失），
                // 连续 3 次（约 0.9s）都没找到才清空。
                if (++_misses >= 3)
                {
                    if (_lastFraction is not double.NaN)
                    {
                        Logger.Info("进度：拷贝对话框已消失，清除进度条");
                        _lastFraction = double.NaN;
                    }

                    _surface.ApplyProgress(Empty);
                }

                return;
            }

            _misses = 0;

            if (dialog != _dumpedDialog)
            {
                _dumpedDialog = dialog;
                DumpProgressBars(dialog);
            }

            var value = fraction < 0 ? -1 : DockProgressBar.Clamp(fraction);
            if (double.IsNaN(_lastFraction) || value < 0 || Math.Abs(value - _lastFraction) >= 0.02)
            {
                Logger.Info($"进度：fraction={(value < 0 ? "未知" : value.ToString("0.###"))}（已应用到 Dock）");
                _lastFraction = value;
            }

            var map = new Dictionary<string, (double, DockProgressState)>(StringComparer.OrdinalIgnoreCase)
            {
                [TargetExe] = fraction < 0
                    ? (0, DockProgressState.Indeterminate)   // 对话框在忙但没有数字
                    : (DockProgressBar.Clamp(fraction), DockProgressState.Normal)
            };
            _surface.ApplyProgress(map);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 进度条采样失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 找一次拷贝对话框并读进度。返回 <c>(是否找到, 0..1 或 -1)</c>。
    /// 也供 <c>--progress-probe</c> 诊断入口复用，避免探针与产品走两条不同的读取路径。
    /// </summary>
    internal static (bool Found, double Fraction) Probe()
        => Probe(out _);

    /// <summary>同上，另返回对话框句柄（服务用它做"每个对话框只 dump 一次"的去重）。</summary>
    internal static (bool Found, double Fraction) Probe(out IntPtr dialog)
    {
        dialog = IntPtr.Zero;
        var handle = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (!string.Equals(NativeMethods.GetWindowClassName(hwnd), CopyDialogClass, StringComparison.Ordinal))
            {
                return true;
            }

            handle = hwnd;
            return false;   // 找到一个就够（同一时刻只会有一个前台拷贝对话框）
        }, IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            return (false, -1);
        }

        dialog = handle;

        try
        {
            var root = AutomationElement.FromHandle(handle);
            var bars = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
            if (bars.Count == 0)
            {
                // 实测（Win11 24H2）：拷贝对话框是 DirectUI，UIA 树里**一条 ProgressBar 都没有**
                // （日志实证 "对话框内共 0 条进度条"）。退一步扫所有元素的名字：部分版本的对话框
                // 会把 "56%" 放在文本元素的 Name 上。
                return (true, PercentFromNames(root));
            }

            // 选"最宽的那条"：拷贝对话框里可能同时有"当前项"和"总体"两条进度条，
            // 总体条横跨整个对话框、当前项条只占详情行。取最宽的一条才跟着整体进度走；
            // 只取第一条会表现为"进度条跟着单个文件在 0↔100 之间来回跳"。
            var best = -1.0;
            var bestWidth = double.NegativeInfinity;
            for (var i = 0; i < bars.Count; i++)
            {
                var fraction = FractionOf(bars[i]);
                var width = bars[i].Current.BoundingRectangle.Width;
                if (fraction >= 0 && width >= bestWidth)
                {
                    best = fraction;
                    bestWidth = width;
                }
            }

            return (true, best);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 进度条：UIA 读取失败 {ex.GetType().Name} {ex.Message}");
            return (true, -1);
        }
    }

    /// <summary>
    /// 在所有后代元素的名字里找百分数（UIA 无 ProgressBar 时的兜底）。
    /// 取"名字里带 % 且元素最宽"的那条：拷贝对话框的总体进度文本比逐项文本更宽。
    /// </summary>
    private static double PercentFromNames(AutomationElement root)
    {
        try
        {
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var best = -1.0;
            var bestWidth = double.NegativeInfinity;
            for (var i = 0; i < all.Count && i < 200; i++)
            {
                var element = all[i];
                var fraction = DockProgressBar.ParsePercentText(element.Current.Name);
                if (fraction < 0)
                {
                    continue;
                }

                var width = element.Current.BoundingRectangle.Width;
                if (width >= bestWidth)
                {
                    best = fraction;
                    bestWidth = width;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            Logger.Warn($"进度：名字扫描失败 {ex.GetType().Name} {ex.Message}");
            return -1;
        }
    }

    /// <summary>读一条进度条的 0..1（Value → RangeValue → Name 三级回退）；读不到返回 -1。</summary>
    private static double FractionOf(AutomationElement bar)
    {
        if (bar.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
        {
            var parsed = DockProgressBar.ParsePercentText(((ValuePattern)valuePattern).Current.Value);
            if (parsed >= 0)
            {
                return parsed;
            }
        }

        if (bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangePattern))
        {
            var range = ((RangeValuePattern)rangePattern).Current;
            if (range.Maximum > 0)
            {
                return DockProgressBar.Clamp(range.Value / range.Maximum);
            }
        }

        return DockProgressBar.ParsePercentText(bar.Current.Name);
    }

    /// <summary>
    /// 首次看到某个拷贝对话框时，把它里面所有进度条的名字/数值/范围写一行日志。
    /// <para>
    /// 为什么需要：拷贝对话框里可能同时有"当前项"和"总体"两条进度条，只取第一条会表现为
    /// "进度条跟着单个文件在 0↔100 之间跳"——用户看到的就是"同步不好"。日志只写数值与
    /// UIA 名称（如 "56%"/"进度"），不写文件名或路径。
    /// </para>
    /// </summary>
    private static void DumpProgressBars(IntPtr dialog)
    {
        try
        {
            var root = AutomationElement.FromHandle(dialog);
            var bars = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
            Logger.Info($"进度：对话框内共 {bars.Count} 条进度条");

            if (bars.Count == 0)
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Logger.Info($"进度：对话框内元素 {all.Count} 个，前 30 个名字：");
                for (var i = 0; i < all.Count && i < 30; i++)
                {
                    Logger.Info($"进度：  [{i}] type={all[i].Current.ControlType.ProgrammaticName} name=[{all[i].Current.Name}]");
                }
            }

            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var name = bar.Current.Name;
                var valueText = bar.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)
                    ? ((ValuePattern)vp).Current.Value
                    : string.Empty;
                var rangeText = string.Empty;
                if (bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp))
                {
                    var range = ((RangeValuePattern)rp).Current;
                    rangeText = $"{range.Value:0.##}/{range.Maximum:0.##}";
                }

                Logger.Info($"进度：第 {i} 条 name=[{name}] value=[{valueText}] range=[{rangeText}]");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"进度：进度条结构读取失败 {ex.GetType().Name} {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
    }
}
