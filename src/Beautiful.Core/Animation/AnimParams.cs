using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beautiful.Core.Animation;

/// <summary>
/// 动画全部可调参数的<b>单一事实源</b>（v2.2 §5 参数表落成 token）。
/// <para>
/// 【为什么参数必须离开 <c>const</c>】v4.3 之前每个时长都是 <c>MinimizeAnimator</c> 里的
/// <c>const int</c>：改 20ms 要重编译、重发布、重装，而"灯罩收得够不够狠"这种口味问题
/// 只能靠看着改。macOS 观感的最后一毫米是调出来的，不是想出来的——所以参数进 Core（纯数据、
/// 可单测），运行期从 <c>config/anim-params.json</c> 热加载，AnimLab 与产品共用同一份。
/// </para>
/// <para>
/// 【为什么放在 Core 而不是 App】参数与曲线、判废策略同属"纯函数的地盘"：没有窗口、没有 DWM、
/// 没有 DPI，才能在没有桌面的机器上断言"速度倍率 2.0 时 500ms 变 250ms"。App 只负责读文件与把
/// 值传给渲染器。
/// </para>
/// </summary>
public sealed record AnimParams
{
    // ---------- genie（最小化/还原）----------

    /// <summary>最小化收拢总时长。macOS 约 0.45-0.5s；短于此"吸进去"没有过程感，长于此变成等待。</summary>
    public int GenieDurationMs { get; init; } = 500;

    /// <summary>灯罩两阶段分界（前段拉长、后段收拢）。0.30 = v2.2 参数表默认值。</summary>
    public double LampSplit { get; init; } = 0.30;

    /// <summary>从最小化还原的时长。比收拢略短（回到手里比离开手里更该干脆）。</summary>
    public int RestoreDurationMs { get; init; } = 450;

    /// <summary>最大化/还原缩放（morph）。</summary>
    public int MorphDurationMs { get; init; } = 240;

    /// <summary>系统触发路径的事后补偿（settle 交叉淡化）。</summary>
    public int SettleDurationMs { get; init; } = 180;

    // ---------- 打开 ----------

    /// <summary>打开（in-place scale + fade）时长。</summary>
    public int OpenDurationMs { get; init; } = 200;

    /// <summary>打开起始缩放：0.94 → 1.0。macOS 的窗口出现是"轻轻落位"，不是"弹出来"。</summary>
    public double OpenStartScale { get; init; } = 0.94;

    /// <summary>从 Dock 图标飞出的时长（默认关，见 <c>OpenAnimStyle</c>）。</summary>
    public int FromIconDurationMs { get; init; } = 300;

    /// <summary>从图标飞出的起始缩放（0.3 = 图标尺寸量级）。</summary>
    public double FromIconStartScale { get; init; } = 0.3;

    // ---------- 关闭 ----------

    /// <summary>关闭退场时长。退场一律快于入场。</summary>
    public int CloseDurationMs { get; init; } = 150;

    /// <summary>关闭终态缩放（缩到 92% 再消失，避免"硬切"）。</summary>
    public double CloseEndScale { get; init; } = 0.92;

    // ---------- 捕获 ----------

    /// <summary>占位帧就绪重试间隔（打开路径的首帧竞态）。</summary>
    public int CaptureRetryMs { get; init; } = 8;

    /// <summary>占位帧就绪重试次数。8ms × 12 ≈ 一帧半，够一轮合成。</summary>
    public int CaptureRetryCount { get; init; } = 12;

    /// <summary>前台帧缓存的最大可用龄：超过它就不许拿去播动画（画面已经不是"刚才那个"）。</summary>
    public int ForegroundCacheMaxAgeMs { get; init; } = 700;

    /// <summary>判废龄：缓存旧于此值直接当没有（1500ms 是"用户已经换了焦点"的量级）。</summary>
    public int ForegroundCacheDiscardMs { get; init; } = 1500;

    /// <summary>前台缓存刷新间隔（2Hz）。再快等于拿 PrintWindow 拖垮 UI 线程。</summary>
    public int ForegroundRefreshMs { get; init; } = 500;

