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

    public TaskbarProgressService(DockSurface surface, Dispatcher dispatcher)
    {
        _surface = surface;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
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
            var (found, fraction) = Probe();
            if (!found)
            {
                _surface.ApplyProgress(Empty);
                return;
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
    {
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

        try
        {
            var root = AutomationElement.FromHandle(handle);
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar);
            var bar = root.FindFirst(TreeScope.Descendants, condition);
            if (bar is null)
            {
                return (true, -1);
            }

            if (bar.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var parsed = DockProgressBar.ParsePercentText(((ValuePattern)valuePattern).Current.Value);
                if (parsed >= 0)
                {
                    return (true, parsed);
                }
            }

            if (bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangePattern))
            {
                var range = ((RangeValuePattern)rangePattern).Current;
                if (range.Maximum > 0)
                {
                    return (true, DockProgressBar.Clamp(range.Value / range.Maximum));
                }
            }

            return (true, DockProgressBar.ParsePercentText(bar.Current.Name));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 进度条：UIA 读取失败 {ex.GetType().Name} {ex.Message}");
            return (true, -1);
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
