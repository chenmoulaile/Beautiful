using System;
using System.Threading;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;

namespace Beautiful.App.Switcher;

// Alt+Tab 拦截机制参考 stianhoiland/cmdtab (MIT)；实现为 C# 自写。

/// <summary>
/// Alt+Tab 接管器（Task 11）：在低级键盘钩子里识别"触发修饰键 + Tab / Shift+Tab / 方向键"序列，
/// **吞掉**这些按键（返回 1）使系统原生切换器不再出现，并把语义转成事件投递给 UI：
/// <see cref="OpenRequested"/> / <see cref="NextRequested"/> / <see cref="PreviousRequested"/> /
/// <see cref="CommitRequested"/> / <see cref="CancelRequested"/>。
///
/// 【回调纪律（关键）】钩子回调只做三件事：更新修饰键状态位、累加待处理计数、把"排空"动作
/// <see cref="Dispatcher.BeginInvoke"/> 出去，然后立刻返回。所有面板构建/图标/窗口切换都在 UI 线程的
/// 排空回调里执行——绝不在回调内做可能触发 <c>LowLevelHooksTimeout</c> 摘钩的工作
/// （动机详见 <see cref="LowLevelKeyboardHook"/> 注释）。
///
/// 【为什么不吞 Alt 本身】只吞 Tab/方向键/Esc：Alt 的按下与抬起放行给系统，键盘状态机保持自洽
/// （吞掉 Alt 抬起会让系统认为 Alt 一直按住 → 应用菜单聚焦、后续快捷键错乱）。原生切换器需要
/// "Alt 按住 + Tab"才触发，Tab 被吞即足以压制它。
///
/// 【注入键一律放行】回调首行检查 <c>LLKHF_INJECTED</c>：凡 keybd_event/SendInput 合成的按键
/// （我们自己的 TapAlt 兜底、密码管理器自动填充、屏幕键盘、远程输入）既不参与切换器的状态位跟踪，
/// 也不被切换器吞掉。否则注入的 Alt 会开出幽灵会话（提交后到下一次真按键之间吞掉一切），注入的
/// Alt+Tab 则被吞后目标应用永远收不到（评审发现 I2）。应急组合 Win+Alt+K 因此只认真实体键——注入键无法解除接管。
/// <b>唯一的例外是启动器组合键</b>（<see cref="LauncherRequested"/>）：它是一次无状态的"打开面板"边沿，
/// 没有会话可被污染，所以注入键同样触发；但注入键的按下/抬起一律不吞——那是别人主动送进来的序列。
/// 这一条例外在 <see cref="OnKey"/> ⓪ 里实现并就地写明理由。
///
/// 【自愈】会话的开启与收束以 **OS 报告的修饰键状态**（<c>LLKHF_ALTDOWN</c> / GetAsyncKeyState）
/// 为权威，不信任缓存位：漏收一个 key-up 边缘最多错过一次接管，绝不会"陈旧位永久吞掉普通 Tab"
/// （评审发现 I1；<see cref="Start"/> 装钩时亦清零全部状态位）。
///
/// 【应急停用】硬编码 Win+Alt+K：无论其它状态如何，立即在进程内关闭接管并卸载钩子，
/// 原生 Alt+Tab 当场恢复。用户被自己的设置卡住时这是唯一出口，因此不读取任何设置项、不可配置。
/// </summary>
public sealed class AltTabHook : IDisposable
{
    private const int VkK = 0x4B; // 应急停用组合 Win+Alt+K

    private readonly LowLevelKeyboardHook _keyboard;
    private readonly Dispatcher _ui;
    private readonly Action _drain; // 缓存委托：热路径每次只投递这一个已有实例，零闭包分配

    // ---- 左右修饰键分别跟踪（左 Alt 与右 Alt 是不同的 VK，松开其一不应结束会话）----
    private bool _altLeft, _altRight, _ctrlLeft, _ctrlRight, _winLeft, _winRight, _shiftLeft, _shiftRight;
    private bool AltDown => _altLeft || _altRight;
    private bool CtrlDown => _ctrlLeft || _ctrlRight;
    private bool WinDown => _winLeft || _winRight;
    private bool ShiftDown => _shiftLeft || _shiftRight;