    /// <summary>缓存保留的"全尺寸"条数（最近 1 窗），其余降为半尺寸（4 窗）。</summary>
    public int CacheFullSizeCount { get; init; } = 1;

    /// <summary>缓存保留的"半尺寸"条数。</summary>
    public int CacheHalfSizeCount { get; init; } = 4;

    // ---------- 批量 / 系统场景 ----------

    /// <summary>并发幽灵上限。超出就排队等/丢弃，绝不叠三块半透明在屏幕上。</summary>
    public int MaxConcurrentGhosts { get; init; } = 3;

    /// <summary>风暴窗口：该窗口内连续最小化达到阈值即整批不播。</summary>
    public int StormWindowMs { get; init; } = 200;

    /// <summary>风暴阈值（窗口内次数）。</summary>
    public int StormCount { get; init; } = 4;

    /// <summary>同一进程两次动画的最小间隔（连点任务栏不许连环播）。</summary>
    public int SameProcessThrottleMs { get; init; } = 300;

    /// <summary>同进程 1s 内超过该窗数只播第一个（打开动画的去重）。</summary>
    public int OpenBurstWindowMs { get; init; } = 1000;

    /// <summary>同进程打开动画的窗数上限（超过只播首个）。</summary>
    public int OpenBurstCount { get; init; } = 3;

    // ---------- 速度 ----------

    /// <summary>全局速度倍率：1.0 = 标称；2.0 = 快一倍（动画变短）。</summary>
    public double SpeedMultiplier { get; init; } = 1.0;

    /// <summary>倍率下限（0.5 = 半速，观感检查用）。</summary>
    public double MinSpeedMultiplier { get; init; } = 0.5;

    /// <summary>倍率上限（2.0）。</summary>
    public double MaxSpeedMultiplier { get; init; } = 2.0;

    /// <summary>按住 Shift 的慢放因子（×3 = 三倍时长），调曲线时用。</summary>
    public double ShiftSlowFactor { get; init; } = 3.0;

    /// <summary>渲染引擎：<c>native</c> / <c>mesh</c> / <c>strips</c> 三态回退（native 尚不可用）。</summary>
    public string Engine { get; init; } = "mesh";

    /// <summary>合法引擎标识（顺序即回退顺序）。</summary>
    public static IReadOnlyList<string> KnownEngines { get; } = ["native", "mesh", "strips"];

