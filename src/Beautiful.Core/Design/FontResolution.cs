namespace Beautiful.Core.Design;

/// <summary>
/// 字体解析结果：一个 WPF FontFamily 字符串（首选 + 逗号回退链）与一句可读的来源说明。
/// </summary>
/// <param name="Family">FontFamily 字符串（可含 pack URI 与逗号回退）。</param>
/// <param name="Source">来源说明（写日志/关于页）：SF Pro Text(内嵌) / SF Pro Text(系统) / Segoe UI Variable(回退)。</param>
public sealed record FontChoice(string Family, string Source);

/// <summary>
/// SF Pro 解析链（v4 Phase 1 T5，D-FONT）：内嵌 pack URI → 系统已装 SF Pro → Segoe UI Variable。
/// Core 只做纯决策；"资源在不在"与"系统装没装"由 App 侧探测后传入（CI 无字体也能过门禁）。
/// </summary>
public static class FontResolution
{
    /// <summary>WPF 内嵌字体的 pack URI 前缀；家族名以 # 结尾。</summary>
    public const string EmbeddedSfProText = "pack://application:,,,/Assets/Fonts/#SF Pro Text";

    /// <summary>内嵌命中时的完整回退链（字体文件缺失时 WPF 自动落到 Segoe）。</summary>
    public static string EmbeddedFamily => $"{EmbeddedSfProText}, Segoe UI Variable, Segoe UI";

    /// <summary>系统已安装 SF Pro 时的回退链。</summary>
    public const string SystemFamily = "SF Pro Text, Segoe UI Variable, Segoe UI";

    /// <summary>无字体资产的 CI/公开仓库形态。</summary>
    public const string FallbackFamily = "Segoe UI Variable, Segoe UI";

    /// <summary>
    /// 解析链：内嵌优先（随产物分发、最接近 macOS）→ 系统 SF Pro（所有者机器已装）→ Segoe 回退。
    /// </summary>
    public static FontChoice Resolve(bool fontAssetsBundled, bool sfProInstalled)
    {
        if (fontAssetsBundled) return new(EmbeddedFamily, "SF Pro Text(内嵌)");
        if (sfProInstalled) return new(SystemFamily, "SF Pro Text(系统)");
        return new(FallbackFamily, "Segoe UI Variable(回退)");
    }

    /// <summary>可 grep 的中文证据行（启动时写一次）。</summary>
    public static string LogLine(FontChoice choice) => $"字体解析：{choice.Source} 命中";

    /// <summary>关于页/诊断块显示的一行。</summary>
    public static string Describe(FontChoice choice) => $"字体：{choice.Source}";
}
