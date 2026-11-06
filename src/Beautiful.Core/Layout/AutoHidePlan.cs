namespace Beautiful.Core.Layout;

/// <summary>
/// 自动隐藏的"唤出方式"（Dock 滑下去之后，鼠标怎么把它唤回来）。
/// </summary>
public enum AutoHideRevealMode
{
    /// <summary>触底立即唤出（默认，与历史行为一致）。</summary>
    Touch,

    /// <summary>触底需停留一小段时间才唤出（防误触：路过底边不弹）。</summary>
    Dwell,

    /// <summary>边缘二次触发：触底 → 离开 → 窗口期内再触底才唤出。</summary>
    Double
}

/// <summary>
/// 自动隐藏的两只纯状态机（P3-T5 隐藏模式的可配化）。App 侧只做 GetCursorPos 轮询与
/// SetWindowPos 落地；"离开多久算够、触底停多久、二次触发怎么武装"全部在这里，
/// 时间由调用方传入（Environment.TickCount64），本文件不读时钟，可脱离 UI 单测。
/// </summary>
public static class AutoHidePlan
{
    /// <summary>光标离开后到滑下去的默认延迟（ms）——与历史硬编码值同源，默认行为不变。</summary>
    public const int DefaultHideDelayMs = 500;

    /// <summary>隐藏延迟的合法区间（ms）：0 = 立即滑下；上限 5s（再慢就不像"自动"隐藏了）。</summary>
    public const int MinHideDelayMs = 0;
    public const int MaxHideDelayMs = 5000;

    /// <summary>dwell 唤出需要的触底停留时长（ms）。</summary>
    public const int RevealDwellMs = 300;

    /// <summary>double 唤出的"再触底"有效窗口（ms）：从离开触底区那一刻起算，超时则重新武装。</summary>
    public const int RevealReentryWindowMs = 2000;

    /// <summary>唤出方式串 → 枚举（未知值兜 Touch，与历史行为一致）。</summary>
    public static AutoHideRevealMode RevealModeOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "dwell" => AutoHideRevealMode.Dwell,
        "double" => AutoHideRevealMode.Double,
        _ => AutoHideRevealMode.Touch
    };
}

/// <summary>
/// 隐藏延迟状态机：光标在 Dock 上 → 不隐藏并清零计时；离开后满 <c>delayMs</c> → 报告隐藏。
/// 与 DockWindow 旧实现（_cursorLeftAt + 硬编码 500ms）逐语义一致，只是阈值可配、逻辑可测。
/// </summary>
public sealed class AutoHideDelayState
{
    private long _leftAt = -1;

    /// <summary>推进一拍。inside=true 永远返回 false 并重置；离开满 delayMs 返回 true（可重复返回，调用方以自身隐藏态幂等）。</summary>
    public bool Update(bool inside, long nowMilliseconds, int delayMs = AutoHidePlan.DefaultHideDelayMs)
    {
        if (inside)
        {
            _leftAt = -1;
            return false;
        }
        if (_leftAt < 0) _leftAt = nowMilliseconds;
        var delay = Math.Max(0, delayMs);
        return nowMilliseconds - _leftAt >= delay;
    }

    public void Reset() => _leftAt = -1;
}

/// <summary>
/// 唤出状态机：Dock 已滑下（留 4px 缝）后，按唤出方式判定"这一次触底要不要滑回"。
/// 每次触发只报一次；DockWindow 在滑回时调 <see cref="Reset"/>，超时/换模式也 Reset。
/// </summary>
public sealed class AutoHideRevealState
{
    private AutoHideRevealMode _mode = AutoHideRevealMode.Touch;
    private long _hitAt = -1;      // dwell：本段连续触底的起点
    private long _leftAt = -1;     // double：离开触底区的时刻（仅离开跳变沿记录，窗口从那一刻起算）
    private bool _wasHit;          // 上一拍是否触底（识别离开跳变沿）
    private bool _armed;           // double：已完成一次触底武装
    private bool _fired;           // 本轮已触发（触发后须 Reset 才能再次武装）

    public AutoHideRevealState(AutoHideRevealMode mode = AutoHideRevealMode.Touch) => _mode = mode;

    public void SetMode(AutoHideRevealMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Reset();
    }

    /// <summary>推进一拍。hit = 光标在触底命中区。返回 true = 本次应滑回。</summary>
    public bool Update(bool hit, long nowMilliseconds)
    {
        switch (_mode)
        {
            case AutoHideRevealMode.Touch:
                if (!hit) { _fired = false; return false; }
                if (_fired) return false;
                _fired = true;
                return true;

            case AutoHideRevealMode.Dwell:
                if (!hit) { _hitAt = -1; _fired = false; return false; }
                if (_fired) return false;
                if (_hitAt < 0) { _hitAt = nowMilliseconds; return false; }
                if (nowMilliseconds - _hitAt >= AutoHidePlan.RevealDwellMs)
                {
                    _fired = true;
                    return true;
                }
                return false;

            default: // Double
                if (!hit)
                {
                    // 只在"已武装 → 离开"的跳变沿记窗口起点：持续离开不得刷新它，
                    // 否则 2s 窗口永不过期，二次触发退化成"随便哪两次触底都行"。
                    if (_wasHit && _armed && !_fired) _leftAt = nowMilliseconds;
                    _wasHit = false;
                    return false;
                }
                _wasHit = true;
                if (_fired) return false;
                if (_armed && _leftAt >= 0 && nowMilliseconds - _leftAt <= AutoHidePlan.RevealReentryWindowMs)
                {
                    _fired = true;
                    return true;
                }
                // 首次触底（或窗口已超时）：重新武装，等下一次离开
                _armed = true;
                _leftAt = -1;
                return false;
        }
    }

    public void Reset()
    {
        _hitAt = -1;
        _leftAt = -1;
        _wasHit = false;
        _armed = false;
        _fired = false;
    }
}