    /// <summary>参数表默认值（v2.2 §5）。</summary>
    public static AnimParams Default { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 规范化：所有时长/比例/计数钳到可用区间，引擎名落到合法集合。
    /// <para>
    /// 【为什么要这个】anim-params.json 是用户可以手改的文件。一个 <c>0</c> 的时长会让进度
    /// 计算除零、一个 <c>-1</c> 的倍率会让动画倒放。<b>配置文件写错只能降级成默认值，绝不能让动画子系统把主功能带崩</b>
    /// ——这是红线里"动画故障不影响最小化本身"的具体落点。
    /// </para>
    /// </summary>
    public AnimParams Normalized()
    {
        var min = Math.Clamp(MinSpeedMultiplier, 0.1, 4.0);
        var max = Math.Clamp(MaxSpeedMultiplier, min, 4.0);
        return this with
        {
            GenieDurationMs = ClampInt(GenieDurationMs, 100, 2000, 500),
            LampSplit = ClampDouble(LampSplit, 0.05, 0.95, 0.30),
            RestoreDurationMs = ClampInt(RestoreDurationMs, 100, 2000, 450),
            MorphDurationMs = ClampInt(MorphDurationMs, 80, 1500, 240),
            SettleDurationMs = ClampInt(SettleDurationMs, 60, 1000, 180),
            OpenDurationMs = ClampInt(OpenDurationMs, 80, 1200, 200),
            OpenStartScale = ClampDouble(OpenStartScale, 0.5, 1.0, 0.94),
            FromIconDurationMs = ClampInt(FromIconDurationMs, 80, 1200, 300),
            FromIconStartScale = ClampDouble(FromIconStartScale, 0.05, 1.0, 0.3),
            CloseDurationMs = ClampInt(CloseDurationMs, 60, 1000, 150),
            CloseEndScale = ClampDouble(CloseEndScale, 0.5, 1.0, 0.92),
            CaptureRetryMs = ClampInt(CaptureRetryMs, 1, 100, 8),
            CaptureRetryCount = ClampInt(CaptureRetryCount, 1, 100, 12),
            ForegroundCacheMaxAgeMs = ClampInt(ForegroundCacheMaxAgeMs, 100, 5000, 700),
            ForegroundCacheDiscardMs = ClampInt(ForegroundCacheDiscardMs, 200, 10000, 1500),
            ForegroundRefreshMs = ClampInt(ForegroundRefreshMs, 100, 2000, 500),
            CacheFullSizeCount = ClampInt(CacheFullSizeCount, 1, 4, 1),
            CacheHalfSizeCount = ClampInt(CacheHalfSizeCount, 0, 8, 4),
            MaxConcurrentGhosts = ClampInt(MaxConcurrentGhosts, 1, 8, 3),
            StormWindowMs = ClampInt(StormWindowMs, 50, 2000, 200),
            StormCount = ClampInt(StormCount, 2, 20, 4),
            SameProcessThrottleMs = ClampInt(SameProcessThrottleMs, 0, 2000, 300),
            OpenBurstWindowMs = ClampInt(OpenBurstWindowMs, 200, 5000, 1000),
            OpenBurstCount = ClampInt(OpenBurstCount, 1, 20, 3),
            MinSpeedMultiplier = min,
            MaxSpeedMultiplier = max,
            SpeedMultiplier = ClampDouble(SpeedMultiplier, min, max, 1.0),
            ShiftSlowFactor = ClampDouble(ShiftSlowFactor, 1.0, 6.0, 3.0),
            Engine = KnownEngines.Contains(Engine?.Trim().ToLowerInvariant() ?? "") ? Engine!.Trim().ToLowerInvariant() : "mesh",
        };
    }

    /// <summary>按速度倍率折算后的实际时长（毫秒，至少 1）。倍率越大越短。</summary>
    public int Scaled(int baseMs, bool slowMotion = false)
    {
        var n = Normalized();
        var mul = n.SpeedMultiplier;
        if (slowMotion) mul /= n.ShiftSlowFactor;
        var ms = baseMs / Math.Max(0.1, mul);
        return (int)Math.Clamp(Math.Round(ms), 1, 20000);
    }

    /// <summary>从 JSON 文本解析。<paramref name="error"/> 非空时返回默认值（并保留错误原因，供日志照实写）。</summary>
    public static AnimParams FromJson(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "内容为空"; return Default; }
        try
        {
            var parsed = JsonSerializer.Deserialize<AnimParams>(json, JsonOptions);
            if (parsed is null) { error = "反序列化结果为 null"; return Default; }
            return parsed.Normalized();
        }
        catch (JsonException ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return Default;
        }
        catch (NotSupportedException ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalized(), JsonOptions);

    /// <summary>诊断行：一次把关键参数写进日志，事后不必靠"当时是哪个版本"猜观感。</summary>
    public string ToLogLine()
        => string.Create(CultureInfo.InvariantCulture,
            $"genie={GenieDurationMs}ms split={LampSplit:0.00} restore={RestoreDurationMs}ms "
            + $"morph={MorphDurationMs}ms settle={SettleDurationMs}ms open={OpenDurationMs}ms/{OpenStartScale:0.00} "
            + $"close={CloseDurationMs}ms/{CloseEndScale:0.00} 缓存={ForegroundCacheMaxAgeMs}ms/{ForegroundRefreshMs}ms "
            + $"风暴={StormCount}/{StormWindowMs}ms 倍率={SpeedMultiplier:0.0} 引擎={Engine}");

    private static int ClampInt(int v, int lo, int hi, int fallback)
        => v <= 0 ? fallback : Math.Clamp(v, lo, hi);

    private static double ClampDouble(double v, double lo, double hi, double fallback)
        => double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? fallback : Math.Clamp(v, lo, hi);
}
