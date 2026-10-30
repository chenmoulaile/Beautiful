namespace Beautiful.Core.Design;

/// <summary>
/// macOS 强调色板（Big Sur/Monterey）。id 是持久化值（AppSettings.AccentColor），
/// 亮/暗两套 hex 直接来自 Apple Human Interface Guidelines 的系统色；
/// 名字用于设置页显示，渲染器不按名字分支。
/// </summary>
/// <param name="Id">小写持久化标识（blue/purple/…）。</param>
/// <param name="Name">设置页显示名。</param>
/// <param name="LightHex">浅色外观下的 #RRGGBB。</param>
/// <param name="DarkHex">深色外观下的 #RRGGBB。</param>
public sealed record MacAccent(string Id, string Name, string LightHex, string DarkHex);

/// <summary>
/// macOS 12 色强调色板 + 规范化/取色纯函数。App 侧把 <see cref="All"/> 铺进设置页，
/// 渲染器只读 <see cref="Hex"/> 的结果，不自己写颜色表。
/// </summary>
public static class MacAccents
{
    public const string DefaultId = "blue";

    public static IReadOnlyList<MacAccent> All { get; } =
    [
        new("blue",     "蓝色",   "#007AFF", "#0A84FF"),
        new("purple",   "紫色",   "#AF52DE", "#BF5AF2"),
        new("pink",     "粉色",   "#FF2D55", "#FF375F"),
        new("red",      "红色",   "#FF3B30", "#FF453A"),
        new("orange",   "橙色",   "#FF9500", "#FF9F0A"),
        new("yellow",   "黄色",   "#FFCC00", "#FFD60A"),
        new("green",    "绿色",   "#34C759", "#32D74B"),
        new("graphite", "石墨",   "#8E8E93", "#98989D"),
        new("teal",     "蓝绿色", "#30B0C7", "#40C8E0"),
        new("indigo",   "靛蓝色", "#5856D6", "#5E5CE6"),
        new("cyan",     "青色",   "#32ADE6", "#64D2FF"),
        new("brown",    "棕色",   "#A2845E", "#AC8E68"),
    ];

    /// <summary>null/空白/未知标识 → blue（手改配置不会把界面搞成无强调色）。</summary>
    public static string Canonical(string? id)
    {
        var value = (id ?? DefaultId).Trim().ToLowerInvariant();
        return All.Any(a => a.Id == value) ? value : DefaultId;
    }

    /// <summary>取当前外观下的强调色 hex；未知 id 回落 blue。</summary>
    public static string Hex(string? id, bool dark)
    {
        var accent = All.First(a => a.Id == Canonical(id));
        return dark ? accent.DarkHex : accent.LightHex;
    }
}
