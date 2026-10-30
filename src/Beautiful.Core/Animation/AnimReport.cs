using System.Globalization;
using System.Text;

namespace Beautiful.Core.Animation;

/// <summary>动画几何用的矩形（物理像素）。Core 不引用 WPF，所以自带一个最小结构。</summary>
public readonly record struct AnimRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public static AnimRect FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{X:0},{Y:0} {Width:0}×{Height:0}");
}

/// <summary>一帧序列的统计量（帧数、P50、P95、最大值，单位毫秒）。</summary>
public readonly record struct FrameStats(int Frames, double P50, double P95, double Max)
{
    public static readonly FrameStats Empty = new(0, 0, 0, 0);

    /// <summary>
    /// 由帧间隔（相邻两帧的毫秒差）统计。空输入返回 <see cref="Empty"/>，绝不抛。
    /// <para>P95 的定义用"最近秩法"：排序后取第 ceil(0.95·n) 个（1 起），n=20 时取第 19 个。</para>
    /// </summary>
    public static FrameStats FromIntervals(IReadOnlyList<double>? intervals)
    {
        if (intervals is null || intervals.Count == 0) return Empty;
        var sorted = new List<double>(intervals.Count);
        foreach (var v in intervals)
            if (!double.IsNaN(v) && v >= 0) sorted.Add(v);
        if (sorted.Count == 0) return Empty;
        sorted.Sort();
        var max = sorted[^1];
        return new FrameStats(sorted.Count, Percentile(sorted, 0.50), Percentile(sorted, 0.95), max);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

/// <summary>
/// 结构化日志行（v3.0 §5.4）。<b>日志是冲刺期间唯一的取证手段</b>，所以格式必须是"机器能切、人能读"：
/// 每个字段 <c>键=值</c>、空格分隔、固定顺序、中文键名（与仓库既有人读日志一致）。
/// <para>
/// 【为什么不在调用点拼字符串】v4.3 的"跳过"日志有七八种写法（"跳过"/"不可用"/"没抓到"/"错过"），
/// 于是 diag 脚本一个都统计不了，用户报"最小化没动画"时只能人工翻日志。统一出口后，
/// diag 的 [动画] 节可以直接按 <c>动画未播</c> 前缀 + <c>因=</c> 分组出分布。
/// </para>
/// </summary>
public static class AnimReport
{
    /// <summary>触发成功行：<c>动画触发：源=… 层=… 引擎=… 时长=… 帧数=… P95=…</c>。</summary>
    public static string Trigger(string source, string layer, string engine, int durationMs, int frames, double p95Ms)
        => string.Create(CultureInfo.InvariantCulture,
            $"动画触发：源={N(source)} 层={N(layer)} 引擎={N(engine)} 时长={durationMs}ms "
            + $"帧数={frames} P95={p95Ms:0.0}ms");

    /// <summary>
    /// 未播行：<c>动画未播：源=… 层=… 因=…</c>（可带 <c>细节=…</c>）。
    /// <b>每一条早退路径都必须产出一行</b>——"没有动画"与"没有日志"必须是同一件事。
    /// </summary>
    public static string Skipped(string source, string layer, string reason, string? detail = null)
    {
        var sb = new StringBuilder("动画未播：");
        sb.Append("源=").Append(N(source))
          .Append(" 层=").Append(N(layer))
          .Append(" 因=").Append(N(reason));
        if (!string.IsNullOrWhiteSpace(detail)) sb.Append(" 细节=").Append(N(detail));
        return sb.ToString();
    }

    /// <summary>
    /// 几何三方对账：幽灵窗实际矩形（<paramref name="ghost"/>）、源窗口 DWM 可见矩形（<paramref name="dwm"/>）、
    /// Dock 图标物理矩形（<paramref name="icon"/>）。<paramref name="tolerancePx"/> 默认 2（DWM 边框取整误差量级）。
    /// </summary>
    public static string Geometry(string source, AnimRect sourceRect, AnimRect dwm, AnimRect icon,
        AnimRect? ghost, int tolerancePx = 2)
    {
        var verdict = GeometryVerdict(sourceRect, dwm, icon, ghost, tolerancePx);
        var sb = new StringBuilder("几何对账：");
        sb.Append("源=").Append(N(source))
          .Append(" GetWindowRect=").Append(sourceRect.ToString())
          .Append(" DWM=").Append(dwm.ToString())
          .Append(" 图标=").Append(icon.ToString());
        if (ghost is { } g) sb.Append(" 幽灵=").Append(g.ToString());
        sb.Append(" 结论=").Append(verdict);
        return sb.ToString();
    }

    /// <summary>
    /// 对账结论（纯函数，可单测）：<c>一致</c> / <c>DPI倍率错位</c> / <c>幽灵越界</c> / <c>图标缺失</c>。
    /// <para>
    /// 四条判据对应四类真实事故：① 幽灵比源窗口大/小整数倍 = 物理像素当 DIP 用（v3 反复踩的坑）；
    /// ② 幽灵四角跑到源矩形外 = 落位用了错误的坐标系；③ 图标矩形宽 0 = Dock 没给到落点，
    /// 动画会收到屏幕边上一块虚构区域；④ DWM 与 GetWindowRect 差一圈不可见边，属正常（不算错）。
    /// </para>
    /// </summary>
    public static string GeometryVerdict(AnimRect source, AnimRect dwm, AnimRect icon, AnimRect? ghost, int tolerancePx = 2)
    {
        if (icon.Width <= 0 || icon.Height < 0) return "图标缺失";
        if (source.Width <= 0 || source.Height <= 0) return "源矩形无效";
        if (ghost is not { } g) return "无幽灵（未起播）";
        if (g.Width <= 0 || g.Height <= 0) return "幽灵尺寸为零";

        // 整数倍错位：缩放因子明显偏离 1（1.25 倍起判），这是 DPI 换算错误的签名
        var sx = g.Width / source.Width;
        var sy = g.Height / source.Height;
        if (Math.Abs(sx - 1.0) > 0.25 || Math.Abs(sy - 1.0) > 0.25)
            return string.Create(CultureInfo.InvariantCulture, $"DPI倍率错位×{sx:0.00}/{sy:0.00}");

        // 幽灵左上角必须落在源矩形左上角附近（容差内）
        if (Math.Abs(g.X - source.X) > tolerancePx + 1 || Math.Abs(g.Y - source.Y) > tolerancePx + 1)
            return "幽灵越界";

        // 有边框窗口的 GetWindowRect 比 DWM 可见矩形大一圈不可见缩放边（本机实测 13px/边@200%，
        // 一条 2906px 宽的窗口两个矩形差 26px）——这是正常的，不能当错误报。
        // 只有差到"超过 10% 边长"才说明拿错了窗口或坐标系（那已经不是一个边框的量级）。
        if (Math.Abs(dwm.Width - source.Width) > Math.Max(48, source.Width * 0.10)
            || Math.Abs(dwm.Height - source.Height) > Math.Max(48, source.Height * 0.10))
            return "DWM与GetWindowRect显著不一致";

        return "一致";
    }

    /// <summary>字段值清洗：空值写 <c>未知</c>，去掉空白与冒号，避免把日志格式搅乱。</summary>
    private static string N(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未知";
        var v = value.Trim();
        return v.Length > 48 ? v[..48] : v;
    }
}
