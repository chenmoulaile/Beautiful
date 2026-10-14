using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Beautiful.App.Services;

namespace Beautiful.App.Interop;

/// <summary>
/// WH_KEYBOARD_LL 低级键盘钩子的安装/卸载与回调调度（Task 11）。
///
/// 【为什么必须"即刻返回"】低级钩子的回调安装在**当前线程的消息泵**上，系统等待回调返回的时间受
/// <c>LowLevelHooksTimeout</c>（默认约 300ms）限制：超时后 Windows 会**静默摘掉钩子**——表现为
/// "Alt+Tab 有时又变回系统样式"这种极难复现的故障。因此本类的回调路径坚持三条纪律：
/// ①零堆分配（手工按偏移读 KBDLLHOOKSTRUCT 字段，不做结构体封送）；
/// ②不做任何 UI/磁盘/阻塞调用（耗时告警也只在超时后**投递**到 Dispatcher 稍后写日志）；
/// ③每次回调都测耗时，超过阈值立即改为"直通放行"（CallNextHookEx），宁可丢一次接管也不拖慢输入链。
///
/// 【委托生命周期】委托实例存字段并用 <see cref="GCHandle"/> 固定——非托管回调若只靠局部变量引用，
/// GC 回收后回调会打到已回收的桩上（AV 崩溃）。钩子句柄必须在**安装它的线程**上卸载，故 Dispose
/// 会把卸载转回 UI 线程执行。
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>
    /// 按键回调：返回 true = 吞掉该事件（系统与其他应用都收不到），false = 放行。
    /// 约定：实现在任意线程上被调用（实为安装线程），必须短、无分配、不抛异常（抛了会被本类兜住并放行）。
    /// </summary>
    /// <param name="msg">WM_KEYDOWN / WM_KEYUP / WM_SYSKEYDOWN / WM_SYSKEYUP。</param>
    /// <param name="vkCode">虚拟键码。</param>
    /// <param name="flags">KBDLLHOOKSTRUCT.flags（注入位/Alt 位/抬起位）。</param>
    public delegate bool KeyCallback(int msg, uint vkCode, uint flags);

    private const int SlowCallbackBudgetMs = 300;

    private readonly KeyCallback _callback;      // 业务回调（AltTabHook 提供）
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // 必须持有：见类型注释
    private readonly GCHandle _pin;              // 固定委托，杜绝 GC 回收
    private readonly Dispatcher _ui;
    private readonly long _budgetTicks = Stopwatch.Frequency / 1000 * SlowCallbackBudgetMs;

    private IntPtr _hook;
    private long _slowCount;                     // 累计超时次数（Interlocked）
    private long _errorCount;                    // 累计业务回调异常次数（Interlocked）
    private int _warnPending;                    // 0/1：是否已投递告警，防日志风暴
    private bool _disposed;

    public LowLevelKeyboardHook(KeyCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _proc = Invoke;
        _pin = GCHandle.Alloc(_proc);
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>钩子是否在线。</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>累计超时次数（诊断用）。</summary>
    public long SlowCallbackCount => Interlocked.Read(ref _slowCount);

    /// <summary>
    /// 安装全局钩子（dwThreadId=0 → 系统范围）。必须在有消息泵的 UI 线程调用。
    /// 重复调用为 no-op；失败返回 false（调用方据此降级，不影响其他功能）。
    /// </summary>
    public bool Install()
    {
        if (_disposed || _hook != IntPtr.Zero) return _hook != IntPtr.Zero;
        if (!_ui.CheckAccess())
        {
            Logger.Warn("LowLevelKeyboardHook.Install 必须在 UI 线程调用（钩子回调投递在该线程消息泵上）");
            return false;
        }
        // LL 钩子的 hMod 被系统忽略（官方文档：WH_KEYBOARD_LL 传 0 即可）
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            Logger.Warn($"LowLevelKeyboardHook: SetWindowsHookEx 失败 (GetLastWin32Error={Marshal.GetLastWin32Error()})");
        return _hook != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 卸载必须在安装线程上；从别线程 Dispose 时同步转回。
        // 关键：只有确认钩子已离场才解除委托固定——若 Dispatcher 已关停导致卸载没跑成，
        // 宁可泄漏一个 GCHandle（进程即将退出，代价为零），也不能让还挂着的钩子回调到已回收的委托。
        var unhooked = false;
        try
        {
            if (_ui.CheckAccess()) { Uninstall(); unhooked = true; }
            else _ui.Invoke(() => { Uninstall(); unhooked = true; });
        }
        catch (Exception ex)
        {
            Logger.Warn($"LowLevelKeyboardHook: 卸载钩子未完成: {ex.Message}");
        }
        if ((unhooked || _hook == IntPtr.Zero) && _pin.IsAllocated) _pin.Free();
    }

    /// <summary>
    /// 卸载钩子（幂等）。必须在安装线程（UI 线程）执行，故内部转回该线程；
    /// 关开关/应急停用走这里，Dispose 亦复用。GCHandle 只有在钩子确认离场后才解除固定。
    /// </summary>
    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        if (!NativeMethods.UnhookWindowsHookEx(_hook))
            Logger.Warn($"LowLevelKeyboardHook: UnhookWindowsHookEx 失败 (GetLastWin32Error={Marshal.GetLastWin32Error()})");
        _hook = IntPtr.Zero;
        Logger.Info($"LowLevelKeyboardHook: 钩子已卸载（累计超时 {Interlocked.Read(ref _slowCount)} 次）");
    }

    // ---------- 回调热路径 ----------

    /// <summary>
    /// 非托管回调入口。nCode&lt;0 必须直接转下一环（系统要求）；其余按"读码 → 业务判定 → 放行/吞掉"。
    /// 全程 try/catch：业务回调抛出的异常绝不能逃出回调（会打断安装线程的消息循环）。
    /// </summary>
    private IntPtr Invoke(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || lParam == IntPtr.Zero)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var start = Stopwatch.GetTimestamp();
        bool swallow;
        try
        {
            // 按 KBDLLHOOKSTRUCT 布局手工取值（vkCode@0 / scanCode@4 / flags@8 / time@12 / extra@16）：
            // 零堆分配，且避免 Marshal.PtrToStructure<T> 的封送开销。
            uint vk = unchecked((uint)Marshal.ReadInt32(lParam, 0));
            uint flags = unchecked((uint)Marshal.ReadInt32(lParam, 8));
            swallow = _callback(wParam.ToInt32(), vk, flags);
        }
        catch (Exception ex)
        {
            // 业务异常 → 放行本次按键（用户的键盘永远优先于我们的功能），异常计数与超时计数分开
            Interlocked.Increment(ref _errorCount);
            QueueWarning("回调异常", ex.Message);
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 耗时采样：已超预算 → 即便业务判定要吞也改为放行，避免"钩子被系统摘掉 + 输入被吞"双输
        if (Stopwatch.GetTimestamp() - start > _budgetTicks)
        {
            Interlocked.Increment(ref _slowCount);
            QueueWarning("回调超过 LowLevelHooksTimeout 预算", $"{SlowCallbackBudgetMs}ms 内未返回，本次已改为直通");
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        return swallow ? new IntPtr(1) : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 告警延迟到 UI 线程写文件——回调内直接 Logger.Warn 会做磁盘 IO，正是拖垮钩子的行为。
    /// 用 _warnPending 去重，避免高频超时下日志风暴（一次告警覆盖一段持续超时）。
    /// </summary>
    private void QueueWarning(string title, string detail)
    {
        if (Interlocked.Exchange(ref _warnPending, 1) == 1) return;
        try
        {
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _warnPending, 0);
                Logger.Warn($"LowLevelKeyboardHook {title}: {detail}" +
                            $"（超时 {Interlocked.Read(ref _slowCount)} 次 / 异常 {Interlocked.Read(ref _errorCount)} 次）");
            });
        }
        catch
        {
            Interlocked.Exchange(ref _warnPending, 0); // Dispatcher 已关停：放弃这条告警
        }
    }
}
