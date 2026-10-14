using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Services;

public enum TaskbarPolicy { Keep, AutoHide, Hide }

/// <summary>
/// 原生任务栏策略管理。AutoHide=系统级自动隐藏；Hide=释放条带预留 + 移出屏幕/整窗隐藏（均可还原）。
/// <para>
/// 【Hide 为什么必须走 autohide —— 实测结论，勿再回退成"只藏窗口"】Win11（24H2/25H2, build 26200）
/// 任务栏的底部条带由 shell 自己的布局持有：把 <c>Shell_TrayWnd</c> 窗口移出屏幕（实测 after=(0,768)-(1024,816)）
/// 或 <c>SW_HIDE</c>，甚至对它发 <c>ABM_REMOVE</c>（返回 TRUE=1），<c>ABM_QUERYPOS(ABE_BOTTOM)</c> 依旧
/// 返回 bottom=screenH-48 —— 48px 预留**没有释放**，Dock 只能悬在真底边上方 48px（用户投诉的"断裂感"）。
/// 实测唯一释放该预留的手段是 <c>ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)</c>：autohidden appbar
/// 不占工作区，此后 QUERYPOS 返回 bottom=screenH（真底边，缺口 0px）。
/// </para>
/// <para>
/// 【autohide 的副作用与屏蔽】autohide 打开后，光标顶到屏幕最底行会让任务栏滑出（实测滑到 (0,720)-(1024,768)，
/// 即使我们自己的 topmost AppBar 盖住触发区也照样滑）。故 Hide 在释放条带后再把窗口请出去：
/// 先尝试 SWP_NOSENDCHANGING 移出屏幕，被 shell 回夹时降级 <c>SW_HIDE</c>（Win11 XAML 任务栏会把程序性移动
/// 在 WM_WINDOWPOSCHANGING 里夹回屏幕内，实测 SetWindowPos 返回成功但矩形原位不动）。两种路径都实测
/// 让滑出不再发生（悬停底边 3.7s：visible=False 且矩形停在 (0,766)-(1024,814)）。
/// </para>
/// 还原凭据全程留存：<see cref="_origState"/>（改动前的 ABM_GETSTATE 原值，Restore 原样写回）
/// + <see cref="_saved"/> 屏幕内矩形（几何复位）+ <see cref="_swHidden"/> 可见性。
/// 两条还原各自独立：<b>可见性只认 _swHidden</b>，几何复位只认 _saved——SW_HIDE 兜底可能发生在
/// _saved 为空时（上一个实例被强杀留下 autohide 位 → 本实例的基准捕获正确拒绝停靠位矩形），
/// 若把可见性挂在 _saved 上就会漏还原，任务栏退出后永久不可见（评审发现）。
/// Hide 策略落地（退出/切回 Keep）时另有一次兜底，只清 autohide 位（<c>state &amp; ~ABS_AUTOHIDE</c>，
/// 不写其余位），用于擦掉"上一个被强杀实例"的残留
/// （代价：用户自己的原生 autohide 偏好会被一并清掉，见 README 已知限制 5(b)）。
/// shell 重布局（Dock AppBar 重注册、explorer 重启）可能把任务栏摆回屏幕内、或把 autohide 关掉，经 Reassert 重申。
/// </summary>
public sealed class TaskbarManager
{
    private readonly Dictionary<IntPtr, RECT> _saved = new(); // 还原基准：首次隐藏时的屏幕内矩形
    private readonly HashSet<IntPtr> _swHidden = new();       // 经 SW_HIDE 隐藏的窗口：Restore 第 1 步**只按这张表**SW_SHOWNA（基准缺失也要还原）
    private TaskbarPolicy _policy = TaskbarPolicy.Keep;
    private int? _origState;      // 本进程首次改 autohide 之前的 ABM_GETSTATE 原值（Restore 原样写回）
    private bool _autohideApplied; // 本进程把 autohide 从"关"改成了"开"（仅此时 Restore 才需要写回）

    public event Action? PolicyApplied;
    public TaskbarPolicy Current => _policy;

