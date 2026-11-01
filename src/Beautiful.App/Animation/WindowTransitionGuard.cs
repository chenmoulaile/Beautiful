using System.Diagnostics;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Animation;

/// <summary>
/// 窗口过渡接管器（alpha.21 换思路的核心）。
/// <para>
/// 【为什么换思路】alpha.20 之前的一切方案都是"事件后追播"：原生最小化动画已经在跑，我们的替身
/// 叠上去演第二遍——用户看到的就是两条动画打架、替身半透明、窗口先弹出来再演动画。根子在于
/// <b>原生动画从来没有被关掉过</b>：SPI_SETANIMATION 在 Win11 24H2 已被系统限制（见
/// <see cref="SystemAnimationSuppressor"/> 的实测注释），拦 SC_MINIMIZE 又需要注入。
/// </para>
/// <para>
/// 【新思路】用两个公开 DWM 属性，把"原生动画"与"可见性"都从系统手里接过来：
/// ① <c>DWMWA_TRANSITIONS_FORCEDISABLED</c>——按窗口关闭 DWM 全部过渡动画，原生动画<b>从源头不存在</b>；
/// ② <c>DWMWA_CLOAK</c>——斗篷化窗口：屏幕上隐形、DWM 继续合成内容。最小化期间保持斗篷，
/// 还原时真窗口先回来但仍隐形，genie 放完才解斗篷——"窗口先弹出再演动画"的那一帧从此看不见。
/// 两者都是 dwmapi 的公开属性、可跨进程调用、不注入、不改窗口样式。
/// </para>
/// <para>
/// 【fail-open 是红线】斗篷把窗口藏起来的前提是"我们一定能放它出来"。为此三层保险：
/// 看门狗定时器（后台线程，UI 线程卡死也能解斗篷）、所有早退路径先解斗篷、Dispose 全量解斗篷。
/// 任何一层失败的表现只是"动画没播/窗口直接出现"——绝不允许"窗口凭空消失"。
/// </para>
/// </summary>
public sealed class WindowTransitionGuard : IDisposable
{
    /// <summary>斗篷超过这个时长还没被动画收尾解掉 → 看门狗强制解（fail-open）。
    /// 必须大于最长动画（morph ~400ms + 调度抖动），小于用户能察觉"窗口丢了"的耐心。</summary>
    private const int CloakTimeoutMs = 2000;

    /// <summary>看门狗扫一遍的间隔。</summary>
    private const int WatchdogPeriodMs = 300;

    /// <summary>打上 FORCEDISABLED 的窗口表（hwnd → 打上的时刻）。幂等去重与 Dispose 还原都用它。</summary>
    private readonly Dictionary<IntPtr, long> _noTransition = new();

    /// <summary>
    /// 打 FORCEDISABLED 被 DWM 拒过的窗口（alpha.21 真机：E_HANDLE 0x80070006 反复出现）。
    /// 不记忆的话同一窗口每次 SHOW 事件都重调一次 DWM + 刷一条 WARN，日志被这种噪声淹掉。
    /// 上限 1024：窗口句柄会被系统复用，满了整体清空重记，代价只是重试一次。
    /// </summary>
    private readonly HashSet<IntPtr> _noTransitionFailed = new();

    /// <summary>接管登记表上限：防长会话下无限增长（几千个窗口开开关关）。满了整体清空（重打是幂等无害的）。</summary>
    private const int ManagedTableLimit = 2048;

    /// <summary>斗篷登记表（hwnd → 打上时刻）。看门狗与"是不是我们斗的"判定都认这张表。</summary>
    private readonly Dictionary<IntPtr, long> _cloaked = new();

    /// <summary>
    /// SW_HIDE 代管表：斗篷被拒且仅限自家窗口时启用（见 Cloak）。隐藏窗口会从任务栏/
    /// 切换器消失，所以每一笔登记都有明确的恢复口（Uncloak）与兜底（看门狗清死窗口、退出清理现身）。
    /// </summary>
    private readonly HashSet<IntPtr> _hiddenByUs = new();

    /// <summary>哪些 hwnd 由谁管理由装配方决定（排除清单/资格判定集中在 App 装配处）。</summary>
    private readonly Func<IntPtr, bool> _shouldManage;

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly System.Threading.Timer? _watchdog;
    private long NowMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public WindowTransitionGuard(Func<IntPtr, bool> shouldManage)
    {
        _shouldManage = shouldManage;
        // 看门狗必须在后台线程跑：斗篷的解锁不能依赖 UI 线程活着——UI 线程恰恰是最可能卡死的那条
        _watchdog = new System.Threading.Timer(_ =>
        {
            try { SweepExpired(); }
            catch { /* 看门狗自身不许抛 */ }
        }, null, WatchdogPeriodMs, WatchdogPeriodMs);
    }

    /// <summary>当前斗篷中的窗口数（诊断行用）。</summary>
    public int CloakedCount { get { lock (_gate) return _cloaked.Count; } }

