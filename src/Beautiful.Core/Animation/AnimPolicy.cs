namespace Beautiful.Core.Animation;

/// <summary>动画种类（每类有自己的时长、自己的门禁）。</summary>
public enum AnimKind
{
    Minimize,
    Restore,
    Maximize,
    Open,
    Close,
    Hide,
}

/// <summary>
/// 一次动画请求。<paramref name="NowMs"/> 由调用方给（单调毫秒），策略本身不读时钟——这样单测能精确摆出
/// "4 个窗口在 200ms 内被最小化"这种批次，而不用 sleep。
/// </summary>
public readonly record struct AnimRequest(
    AnimKind Kind,
    uint ProcessId,
    string ProcessName,
    long NowMs,
    bool Fullscreen = false,
    bool Excluded = false,
    string Source = "未知");

/// <summary>
/// 放行结论。<paramref name="Reason"/> 直接进 <see cref="AnimReport.Skipped"/> 的 <c>因=</c> 字段，
/// 所以用短语（"风暴抑制"而不是"storm suppression"）。
/// </summary>
public readonly record struct AnimDecision(bool Allowed, string Reason, int DurationMs)
{
    public static AnimDecision Deny(string reason) => new(false, reason, 0);
}

/// <summary>
/// 动画门禁（v2.2 §4.5 / v3.0 §4 红线落点）：<b>先问策略、再起渲染</b>。
/// <para>
/// 【为什么门禁必须是纯策略】这些规则全是"该不该播"的判断：全屏里不播、风暴里不播、并发太多不播、
/// 连点同一应用不播。把它们散在渲染代码里，就必然出现"一条路径忘了查全屏"的事故，而且没法单测。
/// 集中到一处之后，渲染器只剩一件事：把已经批准的那一次画出来。
/// </para>
/// <para>
/// <b>时间口径</b>：所有方法都收 <c>nowMs</c>（调用方传单调时钟），内部只存时间戳不读系统时钟。
/// </para>
/// <para>
/// <b>线程</b>：WinEvent 回调可能来自不同消息泵线程，门禁状态用一把锁串行化；锁内只做 O(1) 计算。
/// </para>
/// </summary>
public sealed class AnimPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, long> _lastByProcess = new();
    private readonly Dictionary<uint, List<long>> _opensByProcess = new();
    private readonly List<long> _minimizeTimes = new();

    private AnimParams _params;
    private IReadOnlyCollection<string> _exclusions = Array.Empty<string>();
    private int _active;
    private long _stormUntilMs = long.MinValue;

    public AnimPolicy(AnimParams? parameters = null) => _params = (parameters ?? AnimParams.Default).Normalized();

    public AnimParams Params { get { lock (_gate) return _params; } }

    /// <summary>当前正在跑的幽灵数量（诊断用）。</summary>
    public int ActiveGhosts { get { lock (_gate) return _active; } }

    /// <summary>热加载参数：只换值，不清状态（调参时不该把风暴窗口清掉）。</summary>
    public void UpdateParams(AnimParams parameters)
    {
        lock (_gate) _params = parameters.Normalized();
    }

    /// <summary>排除清单（进程名，可带 .exe；大小写不敏感）。空集合 = 不排除。</summary>
    public void SetExclusions(IReadOnlyCollection<string>? processNames)
    {
        lock (_gate)
        {
            _exclusions = processNames is null
                ? Array.Empty<string>()
                : processNames.Where(s => !string.IsNullOrWhiteSpace(s))
                              .Select(s => s.Trim().ToLowerInvariant())
                              .ToArray();
        }
    }

    /// <summary>
    /// 判定一次请求。放行会<b>同时占一个并发槽</b>，调用方在该次动画结束时必须
    /// <see cref="Complete"/>（否则并发槽会漏，几轮之后所有动画都被"并发上限"挡住）。
    /// </summary>
    public AnimDecision Evaluate(AnimRequest request)
    {
        lock (_gate)
        {
            var p = _params;

            if (request.Fullscreen)
                return AnimDecision.Deny("全屏应用期间不播");

            if (request.Excluded || IsExcluded(request))
                return AnimDecision.Deny("排除清单");

            if (request.Kind == AnimKind.Minimize && InStorm(request.NowMs, p))
                return AnimDecision.Deny("风暴抑制");

            if (_active >= p.MaxConcurrentGhosts)
                return AnimDecision.Deny("并发上限");

            if (p.SameProcessThrottleMs > 0
                && _lastByProcess.TryGetValue(request.ProcessId, out var last)
                && request.NowMs - last < p.SameProcessThrottleMs)
                return AnimDecision.Deny("同进程节流");

            if (request.Kind == AnimKind.Open && OpenBurstExceeded(request, p))
                return AnimDecision.Deny("打开批量（1s 内同进程过多）");

            _active++;
            _lastByProcess[request.ProcessId] = request.NowMs;
            if (_lastByProcess.Count > 512) Prune(request.NowMs, p);

            if (request.Kind == AnimKind.Open)
            {
                if (!_opensByProcess.TryGetValue(request.ProcessId, out var list))
                    _opensByProcess[request.ProcessId] = list = new List<long>(4);
                list.Add(request.NowMs);
            }

            return new AnimDecision(true, "允许", BaseDuration(request.Kind, p));
        }
    }

    /// <summary>该次动画结束（成功、判废、异常都要调；幂等保护见下）。</summary>
    public void Complete(AnimKind kind)
    {
        _ = kind;
        lock (_gate)
        {
            if (_active > 0) _active--;
        }
    }

    /// <summary>
    /// 前台缓存帧的龄判定：<c>&lt;= MaxAge</c> 新鲜（可直接播）、<c>&lt;= Discard</c> 过期但仍可当兜底
    /// （日志里必须写龄）、超过 <c>Discard</c> 判废（画面已经不是"刚才那个窗口"）。
    /// </summary>
    public CacheAge Usability(long ageMs)
    {
        lock (_gate)
        {
            if (ageMs < 0) return CacheAge.Expired;
            if (ageMs <= _params.ForegroundCacheMaxAgeMs) return CacheAge.Fresh;
            if (ageMs <= _params.ForegroundCacheDiscardMs) return CacheAge.Stale;
            return CacheAge.Expired;
        }
    }

    /// <summary>锁屏/注销/explorer 重启：清空时间戳与风暴态（并发槽不清——那里的幽灵由渲染侧负责收）。</summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            _lastByProcess.Clear();
            _opensByProcess.Clear();
            _minimizeTimes.Clear();
            _stormUntilMs = long.MinValue;
        }
    }

    /// <summary>倍率变化时把已记录的时间戳按新阈值重新审视（不需要——阈值只在判定时读，这里仅作文档）。</summary>
    public double EffectiveSpeed(bool slowMotion)
    {
        lock (_gate)
        {
            var mul = _params.SpeedMultiplier;
            if (slowMotion) mul /= _params.ShiftSlowFactor;
            return mul;
        }
    }

    // ---------- 内部 ----------

    private bool InStorm(long nowMs, AnimParams p)
    {
        if (nowMs < _stormUntilMs) return true;

        _minimizeTimes.Add(nowMs);
        var windowStart = nowMs - p.StormWindowMs;
        var drop = 0;
        while (drop < _minimizeTimes.Count && _minimizeTimes[drop] < windowStart) drop++;
        if (drop > 0) _minimizeTimes.RemoveRange(0, drop);

        if (_minimizeTimes.Count >= p.StormCount)
        {
            // 阈值达成：本批从这一次起压制一个风暴窗口。已经播出去的那 1~2 个收不回来——
            // 不为此加 200ms 的防抖延迟（那会让每一次正常最小化都慢半拍），代价如实记在报告里。
            _stormUntilMs = nowMs + p.StormWindowMs;
            _minimizeTimes.Clear();
            return true;
        }
        return false;
    }

    private bool IsExcluded(AnimRequest request)
    {
        if (_exclusions.Count == 0) return false;
        var name = request.ProcessName;
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim().ToLowerInvariant();
        var bare = name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
        foreach (var x in _exclusions)
        {
            var xb = x.EndsWith(".exe", StringComparison.Ordinal) ? x[..^4] : x;
            if (xb == bare) return true;
        }
        return false;
    }

    private bool OpenBurstExceeded(AnimRequest request, AnimParams p)
    {
        if (!_opensByProcess.TryGetValue(request.ProcessId, out var list)) return false;
        var windowStart = request.NowMs - p.OpenBurstWindowMs;
        list.RemoveAll(t => t < windowStart);
        return list.Count >= p.OpenBurstCount;
    }

    private static int BaseDuration(AnimKind kind, AnimParams p) => kind switch
    {
        AnimKind.Minimize => p.Scaled(p.GenieDurationMs),
        AnimKind.Restore => p.Scaled(p.RestoreDurationMs),
        AnimKind.Maximize => p.Scaled(p.MorphDurationMs),
        AnimKind.Open => p.Scaled(p.OpenDurationMs),
        AnimKind.Close => p.Scaled(p.CloseDurationMs),
        AnimKind.Hide => p.Scaled(p.SettleDurationMs),
        _ => p.Scaled(p.GenieDurationMs),
    };

    private void Prune(long nowMs, AnimParams p)
    {
        var horizon = nowMs - Math.Max(p.SameProcessThrottleMs, p.OpenBurstWindowMs) * 2;
        foreach (var key in _lastByProcess.Where(kv => kv.Value < horizon).Select(kv => kv.Key).ToList())
            _lastByProcess.Remove(key);
        foreach (var key in _opensByProcess.Keys.ToList())
        {
            var list = _opensByProcess[key];
            list.RemoveAll(t => t < horizon);
            if (list.Count == 0) _opensByProcess.Remove(key);
        }
    }
}

/// <summary>缓存帧的可用性（三档：新鲜 / 过期可用 / 判废）。</summary>
public enum CacheAge
{
    Fresh,
    Stale,
    Expired,
}