    public void Apply(TaskbarPolicy policy)
    {
        Logger.Info($"任务栏策略: {_policy} → {policy}");
        RestoreInternal();
        _policy = policy;
        switch (policy)
        {
            case TaskbarPolicy.AutoHide: EnableAutoHide(); break;
            case TaskbarPolicy.Hide:
                CaptureOnScreenRects(); // 必须在释放条带之前：autohide 一开，窗口矩形就变成"藏起来的停靠位"，不再是还原基准
                ReclaimBottomStrip();
                HideAll();
                break;
        }
        PolicyApplied?.Invoke();
    }

    /// <summary>explorer 重启（TaskbarCreated 广播）后重新应用当前策略。</summary>
    public void HandleTaskbarCreated() => Apply(_policy);

    /// <summary>
    /// shell 重布局（Dock AppBar 注册/重注册、explorer 重启等）后重申 Hide 策略：
    /// 条带预留可能被 shell 收回（autohide 被关）或任务栏被摆回屏幕内，两者都要重申。
    /// 幂等（已释放/已移出的窗口跳过）；非 Hide 策略为 no-op。
    /// </summary>
    public void Reassert()
    {
        if (_policy == TaskbarPolicy.Hide)
        {
            CaptureOnScreenRects(); // 同 Apply：先留存还原基准，再动状态位
            ReclaimBottomStrip();
            HideAll();
        }
    }

    /// <summary>退出/策略切换前调用：先恢复可见性 → 关自动隐藏并写回原始状态位（shell 随即把任务栏重排回
    /// 屏幕内）→ 兜底按留存基准复位矩形。三步顺序不可颠倒，见 <see cref="RestoreInternal"/> 注释。</summary>
    public void Restore()
    {
        RestoreInternal();
        _policy = TaskbarPolicy.Keep;
        PolicyApplied?.Invoke();
    }

    private void RestoreInternal()
    {
        // 1) 先恢复可见性：**只认 _swHidden，不能按 _saved 的键遍历**。
        //    SW_HIDE 兜底完全可能在"没有还原基准"的情况下发生：上一个实例被强杀把 autohide 位留在"开"，
        //    本实例启动时任务栏已停在停靠位 (0,766)-(1024,814) → CaptureOnScreenRects 正确地拒绝把它当基准
        //    （基准若取停靠位，Restore 会把任务栏摆回屏幕外）→ HideAll 的移动又被 shell 夹回 → 走 SW_HIDE。
        //    旧写法 `foreach (_saved) if (_swHidden.Remove(hwnd)) ShowWindow(...)` 在这种组合下**一个都不还原**
        //    （_saved 为空 → 循环不执行），随后 IntersectWith 还把 _swHidden 的记录抹掉 → 任务栏永久不可见，
        //    只能重启 explorer 救回；而 shell 重布局并不会替我们把 SW_HIDE 的窗口 Show 回来（见下方 2 的实测）。
        //    死亡句柄（explorer 重启后的旧 hwnd）用 IsWindow 过滤，保持 _swHidden 不积累残留。
        foreach (var hwnd in _swHidden.ToArray())
        {
            var alive = NativeMethods.IsWindow(hwnd);
            if (alive)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowna); // SW_SHOWNA：恢复可见且不抢焦点
            Logger.Info($"Restore(1/3): hwnd=0x{hwnd.ToInt64():X} {(alive ? "SW_SHOWNA" : "跳过（句柄已失效，explorer 重启过）")}" +
                        "（可见性还原只认 _swHidden，不依赖 _saved 基准）");
        }
        _swHidden.Clear();
        // 2) 再关 autohide 写回原状态：shell 在状态位变化的瞬间按"占位条带"重排任务栏，
        //    此时窗口已可见，重排结果就是正常屏幕内位置（实测落回 (0,720)-(1024,768)）。
        if (_autohideApplied) DisableAutoHide(); // 只清自己设的 autohide；Keep 策略不触碰系统开关
        // 3) 最后兜底复位矩形：shell 若没摆回原位（多显示器/被 Dock 条带挤过），按留存基准强摆一次。
        foreach (var (hwnd, rect) in _saved)
        {
            NativeMethods.GetWindowRect(hwnd, out var before);
            Logger.Info($"Restore: hwnd=0x{hwnd.ToInt64():X} 当前=({before.Left},{before.Top})-({before.Right},{before.Bottom})" +
                        $" → 基准=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
        }
        _saved.Clear();
        // Hide 策略落地（退出 / 切到 Keep）时兜底清一次 autohide 位——不管这个位是不是本实例认领的：
        // 它可能是"上一个被强杀的实例"留下的（本实例启动时它已为开 → EnableAutoHide 不认领还原责任
        // → DisableAutoHide 是 no-op），那样任务栏会停在半隐藏态。用户选"隐藏"的语义是
        // "我们走了以后原生任务栏要完整回来"，宁可覆盖用户自己的原生 autohide 偏好
        // （可在 系统设置 > 个性化 > 任务栏 重开），也不留半隐藏态。
        // 但**只清 autohide 这一个位**：旧写法直接写 AbsAlwaysOnTop 是"置位"而不是"清位"——
        // 原状态=1（自动隐藏、非常置顶）时会被改成 2，多改了一个本不属于我们的位。
        var state = NativeMethods.GetAppBarState();
        if (_policy == TaskbarPolicy.Hide && (state & NativeMethods.AbsAutohide) != 0)
        {
            var target = state & ~NativeMethods.AbsAutohide;
            NativeMethods.SetAppBarState(target);
            Logger.Info($"Restore(Hide): 只清 autohide 位 {state} → {target}（可能是上一个被强杀实例的残留，其余位原样保留）");
        }
    }