    /// <summary>已接管过渡动画的窗口数（诊断行用）。</summary>
    public int ManagedCount { get { lock (_gate) return _noTransition.Count; } }

    private readonly object _gate = new();

    // ---------- 过渡动画开关 ----------

    /// <summary>
    /// 确保该窗口的 DWM 过渡动画已被关闭。幂等：同一 hwnd 只打一次（重复打不报错但纯浪费）。
    /// 失败只记 WARN——关不掉的表现是"原生动画可能还在"，功能本身不受影响。
    /// </summary>
    public void EnsureNoTransitions(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_gate)
        {
            if (_noTransition.ContainsKey(hwnd)) return;
            if (_noTransitionFailed.Contains(hwnd)) return;   // 拒过的不再重试（见字段注释）
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == _ownPid) return;
            if (_noTransition.Count >= ManagedTableLimit)
            {
                _noTransition.Clear();   // 整体重来：重打幂等，清掉的是已销毁窗口的登记
                _noTransitionFailed.Clear();
            }
        }

        var one = 1;
        var hr = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaTransitionsForcedDisabled, ref one, sizeof(int));
        lock (_gate)
        {
            if (hr == 0) _noTransition[hwnd] = NowMs;
            else if (_noTransitionFailed.Count < 1024) _noTransitionFailed.Add(hwnd);
        }
        if (hr != 0)
            Logger.Info($"过渡接管：0x{hwnd.ToInt64():X} 关闭 DWM 过渡失败 hr=0x{hr:X8}（该窗口保留原生动画，本会话不再重试）");
    }

    /// <summary>该窗口是否已被我们关掉过渡动画（诊断用）。</summary>
    public bool IsManaged(IntPtr hwnd)
    {
        lock (_gate) return _noTransition.ContainsKey(hwnd);
    }

    /// <summary>
    /// 启动扫描：把**已经存在**的顶层窗口都接管一遍（OBJECT_SHOW 只报"显示"那一刻，
    /// 我们启动前就开着的窗口永远等不到那个事件）。
    /// </summary>
    public void ScanExistingWindows()
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == _ownPid) return true;
                var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlpExStyle);
                if ((ex & NativeMethods.WsExToolwindow) != 0) return true;
                if (!_shouldManage(hwnd)) return true;
                EnsureNoTransitions(hwnd);
            }
            catch { /* 单个窗口失败不影响其余 */ }
            return true;
        }, IntPtr.Zero);
        Logger.Info($"过渡接管：启动扫描完成（存量窗口 ManagedCount={ManagedCount}）");
    }

    // ---------- 斗篷 ----------

    /// <summary>
    /// 斗篷化窗口。幂等；成功后登记进看门狗表。返回 false = DWM 拒绝（极少数窗口类型），
    /// 调用方应把"没有斗篷"当作正常降级继续走（动画照播，只是没有隐形代管）。
    /// </summary>
    public bool Cloak(IntPtr hwnd, string reason)
    {
        if (hwnd == IntPtr.Zero) return false;
        var one = 1;
        var hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref one, sizeof(int));
        if (hr != 0)
        {
            // 斗篷被拒（真机：自家 WPF 窗口 E_ACCESSDENIED 0x80070005）。只对【最小化链路 + 自家窗口】
            // 启用 SW_HIDE 代管：最小化方向若没有隐形代管，原生"缩向任务栏"动画（SPI 接管在新系统
            // 上不可用）会和 genie 替身同播——用户实测"最小化动画很透明、观感差"的根因就是双动画叠加。
            // 代管窗口由 Uncloak 统一恢复（动画收尾/早退路径都会走到），死窗口由看门狗清表。
            if (reason == "minimize"
                && NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0
                && pid == (uint)Environment.ProcessId
                && NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
                lock (_gate) _hiddenByUs.Add(hwnd);
                Logger.Info($"斗篷：0x{hwnd.ToInt64():X} DWM 拒绝 hr=0x{hr:X8}（{reason}）→ SW_HIDE 代管（自家窗口，替身收尾自动恢复）");
                return true;
            }
            Logger.Info($"斗篷：0x{hwnd.ToInt64():X} DWM 拒绝 hr=0x{hr:X8}（{reason}）→ 无代管降级");
            return false;
        }
        lock (_gate) _cloaked[hwnd] = NowMs;
        return true;
    }

    /// <summary>解斗篷。幂等、永远安全：窗口没被我们斗过就是 no-op。失败会由看门狗再补一次。</summary>
    public void Uncloak(IntPtr hwnd, string reason)
    {
        if (hwnd == IntPtr.Zero) return;
        bool hidden;
        lock (_gate) hidden = _hiddenByUs.Remove(hwnd);
        if (hidden)
        {
            // SW_HIDE 代管的恢复口：最小化方向窗口此刻是 iconic → 以"最小化但可见"现身
            // （任务栏按钮回来）；还原方向窗口已被系统复位 → 原位现身且不抢激活。
            var cmd = NativeMethods.IsIconic(hwnd) ? NativeMethods.SwShowNoActivate : NativeMethods.SwShowna;
            NativeMethods.ShowWindow(hwnd, cmd);
            Logger.Info($"斗篷：0x{hwnd.ToInt64():X} SW_HIDE 代管恢复（{reason}，cmd=0x{cmd:X}）");
            return;
        }
        bool had;
        lock (_gate) had = _cloaked.Remove(hwnd);
        if (!had) return;   // 不是我们斗的：绝不去动别人的可见性
        var zero = 0;
        var hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref zero, sizeof(int));
        if (hr != 0)
            Logger.Warn($"斗篷：0x{hwnd.ToInt64():X} 解除失败 hr=0x{hr:X8}（{reason}）→ 看门狗将重试");
        else
            Logger.Info($"斗篷：0x{hwnd.ToInt64():X} 已解除（{reason}）");
    }

    /// <summary>该窗口是否正被我们斗着（WindowEligibility 的豁免依据：斗篷窗口不该从 Dock/切换器里消失）。</summary>
    public bool IsCloakedByUs(IntPtr hwnd)
    {
        lock (_gate) return _cloaked.ContainsKey(hwnd);
    }

    /// <summary>该窗口是否被 SW_HIDE 代管（斗篷被拒时的隐形代管，见 Cloak；最小化链路用）。</summary>
    public bool IsHiddenByUs(IntPtr hwnd)
    {
        lock (_gate) return _hiddenByUs.Contains(hwnd);
    }

    /// <summary>看门狗：斗了太久还没人解的窗口强制放出来（fail-open）。</summary>
    /// <remarks>
    /// 两个例外不能解：① 窗口正最小化（iconic）——斗篷是我们最小化链路的"隐形代管"，
    /// 解了反而让还原时窗口先闪现；刷新时间戳让它继续挂着，还原动画收尾自会解。
    /// ② 窗口已销毁——DWM 属性随窗口消亡，只需把登记表清掉防泄漏。
    /// </remarks>
    private void SweepExpired()
    {
        List<IntPtr> expired = new();
        lock (_gate)
        {
            var now = NowMs;
            var dead = new List<IntPtr>();
            foreach (var (hwnd, at) in _cloaked)
            {
                if (!NativeMethods.IsWindow(hwnd)) { dead.Add(hwnd); continue; }
                if (NativeMethods.IsIconic(hwnd)) { _cloaked[hwnd] = now; continue; }
                if (now - at > CloakTimeoutMs) expired.Add(hwnd);
            }
            foreach (var hwnd in dead) _cloaked.Remove(hwnd);
            // SW_HIDE 代管表只清死窗口：活着的代管窗口恢复时机归还原链路管（Uncloak），
            // 这里强行现身会把正在播的替身戳穿。
            if (_hiddenByUs.Count > 0)
            {
                var hiddenDead = new List<IntPtr>();
                foreach (var hwnd in _hiddenByUs)
                    if (!NativeMethods.IsWindow(hwnd)) hiddenDead.Add(hwnd);
                foreach (var hwnd in hiddenDead) _hiddenByUs.Remove(hwnd);
            }
        }
        foreach (var hwnd in expired)
        {
            Logger.Warn($"斗篷看门狗：0x{hwnd.ToInt64():X} 超时 {CloakTimeoutMs}ms 未解 → 强制放出（fail-open）");
            Uncloak(hwnd, "看门狗超时");
        }
    }

    // ---------- 生命周期 ----------

    /// <summary>
    /// 退出清理：全部解斗篷 + 全部还原过渡动画。顺序先斗篷后过渡：
    /// 斗着的窗口必须先放出来，用户才不会在退出后看到窗口集体消失。
    /// </summary>
    public void Dispose()
    {
        _watchdog?.Dispose();
        IntPtr[] cloaked;
        IntPtr[] managed;
        IntPtr[] hidden;
        lock (_gate)
        {
            cloaked = _cloaked.Keys.ToArray();
            managed = _noTransition.Keys.ToArray();
            hidden = _hiddenByUs.ToArray();
            _cloaked.Clear();
            _noTransition.Clear();
            _noTransitionFailed.Clear();
            _hiddenByUs.Clear();
        }

        // SW_HIDE 代管的窗口必须先现身再谈退出：否则窗口随进程一起消失，用户会以为窗口丢了
        foreach (var hwnd in hidden)
        {
            var cmd = NativeMethods.IsIconic(hwnd) ? NativeMethods.SwShowNoActivate : NativeMethods.SwShowna;
            NativeMethods.ShowWindow(hwnd, cmd);
        }

        foreach (var hwnd in cloaked)
        {
            var zero = 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref zero, sizeof(int));
        }
        foreach (var hwnd in managed)
        {
            var zero = 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaTransitionsForcedDisabled, ref zero, sizeof(int));
        }
        if (cloaked.Length > 0 || managed.Length > 0)
            Logger.Info($"过渡接管：已清理（解斗篷 {cloaked.Length} 个、还原过渡 {managed.Length} 个）");
    }
}
