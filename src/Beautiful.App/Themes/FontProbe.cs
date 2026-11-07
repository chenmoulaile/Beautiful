using System.Windows.Media;
using Beautiful.App.Services;
using Beautiful.Core.Design;
using Application = System.Windows.Application;   // UseWindowsForms 隐式 using 消歧

namespace Beautiful.App.Themes;

/// <summary>
/// 运行期字体探测（v4 Phase 1 T5）：资源是否随产物分发、系统是否已装 SF Pro，
/// 给出 <see cref="FontResolution"/> 的最终选择并写一条可 grep 的中文日志。
/// <para>
/// 探测只在首次访问时做一次（惰性）；公开仓库/云 CI 没有 Assets/Fonts，自然落到 Segoe 回退，
/// 应用必须完整可用——这是 D-FONT 的验收边界。
/// </para>
/// </summary>
public static class FontProbe
{
    private static readonly string[] KnownFontFiles =
    [
        "SF-Pro-Text-Regular.otf", "SF-Pro-Text-Medium.otf", "SF-Pro-Text-Semibold.otf",
        "SF-Pro-Display-Regular.otf", "SF-Pro-Display-Medium.otf", "SF-Pro-Display-Semibold.otf",
        "SF-Pro-Display-Bold.otf", "SF-Mono-Regular.otf",
    ];

    // ---------- Inter（Aurora 主题，OFL 1.1 可合法再分发捆绑；≥3 字重要求，实放 7 枚静态实例） ----------
    // WPF 不消费可变字体轴 → 只打包静态 OTF（含 InterDisplay-* 切割），不打包 InterVariable.ttf。
    private static readonly string[] InterFontFiles =
    [
        "Inter-Regular.otf", "Inter-Medium.otf", "Inter-SemiBold.otf", "Inter-Bold.otf",
        "InterDisplay-Medium.otf", "InterDisplay-SemiBold.otf", "InterDisplay-Bold.otf",
    ];

    /// <summary>内嵌命中时的回退链（pack URI 首选；"Inter Medium"/"Inter SemiBold" 是 GDI 静态 OTF
    /// 可能分裂出的独立族名，组串带上即自动兜底；Segoe 垫底）。</summary>
    private const string InterBundledFamily =
        "pack://application:,,,/Assets/Fonts/Inter/#Inter, Inter Medium, Inter SemiBold, Inter Display, Segoe UI Variable, Segoe UI";

    /// <summary>系统已装 Inter 时的回退链。</summary>
    private const string InterSystemFamily =
        "Inter, Inter Medium, Inter SemiBold, Inter Display, Segoe UI Variable, Segoe UI";

    /// <summary>无 Inter（内嵌+系统都没有）时的回退链：Inter → Segoe UI Variable → SystemFonts。</summary>
    private const string InterFallbackFamily = "Segoe UI Variable, Segoe UI";

    private static FontChoice? _current;
    private static string? _auroraFamily;

    /// <summary>最终字体选择（首次访问时探测并记日志）。</summary>
    public static FontChoice Current => _current ??= Detect();

    /// <summary>
    /// 按主题解析正文字体族：Aurora → Inter 链（内嵌 pack URI → 系统已装 → Segoe 回退），
    /// 其余主题 → 现链（<see cref="FontResolution.Resolve"/> 三态链不改语义）。
    /// 探测一次缓存一次，命中来源写一条可 grep 的中文日志（沿用 FontResolution.LogLine 格式）。
    /// </summary>
    public static string FamilyFor(string themeId)
    {
        if (!string.Equals(themeId, "Aurora", StringComparison.OrdinalIgnoreCase))
            return Current.Family;
        if (_auroraFamily is not null) return _auroraFamily;
        var bundled = HasBundledAssets(InterFontFiles, "Assets/Fonts/Inter/");
        var installed = !bundled && HasSystemInter();
        _auroraFamily = bundled ? InterBundledFamily
            : installed ? InterSystemFamily
            : InterFallbackFamily;
        Logger.Info($"字体解析：Inter({(bundled ? "内嵌" : installed ? "系统" : "回退")}) 命中（Aurora 主题，内嵌资产={bundled}, 系统 Inter={installed}）");
        return _auroraFamily;
    }

    private static FontChoice Detect()
    {
        var bundled = HasBundledAssets(KnownFontFiles, "Assets/Fonts/");
        var installed = HasSystemSfPro();
        var choice = FontResolution.Resolve(bundled, installed);
        Logger.Info($"{FontResolution.LogLine(choice)}（内嵌资产={bundled}, 系统 SF Pro={installed}）");
        return choice;
    }

    /// <summary>产物里是否带了至少一个期望字体文件（csproj 条件 Resource）。</summary>
    private static bool HasBundledAssets(string[] files, string directory)
    {
        foreach (var file in files)
        {
            foreach (var uri in new[]
                     {
                         new Uri($"{directory}{file}", UriKind.Relative),
                         new Uri($"/{directory}{file}", UriKind.Relative),
                         new Uri($"pack://application:,,,/{directory}{file}"),
                     })
            {
                try
                {
                    if (Application.GetResourceStream(uri) is not null) return true;
                }
                catch (Exception)
                {
                    // 资源不可用/URI 形态不支持：按"没有"处理，继续找下一个
                }
            }
        }
        return false;
    }

    /// <summary>系统字体集合里是否有 SF Pro Text/Display（所有者机器可能已装）。</summary>
    private static bool HasSystemSfPro()
    {
        try
        {
            return Fonts.SystemFontFamilies.Any(f =>
                f.Source.StartsWith("SF Pro Text", StringComparison.OrdinalIgnoreCase) ||
                f.Source.StartsWith("SF Pro Display", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Warn($"系统字体枚举失败（按未安装 SF Pro 处理）: {ex.Message}");
            return false;
        }
    }

    /// <summary>系统字体集合里是否有 Inter（官方发布名即 "Inter"；GDI 静态实例可能带 "Inter " 前缀族名）。</summary>
    private static bool HasSystemInter()
    {
        try
        {
            return Fonts.SystemFontFamilies.Any(f =>
                string.Equals(f.Source, "Inter", StringComparison.OrdinalIgnoreCase) ||
                f.Source.StartsWith("Inter ", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Warn($"系统字体枚举失败（按未安装 Inter 处理）: {ex.Message}");
            return false;
        }
    }
}
