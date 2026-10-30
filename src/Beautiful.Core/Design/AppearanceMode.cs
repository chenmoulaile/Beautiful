namespace Beautiful.Core.Design;

/// <summary>
/// 外观模式（v4 Phase 1）：浅色 / 深色 / 跟随系统。
/// 纯枚举语义只在这里定义；具体颜色/字号/时长见 <see cref="MacTokens"/>。
/// </summary>
public enum AppearanceMode
{
    /// <summary>深色（Big Sur 默认暗色形态）。</summary>
    Dark,
    /// <summary>浅色。</summary>
    Light,
    /// <summary>跟随 Windows 的"应用模式"（AppsUseLightTheme）。</summary>
    Auto
}

/// <summary>
/// 外观模式的字符串白名单与折算规则（AppSettings 持久化 "dark" | "light" | "auto"）。
/// </summary>
public static class AppearanceModes
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string Auto = "auto";

    public static IReadOnlyList<string> Known { get; } = [Dark, Light, Auto];

    /// <summary>null/空白/大小写差异 → 小写；白名单外回落 dark（Big Sur 默认观感）。</summary>
    public static string Canonical(string? raw)
    {
        var value = (raw ?? Dark).Trim().ToLowerInvariant();
        return Known.Contains(value) ? value : Dark;
    }

    /// <summary>
    /// 折算成"当前是否深色"：light/dark 直接给答案；auto 取系统应用模式。
    /// <paramref name="systemUsesLightTheme"/> 由 App 侧读注册表 AppsUseLightTheme 后传入，
    /// Core 不碰注册表（可单测）。
    /// </summary>
    public static bool IsDark(string? mode, bool systemUsesLightTheme) => Canonical(mode) switch
    {
        Light => false,
        Dark => true,
        _ => !systemUsesLightTheme
    };
}
