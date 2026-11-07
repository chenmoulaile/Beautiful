using System.Globalization;

namespace Beautiful.Core.Layout;

/// <summary>
/// 屏幕圆角的<b>每屏排除键</b>（alpha.29 残项清零）：设置键 <c>AppSettings.ScreenCornersMonitors</c>
/// 列出的显示器<b>不画</b>圆角遮罩，空列表 = 全部屏幕都画（默认行为，与单屏机器完全一致）。
/// <para>
/// 【键的口径】与每显示器 Dock 覆盖配置（Core <c>Dock.DockMonitorProfiles</c>）同一套键：
/// <c>primary</c> = 主屏，<c>1..16</c> = 枚举序十进制串。解析/规范化/匹配全部纯函数——
/// App 侧 <c>ScreenCornerService</c> 只在每秒对账里调 <see cref="IsExcluded"/>，规则本身可脱离 UI 单测。
/// </para>
/// <para>
/// 【语义】这是"排除"名单而不是"包含"名单：用户实际诉求是"副屏/投影上别画圆角"，
/// 排除形让默认（全部屏）保持零配置可用；显示器的"在屏几块"只有运行时知道，
/// 写了不存在的序号匹配不上任何屏，静默无效（与 DockMonitorProfiles 序号越界同一处置）。
/// </para>
/// </summary>
public static class ScreenCornerMonitors
{
    /// <summary>序号上限（与 AppSettings.MaxMonitorIndex 同源的一层护栏：手改 JSON 写不出荒谬台数）。</summary>
    public const int MaxMonitorIndex = 16;

    /// <summary>
    /// 单个键规范化：<c>primary</c> 保留；<c>1..16</c> 的序号（容忍全角/空白/前导符，"2.0" 同 2）
    /// 归一为裸十进制串；auto/空白/非法 → null（调用方丢弃——排除名单里没有意义的键不留）。
    /// </summary>
    public static string? CanonicalKey(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "primary") return "primary";
        if (value.Length == 0 || value == "auto") return null;
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 1 && parsed <= MaxMonitorIndex &&
            Math.Abs(parsed - Math.Truncate(parsed)) < double.Epsilon)
            return ((int)parsed).ToString(CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>
    /// 列表规范化：逐条 <see cref="CanonicalKey"/>，null 丢弃，去重保序；空/全非法 → 空列表
    /// （= 不排除任何屏）。System.Text.Json 反序列化出的列表可能带 null 字面量，一并滤掉。
    /// </summary>
    public static IReadOnlyList<string> Normalized(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0) return [];
        var list = new List<string>(keys.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in keys)
        {
            var key = CanonicalKey(raw);
            if (key is null || !seen.Add(key)) continue;
            list.Add(key);
        }
        return list;
    }

    /// <summary>
    /// 运行时判定：枚举序 <paramref name="monitorIndex"/>（1 基）的这块屏要不要排除。
    /// 匹配顺序与 DockMonitorProfiles 一致：序号精确匹配优先，其次 primary 键（仅当该屏真是主屏）。
    /// <paramref name="isPrimary"/> 未知（句柄失效等）时按 false 处理——宁少排除一块屏，不静默多排除。
    /// </summary>
    public static bool IsExcluded(IReadOnlyList<string>? keys, int monitorIndex, bool isPrimary)
    {
        if (keys is null || keys.Count == 0) return false;
        var indexKey = Math.Clamp(monitorIndex, 1, MaxMonitorIndex).ToString(CultureInfo.InvariantCulture);
        foreach (var key in keys)
        {
            if (string.Equals(key, indexKey, StringComparison.Ordinal)) return true;
            if (isPrimary && key == "primary") return true;
        }
        return false;
    }
}
