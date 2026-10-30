namespace Beautiful.Core.TopBar;

/// <summary>
/// 顶栏/控制中心的状态文案规则（P4-T9、P4-T10）：电量、网络、蓝牙、输入法。
/// <para>
/// 【为什么下沉 Core】这几块全是"一组数字/枚举 → 一句中文"，散在 UI 里就只能靠肉眼验；
/// 下沉后可单测穷举（0%/充电中/未知/离线/中文键盘…），UI 只负责把它们摆上去。
/// </para>
/// <para>
/// 【约定】任何一项拿不到都返回明确占位（<c>--</c> / <c>不可用</c>），不返回空串——
/// 空串会让旁边的分隔点悬空，看起来像渲染坏了。
/// </para>
/// </summary>
public static class StatusTiles
{
    /// <summary>电量百分比：钳到 0..100；未知（&lt;0）返回 <c>--</c>。</summary>
    public static string BatteryText(int percent)
        => percent < 0 ? "--" : $"{Math.Clamp(percent, 0, 100)}%";

    /// <summary>
    /// 电量图标字形键（Segoe Fluent Icons 的电池四档 + 充电）。
    /// 返回 <c>battery-0..4</c> / <c>battery-charging</c> / <c>battery-unknown</c>，由 App 映射成码点。
    /// </summary>
    public static string BatteryGlyphKey(int percent, bool charging, bool unknown = false)
    {
        if (unknown || percent < 0) return "battery-unknown";
        if (charging) return "battery-charging";
        return Math.Clamp(percent, 0, 100) switch
        {
            <= 10 => "battery-0",
            <= 30 => "battery-1",
            <= 60 => "battery-2",
            <= 90 => "battery-3",
            _ => "battery-4"
        };
    }

    /// <summary>电量读数后缀：充电时带 <c>· 充电中</c>（macOS 菜单栏是图标变闪电，这里补文字更可读）。</summary>
    public static string BatteryDetail(int percent, bool charging)
        => charging ? $"{BatteryText(percent)} · 充电中" : BatteryText(percent);

    /// <summary>
    /// 网络一行文字：有线 → <c>以太网</c>；无线且有 SSID → SSID；无线无 SSID → <c>Wi-Fi</c>；
    /// 都没有 → <c>离线</c>。
    /// </summary>
    public static string NetworkText(bool ethernet, bool wifi, string? ssid)
    {
        if (ethernet) return "以太网";
        if (wifi) return string.IsNullOrWhiteSpace(ssid) ? "Wi-Fi" : ssid!.Trim();
        return "离线";
    }

    /// <summary>蓝牙状态文字：<c>开</c>/<c>关</c>/<c>不可用</c>（无蓝牙硬件或读取失败）。</summary>
    public static string BluetoothText(bool? on)
        => on switch { true => "开", false => "关", null => "不可用" };

    /// <summary>
    /// 输入法徽标：中文/日文/韩文键盘显示单字，其余显示 <c>EN</c>。
    /// 参数是 BCP-47 语言标签（如 <c>zh-Hans-CN</c>）或输入法描述，取前两位判断。
    /// </summary>
    public static string InputBadge(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return "EN";

        var tag = languageTag.Trim();
        // 中文输入法常见描述里有"中文/拼音/五笔/搜狗"等字样，先按关键字判，再退回语言标签
        if (tag.Contains("中文", StringComparison.Ordinal) || tag.Contains("拼音", StringComparison.Ordinal)
            || tag.Contains("五笔", StringComparison.Ordinal))
        {
            return "中";
        }

        var head = tag.Length >= 2 ? tag[..2].ToLowerInvariant() : tag.ToLowerInvariant();
        return head switch
        {
            "zh" => "中",
            "ja" => "あ",
            "ko" => "한",
            _ => "EN"
        };
    }
}