    // ---------- 条带预留（autohide 开关） ----------

    /// <summary>
    /// 释放任务栏的底部条带预留，让 Dock 的 <c>ABM_QUERYPOS(ABE_BOTTOM)</c> 能拿到真屏幕底边。
    /// 手段：<c>ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)</c>（实测唯一有效；ABM_REMOVE 无效，见类型注释）。
    /// 幂等：系统本来就 autohide（用户自己的偏好）时不动状态位、也不认领还原责任。
    /// Keep 策略下为 no-op（返回 false = 不释放，Dock 让位于任务栏上方）。
    /// </summary>
    /// <returns>调用后条带预留是否处于"已释放"态。</returns>
    public bool ReclaimBottomStrip()
    {
        if (_policy == TaskbarPolicy.Keep) return false;
        EnableAutoHide();
        var released = (NativeMethods.GetAppBarState() & NativeMethods.AbsAutohide) != 0;
        Logger.Info($"ReclaimBottomStrip: autohide={released}（ABM_QUERYPOS 底边{(released ? "=真屏幕底边" : "仍被任务栏占用")}）");
        return released;
    }

    /// <summary>打开系统自动隐藏（首次改动前留存 ABM_GETSTATE 原值，供 Restore 原样写回）。</summary>
    private void EnableAutoHide()
    {
        var state = NativeMethods.GetAppBarState();
        if ((state & NativeMethods.AbsAutohide) != 0)
        {
            Logger.Info($"EnableAutoHide: 系统已是 autohide（state={state}），不认领还原责任");
            return; // 用户原生偏好：不改状态位，Restore 也不动它
        }
        _origState ??= state;
        NativeMethods.SetAppBarState(state | NativeMethods.AbsAutohide | NativeMethods.AbsAlwaysOnTop);
        _autohideApplied = true;
        Logger.Info($"EnableAutoHide: ABM_SETSTATE {state} → {state | NativeMethods.AbsAutohide | NativeMethods.AbsAlwaysOnTop}（原值已留存 {_origState}）");
    }

    /// <summary>关闭本进程打开的自动隐藏，写回改动前的原始状态位（未打开过则 no-op）。</summary>
    private void DisableAutoHide()
    {
        if (!_autohideApplied) return;
        var target = _origState ?? NativeMethods.AbsAlwaysOnTop; // 兜底：原值未知时回到"常显置顶"
        NativeMethods.SetAppBarState(target);
        Logger.Info($"DisableAutoHide: ABM_SETSTATE → 原状态 {_origState?.ToString() ?? "AbsAlwaysOnTop(兜底)"}");
        _autohideApplied = false;
        _origState = null;
    }

