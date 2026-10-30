namespace Beautiful.Core.Layout;

/// <summary>屏幕四角（以显示器矩形的物理像素为准，副屏负坐标同样成立）。</summary>
public enum HotCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// 触发角的纯几何与驻留状态机（v4 Phase 5-T2）。App 侧只做 GetCursorPos 轮询与动作落地；
/// "命中哪个角、驻留够不够久、触发后要不要先离开再武装"全部在这里，可脱离 UI 单测。
/// </summary>
public static class HotCornerPlan
{
    public const int DefaultHitSizePx = 6;
    public const int DefaultDwellMs = 300;

    /// <summary>光标是否落在该显示器的四角热区；不在返回 null。</summary>
    public static HotCorner? Hit(PhysicalRect monitor, int x, int y, int hitSizePx = DefaultHitSizePx)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return null;
        var size = Math.Clamp(hitSizePx, 1, 64);
        if (x < monitor.Left || x >= monitor.Right || y < monitor.Top || y >= monitor.Bottom) return null;
        var left = x - monitor.Left < size;
        var right = monitor.Right - 1 - x < size;
        var top = y - monitor.Top < size;
        var bottom = monitor.Bottom - 1 - y < size;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => HotCorner.TopLeft,
            (_, true, true, _) => HotCorner.TopRight,
            (true, _, _, true) => HotCorner.BottomLeft,
            (_, true, _, true) => HotCorner.BottomRight,
            _ => null
        };
    }
}

/// <summary>
/// 驻留状态机：同一角连续命中满 <c>dwellMs</c> 才触发一次；触发后必须先离开该角才能再次武装。
/// <para>时间由调用方传入（Environment.TickCount64），本类不读时钟，单测可精确控制。</para>
/// </summary>
public sealed class HotCornerDwellState
{
    private HotCorner? _corner;
    private long _enteredAt;
    private bool _fired;

    /// <summary>当前命中的角（null = 不在任何热区）。</summary>
    public HotCorner? Current => _corner;

    /// <summary>
    /// 推进一帧。返回非 null = 本次刚好满足驻留，调用方应执行一次动作。
    /// 命中变化/离开热区都会重置；触发后保持 fired，直到离开再回来。
    /// </summary>
    public HotCorner? Update(HotCorner? hit, long nowMilliseconds, int dwellMs = HotCornerPlan.DefaultDwellMs)
    {
        if (hit != _corner)
        {
            _corner = hit;
            _enteredAt = nowMilliseconds;
            _fired = false;
            return null;
        }
        if (hit is null || _fired) return null;
        if (nowMilliseconds - _enteredAt < Math.Max(0, dwellMs)) return null;
        _fired = true;
        return hit;
    }

    public void Reset()
    {
        _corner = null;
        _enteredAt = 0;
        _fired = false;
    }
}
