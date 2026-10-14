using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 前台窗口变化的**共享**观察者：整个进程只装一条 <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>，
/// 顶栏（前台应用名）与切换器（MRU 维护）都从这里取同一信号。
///
/// 【为什么共享】两者原本会各自安装一条完全相同的 OUTOFCONTEXT 钩子——钩子本身没有正确性问题，
/// 但每条钩子都意味着每次前台切换多一次跨进程回调与一次 UI 线程投递，且两条钩子的委托/句柄要分别
/// 管理生命周期（历史上这类"第二份同样的 hook"正是漏解绑的高发区）。故抽成本类，按订阅计数装/摘：
/// 首个订阅者安装，最后一个退订时解绑。句柄与委托都保存在静态字段里，防止 GC 回收委托导致回调打到
/// 已回收的桩上。
///
/// 【线程模型】必须在 WPF UI 线程上 <see cref="Subscribe"/>（钩子回调在安装线程的消息泵上投递）。
/// 回调内不做任何耗时事：只把通知 <see cref="Dispatcher.BeginInvoke"/> 转发给订阅者。
/// </summary>
public static class ForegroundWatcher
{
    private static readonly List<Action<IntPtr>> Handlers = new();
    private static readonly Dispatcher UI =
        System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    // 委托与钩子句柄都必须持有在字段里：SetWinEventHook 的委托若被 GC，回调会访问已回收的桩。
    private static NativeMethods.WinEventDelegate? _proc;
    private static IntPtr _hook;

    /// <summary>
    /// 订阅前台窗口变化。参数为新前台句柄（<see cref="IntPtr.Zero"/> 表示前台丢失）——
    /// 事件流不去重、不缓存语义，订阅方自行按句柄判断是否变化（各家消费者的判定口径不同）。
    /// 返回的 <see cref="IDisposable"/> 退订后自动失效，可安全重复 Dispose。
    /// 退订允许来自任意线程（内部转回 UI 线程执行）。
    /// </summary>
    public static IDisposable Subscribe(Action<IntPtr> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!UI.CheckAccess())
            throw new InvalidOperationException("ForegroundWatcher.Subscribe 必须在 WPF UI 线程调用。");

        if (Handlers.Count == 0) Install();
        Handlers.Add(handler);
        return new Subscription(handler);
    }

    /// <summary>当前前台窗口句柄（订阅者收到通知后仍应重新查询，事件句柄可能已过时）。</summary>
    public static IntPtr Current => NativeMethods.GetForegroundWindow();

    /// <summary>是否已装钩子（仅诊断/测试用）。</summary>
    public static bool IsHookInstalled => _hook != IntPtr.Zero;

    private static void Install()
    {
        _proc = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground,
            IntPtr.Zero, _proc, 0, 0, NativeMethods.WineventOutOfContext);
        if (_hook == IntPtr.Zero)
            Logger.Warn("ForegroundWatcher: SetWinEventHook 失败，前台变化仅能靠兜底定时器刷新");
        else
            Logger.Info("ForegroundWatcher: 共享 EVENT_SYSTEM_FOREGROUND 钩子已安装");
    }

    private static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null; // 钩子已摘，委托可安全释放
        Logger.Info("ForegroundWatcher: 共享前台钩子已卸载（订阅者归零）");
    }

    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 回调极短：只转发，且复制一份订阅者快照，避免订阅者在回调中增删集合造成遍历异常。
        var snapshot = Handlers.Count > 0 ? Handlers.ToArray() : null;
        if (snapshot is null) return;

        void Dispatch()
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i](hwnd); }
                catch (Exception ex) { Logger.Warn($"ForegroundWatcher 订阅者异常（已隔离，不影响其他订阅者）: {ex.Message}"); }
            }
        }

        // 一律 BeginInvoke，**没有内联分支**：回调跑在安装线程（即 UI 线程）的消息泵上，
        // CheckAccess 恒为 true，旧的"同步内联"写法把订阅者的耗时活（切换器的窗口登记/图标）
        // 直接拖进了 WinEvent 回调栈。OUTOFCONTEXT 钩子同样受 LowLevelHooksTimeout 约束，
        // 超时被静默摘钩，殃及顶栏前台跟踪（评审发现 I4）。延后一拍投递不改变语义（订阅者
        // 本就应重新查询 Current 句柄），换来回调恒定极短。
        UI.BeginInvoke(DispatcherPriority.Normal, Dispatch);
    }

    private sealed class Subscription : IDisposable
    {
        private Action<IntPtr>? _handler;

        internal Subscription(Action<IntPtr> handler) => _handler = handler;

        public void Dispose()
        {
            var handler = _handler;
            if (handler is null) return;
            _handler = null;
            if (UI.CheckAccess()) Remove(handler);
            else UI.Invoke(() => Remove(handler)); // 退订转回 UI 线程，与安装同侧
        }

        private static void Remove(Action<IntPtr> handler)
        {
            if (!Handlers.Remove(handler)) return;
            if (Handlers.Count == 0) Uninstall();
        }
    }
}
