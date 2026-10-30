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

    private static FontChoice? _current;

    /// <summary>最终字体选择（首次访问时探测并记日志）。</summary>
    public static FontChoice Current => _current ??= Detect();

    private static FontChoice Detect()
    {
        var bundled = HasBundledAssets();
        var installed = HasSystemSfPro();
        var choice = FontResolution.Resolve(bundled, installed);
        Logger.Info($"{FontResolution.LogLine(choice)}（内嵌资产={bundled}, 系统 SF Pro={installed}）");
        return choice;
    }

    /// <summary>产物里是否带了至少一个期望字体文件（csproj 条件 Resource）。</summary>
    private static bool HasBundledAssets()
    {
        foreach (var file in KnownFontFiles)
        {
            foreach (var uri in new[]
                     {
                         new Uri($"Assets/Fonts/{file}", UriKind.Relative),
                         new Uri($"/Assets/Fonts/{file}", UriKind.Relative),
                         new Uri($"pack://application:,,,/Assets/Fonts/{file}"),
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
}