    private bool _switching;     // 已吞下首个 Tab，会话进行中（等触发键抬起提交）
    private bool _nativeSession; // 本次会话已放行给原生（全屏直通），后续按键一并放行

    // ---- 跨线程投递：计数而非布尔，保证快速连按 Tab 的步数不丢 ----
    private int _pendingOpen, _pendingNext, _pendingPrev, _pendingCommit, _pendingCancel, _pendingEmergency;
    private int _pendingPassthrough; // 全屏直通发生次数（回调里只累加，日志在 Drain 里写——回调内禁 IO）
    private int _drainScheduled;

    private volatile int _enabledFlag = 1;
    private volatile int _passthroughFlag = 1;

    // 触发组合的修饰键要求（默认 Alt；来自设置项 SwitcherHotkey）
    private volatile int _needAlt = 1, _needCtrl, _needWin;

    // ---- 启动器触发组合（v3 T7，来自设置项 LauncherHotkey；默认 Ctrl+Alt+Space）----
    // 主键 0 = 停用（设置里关了启动器）。修饰键要求与主键都是"位/整数"，回调里只做比较，不查表。
    private volatile int _launcherMainVk;
    private volatile int _launcherNeedCtrl = 1, _launcherNeedAlt = 1, _launcherNeedWin, _launcherNeedShift;
    private bool _launcherTriggerDown;   // 已吞下按下，等着吞掉配对的那个抬起
    private int _pendingLauncher;

    /// <summary>
    /// 是否启用接管。false 时所有按键原样放行（钩子仍在线，仅为保住应急停用组合）。
    /// 由设置项 <c>SwitcherEnabled</c> 与应急停用共同驱动。
    /// </summary>
    public bool Enabled
    {
        get => _enabledFlag != 0;
        set => Interlocked.Exchange(ref _enabledFlag, value ? 1 : 0);
    }

    /// <summary>前台为独占/无边框全屏应用时，本次 Alt+Tab 放行给系统（游戏/反作弊安全兜底）。</summary>
    public bool FullscreenPassthrough
    {
        get => _passthroughFlag != 0;
        set => Interlocked.Exchange(ref _passthroughFlag, value ? 1 : 0);
    }

    /// <summary>当前是否处于切换会话（面板态判定/诊断用）。</summary>
    public bool IsSwitching => _switching;

    public event Action? OpenRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? CommitRequested;
    public event Action? CancelRequested;

    /// <summary>启动器组合键被按下（v3 T7）：宿主据此开/收 Spotlight 式启动面板。</summary>
    public event Action? LauncherRequested;


    /// <summary>应急停用（Win+Alt+K）触发：接管已关闭且 <see cref="UnhookRequested"/> 已排队，宿主据此同步设置/UI。</summary>
    public event Action? EmergencyDisabled;

    /// <summary>
    /// 应急停用后请求卸载钩子的动作（由宿主 SwitcherService 注入）。卸载必须在 UI 线程、
    /// 且在钩子回调**之外**进行——在自身回调里 Unhook 是未定义行为，故走 BeginInvoke。
    /// </summary>
    public Action? UnhookRequested { get; set; }

    public AltTabHook()
    {
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _drain = Drain;
        _keyboard = new LowLevelKeyboardHook(OnKey);
    }