    // ---------- 任务栏窗口 ----------

    /// <summary>
    /// 留存还原基准：把当前**完整位于屏幕内**的任务栏窗口矩形记进 <see cref="_saved"/>（已有基准的不覆盖）。
    /// 为什么要在动 autohide 状态位之前单独做：ABM_SETSTATE(ABS_AUTOHIDE) 一生效，shell 立刻把窗口挪到
    /// 停靠位（实测 (0,766)-(1024,814)，只露 2px），此时再取矩形就会把"隐藏态"当成还原基准，Restore 会把
    /// 任务栏摆回屏幕外。
    /// </summary>
    private void CaptureOnScreenRects()
    {
        var screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN：主屏底边
        // SM_YVIRTUALSCREEN(77) + SM_CYVIRTUALSCREEN(79) = 虚拟桌面底边：副屏比主屏更低时，
        // 副屏任务栏的"屏幕内"判定要用虚拟桌面底，否则会被误判成隐藏态、丢掉还原基准。
        var bottom = Math.Max(screenH, NativeMethods.GetSystemMetrics(77) + NativeMethods.GetSystemMetrics(79));
        foreach (var hwnd in FindTaskbars())
        {
            if (_saved.ContainsKey(hwnd)) continue;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) continue;
            if (rect.Bottom > bottom || rect.Top >= screenH)
            {
                Logger.Info($"CaptureOnScreenRects: hwnd=0x{hwnd.ToInt64():X} 矩形 ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) 不在屏幕内，不作基准");
                continue; // 已经处于隐藏/停靠态：不能当还原基准
            }
            _saved[hwnd] = rect;
            Logger.Info($"CaptureOnScreenRects: hwnd=0x{hwnd.ToInt64():X} 基准=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
        }
    }

    private void HideAll()
    {
        var screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
        foreach (var hwnd in FindTaskbars())
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) continue;
            if (rect.Top >= screenH)
            {
                Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 已在屏幕外 (top={rect.Top})，跳过");
                continue;
            }
            if (!_saved.ContainsKey(hwnd) && rect.Bottom <= screenH)
                _saved[hwnd] = rect; // 只记首次屏幕内矩形：重复 HideAll（Reassert）不覆盖还原基准
            // 【多显示器已知缺陷（本机单屏无法实测，README 已知限制 5(d)）】
            // 这里的 screenH 取的是 SM_CYSCREEN = **主屏**底边，没有按窗口所在显示器调 MonitorFromWindow
            // 取该屏自己的底边。后果：副屏的 Shell_SecondaryTrayWnd（例如副屏在 1920,0 起、高 1080）
            // 会被摆到 (0,768)-(1920,816) 这类**主屏那一行**上——任务栏跑到错误的显示器上，
            // 副屏底部反而露出壁纸/或主屏底部多出一条不该在的东西，而副屏自己的条带预留仍需 autohide 释放。
            // 还原不受这个缺陷影响：_saved 按句柄各自存屏幕内基准矩形，Restore 第 3 步用它复位回原显示器。
            // 尝试 1：SWP_NOSENDCHANGING 绕过任务栏的 WM_WINDOWPOSCHANGING 回夹直接落位
            var ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, screenH,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate | NativeMethods.SwpNoSendChanging);
            NativeMethods.GetWindowRect(hwnd, out var after);
            if (after.Top >= screenH)
            {
                Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 移出屏幕成功 after=({after.Left},{after.Top})-({after.Right},{after.Bottom})");
                continue;
            }
            Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 移动被 shell 还原 (ok={ok} after=({after.Left},{after.Top})-({after.Right},{after.Bottom}))，降级 SW_HIDE");
            // 尝试 2：整窗隐藏（Win11 XAML 任务栏对移动有回夹，对可见性没有）——还原时 SW_SHOWNA
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            _swHidden.Add(hwnd);
            Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} SW_HIDE 完成");
        }
    }

    private static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            var cls = NativeMethods.GetWindowClassName(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