    /// <summary>
    /// 按 <c>SwitcherHotkey</c> 解析触发组合的修饰键要求（"alt+tab" / "ctrl+alt+tab" / "win+tab"）。
    /// 主键固定 Tab；解析不出任何修饰键时回退为 Alt（绝不出现"无条件吞 Tab"——那会废掉所有应用的制表键）。
    /// </summary>
    public void SetTriggerHotkey(string? hotkey)
    {
        bool alt = false, ctrl = false, win = false;
        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            foreach (var raw in hotkey.Split('+', '-', ' '))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "alt": case "option": case "menu": alt = true; break;
                    case "ctrl": case "control": ctrl = true; break;
                    case "win": case "windows": case "meta": case "super": case "cmd": win = true; break;
                        // shift 只表示"反向步进"、tab 是主键：两者不作为触发条件
                }
            }
        }
        if (!alt && !ctrl && !win) alt = true;
        Interlocked.Exchange(ref _needAlt, alt ? 1 : 0);
        Interlocked.Exchange(ref _needCtrl, ctrl ? 1 : 0);
        Interlocked.Exchange(ref _needWin, win ? 1 : 0);
    }

    /// <summary>
    /// 设置启动器触发组合（v3 T7）。<paramref name="enabled"/>=false 时主键归 0 → 回调里这条分支整体退出，
    /// 键盘路径回到"只有切换器在看"。解析一律走 <see cref="LauncherHotkey"/>（Core，含"必须带修饰键"
    /// 这条硬约束与单测），这里只把结果落成钩子能立刻比较的位/键码。
    /// 主键名到虚拟键码的映射见 <see cref="VirtualKeyOf"/>：Core 不认识 VK，App 侧不认拼音表，各管一段。
    /// </summary>
    public void SetLauncherHotkey(string? hotkey, bool enabled)
    {
        if (!enabled)
        {
            Interlocked.Exchange(ref _launcherMainVk, 0);
            Logger.Info("启动器：热键已停用（设置里关了启动器）");
            return;
        }
        if (!LauncherHotkey.TryParse(hotkey, out var gesture))
        {
            // 走到这里说明配置绕过了 Normalized（例如旧版本留下的脏串）：回落默认组合而不是停摆，
            // 用户至少还能打开面板；原因写进日志，不在按键路径上弹任何东西。
            if (!LauncherHotkey.TryParse(LauncherHotkey.Default, out gesture))
            {
                Interlocked.Exchange(ref _launcherMainVk, 0);
                Logger.Warn($"启动器：热键 \"{hotkey}\" 无法解析，且默认组合也解析失败 → 本次不注册");
                return;
            }
            Logger.Warn($"启动器：热键 \"{hotkey}\" 无法解析 → 回落 {LauncherHotkey.Default}");
        }
        var vk = VirtualKeyOf(gesture.Key);
        if (vk == 0)
        {
            Interlocked.Exchange(ref _launcherMainVk, 0);
            Logger.Warn($"启动器：热键 \"{hotkey}\" 的主键 {gesture.Key} 没有键码映射 → 本次不注册");
            return;
        }
        Interlocked.Exchange(ref _launcherNeedCtrl, gesture.Ctrl ? 1 : 0);
        Interlocked.Exchange(ref _launcherNeedAlt, gesture.Alt ? 1 : 0);
        Interlocked.Exchange(ref _launcherNeedWin, gesture.Win ? 1 : 0);
        Interlocked.Exchange(ref _launcherNeedShift, gesture.Shift ? 1 : 0);
        Interlocked.Exchange(ref _launcherMainVk, vk);
        Logger.Info($"启动器：热键已注册 {gesture.ToText()}（VK=0x{vk:X2}）");
    }

    /// <summary>
    /// 主键名 → 虚拟键码；认不出返回 0（调用方据此不注册）。字母/数字/F1-F12 按码位算，
    /// 其余按键名给常量（与 <see cref="LauncherHotkey.KeyNames"/> 一一对应，多一条都不留）。
    /// </summary>
    private static int VirtualKeyOf(string key) => key switch
    {
        "space" => NativeMethods.VkSpace,
        "enter" => NativeMethods.VkReturn,
        "tab" => NativeMethods.VkTab,
        "escape" => NativeMethods.VkEscape,
        "backspace" => NativeMethods.VkBack,
        "home" => NativeMethods.VkHome,
        "end" => NativeMethods.VkEnd,
        "pageup" => NativeMethods.VkPrior,
        "pagedown" => NativeMethods.VkNext,
        "left" => NativeMethods.VkLeft,
        "right" => NativeMethods.VkRight,
        "up" => NativeMethods.VkUp,
        "down" => NativeMethods.VkDown,
        "minus" => NativeMethods.VkOemMinus,
        "equal" => NativeMethods.VkOemPlus,
        "comma" => NativeMethods.VkOemComma,
        "period" => NativeMethods.VkOemPeriod,
        "slash" => NativeMethods.VkOem2,
        "backslash" => NativeMethods.VkOem5,
        "semicolon" => NativeMethods.VkOem1,
        "quote" => NativeMethods.VkOem7,
        "leftbracket" => NativeMethods.VkOem4,
        "rightbracket" => NativeMethods.VkOem6,
        _ => key.Length switch
        {
            1 when key[0] is >= 'a' and <= 'z' => 'A' + (key[0] - 'a'),
            1 when key[0] is >= '0' and <= '9' => key[0],
            _ when key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out var n)
                 && n is >= 1 and <= 12 => NativeMethods.VkF1 + n - 1,
            _ => 0,
        },
    };

    /// <summary>
    /// 安装钩子（须在 UI 线程）。失败返回 false，调用方据此降级为"不接管"。
    /// 装钩前先清零全部修饰键/会话状态位：钩子看不见安装之前发生的按下/抬起边缘，
    /// 带着陈旧位上线会让"改热键重装"（App.ApplySwitcherHotkey 的注释承诺）变成空话（评审发现 I1）。
    /// </summary>
    public bool Start()
    {
        ResetTriggerState();
        return _keyboard.Install();
    }

    /// <summary>清零左右修饰键与会话标志（真实按键状态以 OS 侧为准，后续边沿会自行重建这些位）。</summary>
    private void ResetTriggerState()
    {
        _altLeft = _altRight = _ctrlLeft = _ctrlRight = _winLeft = _winRight = _shiftLeft = _shiftRight = false;
        _switching = false;
        _nativeSession = false;
        // 启动器的"按下已吞、等抬起"配对位一并清掉：残留会让下一次普通主键抬起被误吞
        _launcherTriggerDown = false;
    }


    /// <summary>卸载钩子（幂等；转回安装线程）。应急停用与关开关走这里，Dispose 亦复用。</summary>
    public void Stop() => _keyboard.Uninstall();

    /// <summary>钩子是否在线。</summary>
    public bool IsStarted => _keyboard.IsInstalled;

    /// <summary>累计回调超时/异常次数（诊断：证明钩子没被系统摘掉、回调始终够快）。</summary>
    public long SlowCallbackCount => _keyboard.SlowCallbackCount;

    public void Dispose()
    {
        Enabled = false;
        _keyboard.Dispose(); // 卸载钩子（内部转回安装线程）+ 释放委托的 GCHandle
        OpenRequested = null; NextRequested = null; PreviousRequested = null;
        CommitRequested = null; CancelRequested = null; EmergencyDisabled = null;
        LauncherRequested = null;
        UnhookRequested = null;
    }

    // ---------- 钩子回调（热路径：位判断 + 计数 + 至多一次投递） ----------

    private bool OnKey(int msg, uint vk, uint flags)
    {
        var isDown = msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isUp = msg is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        var injected = (flags & NativeMethods.LlkhfInjected) != 0;

        // ⓪ 启动器组合键。这一支刻意排在"注入键一律放行"**之前**，原因与切换器不同：
        //    切换器要跟踪 Alt 会话状态，注入的 Alt 会开出幽灵会话，所以注入键必须完全对它隐形；
        //    而"开启动器"是一次**无状态的边沿动作**——没有会话、没有修饰键残留可被污染，注入键触发它
        //    与真键盘触发它的后果完全一致（也让验收脚本能用 keybd_event 走同一条判定，见 scripts/launcher.ps1）。
        //    但吞键只对真实键生效：注入键是别人（我们的脚本/输入法/远程桌面）主动送进来的序列，
        //    把它吞掉等于替别人的前台应用做主。
        if (!isDown && !isUp) return false;          // WM_SYSCHAR 等字符消息：放行
        if (TryLauncherTrigger(vk, isDown, injected)) return true;
        if (injected) return false;                  // 其余注入键：原样放行且不碰任何状态位

        // ① 修饰键状态跟踪；修饰键自身一律放行（见类型注释"为什么不吞 Alt 本身"）
        if (TrackModifiers(vk, isDown)) return false;


        // ② 应急停用：Win+Alt+K。排在 Enabled 判定之前——被卡住时这条是唯一出口
        if (isDown && vk == VkK && WinDown && AltDown)
        {
            EmergencyDisableInProcess();
            return true; // 吞掉这次 K，避免落到前台应用（如资源管理器/浏览器的快捷键）
        }

        if (!Enabled) { CloseSessionIfTriggerReleased(); return false; }

        // ③ 主键 Tab
        if (vk == NativeMethods.VkTab)
        {
            if (!isDown) return _switching; // 会话中的 Tab 抬起一并吞掉，非会话放行

            if (_nativeSession) return false; // 本会话已交原生，后续 Tab 不再插手
            // OS 的修饰键状态是权威（缓存位可能因漏收 key-up 永久卡住）：会话开启与自愈都只看它。
            // 漏收一个边缘的代价仅是"这一次不接管"（安全方向），而不是"以后永远吞 Tab"（评审发现 I1）。
            var altHeldPerOs = (flags & NativeMethods.LlkhfAltDown) != 0;
            var heldPerOs = TriggerHeldPerOs(altHeldPerOs);
            if (_switching && !heldPerOs)
            {
                _switching = false; // 系统说触发键已松开 → 收束会话，本次放行（陈旧位死锁就此打破）
                return false;
            }
            if (!_switching && !heldPerOs) return false; // 普通制表键

            if (!_switching)
            {
                if (FullscreenPassthrough &&
                    WindowEligibility.IsExclusiveFullscreen(NativeMethods.GetForegroundWindow()))
                {
                    _nativeSession = true;
                    Interlocked.Add(ref _pendingPassthrough, 1); // 日志留到 Drain 写，回调内不做 IO
                    ScheduleDrain();
                    return false; // 本次 Alt+Tab 交给系统（全屏/游戏）
                }
                _switching = true;
                Interlocked.Add(ref _pendingOpen, 1);
            }
            if (ShiftDown) Interlocked.Add(ref _pendingPrev, 1);
            else Interlocked.Add(ref _pendingNext, 1);
            ScheduleDrain();
            return true;
        }

        // ④ 会话中的方向键 / Esc
        if (_switching && !_nativeSession)
        {
            if (isDown)
            {
                switch (vk)
                {
                    case NativeMethods.VkRight: case NativeMethods.VkDown:
                        Interlocked.Add(ref _pendingNext, 1); ScheduleDrain(); return true;
                    case NativeMethods.VkLeft: case NativeMethods.VkUp:
                        Interlocked.Add(ref _pendingPrev, 1); ScheduleDrain(); return true;
                    case NativeMethods.VkEscape:
                        _switching = false;
                        Interlocked.Add(ref _pendingCancel, 1); ScheduleDrain(); return true;
                }
            }
            else if (vk is NativeMethods.VkLeft or NativeMethods.VkRight or NativeMethods.VkUp
                        or NativeMethods.VkDown or NativeMethods.VkEscape)
            {
                return true; // 会话内方向键/Esc 的抬起一并吞掉：前台应用不应收到半套按键
            }
        }

        return false;
    }

    /// <summary>
    /// 启动器组合键判定（回调热路径：一次键码比较 + 至多几次 GetAsyncKeyState 纯读，零分配零 IO）。
    /// 返回 true = 本次按键已被本分支接管（真实键的按下与抬起都被吞）。
    /// <para>
    /// 【为什么按下与抬起要配对吞】面板打开即夺取焦点，若把 Ctrl+Alt+Space 的"空格抬起"放行出去，
    /// 焦点已切换到搜索框的应用会收到一次孤立的空格 —— 恰好落进用户刚要输入的第一个字符位。
    /// 配对位 <see cref="_launcherTriggerDown"/> 只由按下置位、由抬起或重装清零，因此不满足修饰键要求时
    /// 的主键（普通空格）绝不会被误吞。
    /// </para>
    /// </summary>
    private bool TryLauncherTrigger(uint vk, bool isDown, bool injected)
    {
        var main = _launcherMainVk;
        if (main == 0 || vk != (uint)main) return false;
        if (!isDown)
        {
            var swallow = _launcherTriggerDown;
            _launcherTriggerDown = false;
            return swallow;
        }
        if (!LauncherModifiersHeld()) return false;
        Interlocked.Add(ref _pendingLauncher, 1);
        ScheduleDrain();
        _launcherTriggerDown = !injected;   // 注入键只投递、不吞（见 OnKey ⓪ 的裁定）
        return !injected;
    }

    /// <summary>
    /// 启动器要求的修饰键是否按住（与 <see cref="TriggerHeldPerOs"/> 同口径：只信 OS 的实时状态，
    /// 不信缓存位——缓存位漏收一次抬起就会永久"以为按着"，把用户的主键吞到下一次重启）。
    /// </summary>
    private bool LauncherModifiersHeld()
        => (_launcherNeedCtrl == 0 || IsDownOs(NativeMethods.VkLControl) || IsDownOs(NativeMethods.VkRControl))
         && (_launcherNeedAlt == 0 || IsDownOs(NativeMethods.VkLMenu) || IsDownOs(NativeMethods.VkRMenu))
         && (_launcherNeedWin == 0 || IsDownOs(NativeMethods.VkLWin) || IsDownOs(NativeMethods.VkRWin))
         && (_launcherNeedShift == 0 || IsDownOs(NativeMethods.VkLShift) || IsDownOs(NativeMethods.VkRShift));

    /// <summary>更新左右修饰键状态位；返回 true 表示本次按键就是修饰键（不吞）。所有 Alt/Tab 组合都依赖这些位，必须逐键跟踪。</summary>
    private bool TrackModifiers(uint vk, bool down)
    {
        switch (vk)
        {
            case NativeMethods.VkLMenu: _altLeft = down; break;
            case NativeMethods.VkRMenu: _altRight = down; break;
            case NativeMethods.VkLControl: _ctrlLeft = down; break;
            case NativeMethods.VkRControl: _ctrlRight = down; break;
            case NativeMethods.VkLShift: _shiftLeft = down; break;
            case NativeMethods.VkRShift: _shiftRight = down; break;
            case NativeMethods.VkLWin: _winLeft = down; break;
            case NativeMethods.VkRWin: _winRight = down; break;
            default: return false;
        }
        if (!down && !TriggerHeld(altHeldPerOs: false))
        {
            // 触发键松开 → 会话收束：等价 macOS"松开 Cmd 即切到选中项"
            if (_switching && !_nativeSession)
            {
                _switching = false;
                Interlocked.Add(ref _pendingCommit, 1);
                ScheduleDrain();
            }
            else CloseSessionIfTriggerReleased();
        }
        return true;
    }

    /// <summary>配置的触发修饰键是否仍按住（Shift 不计入条件，只用于反向步进）。</summary>
    private bool TriggerHeld(bool altHeldPerOs)
        => (_needAlt == 0 || _altLeft || _altRight || altHeldPerOs)
         && (_needCtrl == 0 || _ctrlLeft || _ctrlRight)
         && (_needWin == 0 || _winLeft || _winRight);

    /// <summary>
    /// 触发键是否按住的 **OS 权威版**（评审发现 I1）：Alt 用 KBDLLHOOKSTRUCT 的 LLKHF_ALTDOWN 位，
    /// Ctrl/Win 用 GetAsyncKeyState（同为纯读操作，回调预算内），完全不读缓存位——缓存位漏收边沿后
    /// 永久为真，OS 状态不会。默认 alt+tab 配置下短路求值，不产生任何额外 P/Invoke。
    /// </summary>
    private bool TriggerHeldPerOs(bool altHeldPerOs)
        => (_needAlt == 0 || altHeldPerOs)
         && (_needCtrl == 0 || IsDownOs(NativeMethods.VkLControl) || IsDownOs(NativeMethods.VkRControl))
         && (_needWin == 0 || IsDownOs(NativeMethods.VkLWin) || IsDownOs(NativeMethods.VkRWin));

    private static bool IsDownOs(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Enabled=false 时会话标志必须收束，否则"关闭后按 Alt+Tab、再松开 Alt"会补发一个陈旧的 Commit。
    /// </summary>
    private void CloseSessionIfTriggerReleased()
    {
        if ((_switching || _nativeSession) && !TriggerHeld(altHeldPerOs: false))
        {
            _switching = false;
            _nativeSession = false;
        }
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 1) return; // 已在队列里
        try { _ui.BeginInvoke(DispatcherPriority.Input, _drain); }
        catch { Interlocked.Exchange(ref _drainScheduled, 0); } // Dispatcher 关停：放弃本次投递
    }

    /// <summary>
    /// UI 线程排空：按 应急 → Open → 净步进 → Cancel/Commit 的顺序回放事件。
    /// 计数一次性读清；步进取净位移后循环触发，面板只需实现"前进一步"。
    /// 首个 Tab 同时产生 Open 与一步 Next（对齐原生/macOS：按下即高亮上一个应用）。
    /// </summary>
    private void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);

        var open = Interlocked.Exchange(ref _pendingOpen, 0);
        var next = Interlocked.Exchange(ref _pendingNext, 0);
        var prev = Interlocked.Exchange(ref _pendingPrev, 0);
        var commit = Interlocked.Exchange(ref _pendingCommit, 0);
        var cancel = Interlocked.Exchange(ref _pendingCancel, 0);
        var emergency = Interlocked.Exchange(ref _pendingEmergency, 0);
        var launcher = Interlocked.Exchange(ref _pendingLauncher, 0);
        var passthrough = Interlocked.Exchange(ref _pendingPassthrough, 0);
        if (passthrough > 0)
            Logger.Info($"切换器：前台为全屏应用，本次 Alt+Tab 交给系统原生切换（全屏直通，累计 {passthrough} 次）");

        if (emergency > 0)
        {
            Logger.Info("切换器：应急停用（Win+Alt+K）生效，原生 Alt+Tab 已恢复；如需重新启用请在设置里打开开关");
            try { UnhookRequested?.Invoke(); }
            catch (Exception ex) { Logger.Warn($"切换器：应急卸载钩子失败: {ex.Message}"); }
            EmergencyDisabled?.Invoke();
            return; // 挂起的会话动作全部丢弃：面板该关，且不能再提交
        }

        // 启动器排在会话动作之前投递：同一次排空里若两者都攒下了，先开面板再回放步进——
        // 面板显示即夺取前台，把步进留在它之后就不会落到一个已经失焦的旧会话上。
        if (launcher > 0) LauncherRequested?.Invoke();

        if (open > 0) OpenRequested?.Invoke();

        var net = next - prev; // 快速连按 8 次 Tab 与"排空一次"等价，但必须走满步数
        if (net > 0) for (int i = 0; i < net; i++) NextRequested?.Invoke();
        else if (net < 0) for (int i = 0; i < -net; i++) PreviousRequested?.Invoke();

        if (cancel > 0) CancelRequested?.Invoke();
        else if (commit > 0) CommitRequested?.Invoke();
    }

    /// <summary>
    /// 应急停用的进程内动作：置 Enabled=false、收束会话、请求卸载钩子（转 UI 线程，
    /// 严禁在钩子回调内同步 Unhook 自身），并投递告警事件。
    /// </summary>
    private void EmergencyDisableInProcess()
    {
        Enabled = false;
        ResetTriggerState(); // 状态位/会话标志全清（与装钩同一套收束）
        Interlocked.Add(ref _pendingEmergency, 1);
        ScheduleDrain();
    }
}
