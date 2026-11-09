using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Beautiful.Core.Launcher;

/// <summary>一个 logo 候选：包内相对路径 + 预期像素边长（0 = 清单里无从判断；诊断与单测断言用）。</summary>
public sealed record AppxLogoCandidate(string RelativePath, int PixelSize);

/// <summary>
/// MSIX 包内 logo 资产的<b>纯解析</b>（无 IO）：从 AppxManifest.xml 的 VisualElements 声明展开
/// "最合适的变体优先"的候选文件列表，App 侧（MsixIndexSource）逐个 <c>File.Exists</c> 取第一个
/// 真实存在的资产填进 <see cref="LauncherApp.IconPath"/>。
/// <para>
/// 【为什么在 Core】"按声明选哪个文件"是纯 XML/字符串规则，单测直接喂清单片段就能复现；
/// 文件是否真的存在是磁盘问题，留在 App 侧（Core 不碰 IO/shell 是 <see cref="LauncherIndex"/>
/// 头注释立下的分工，这条不破）。全链无异常面：任何输入（null 清单、空串、ms-resource 占位、
/// 无扩展名）都只是"展开出更少候选"，不抛。
/// </para>
/// <para>
/// 【降级链（次序即优先级，App 侧逐个试到存在为止）】
/// ① 基名优先级：<c>Square44x44Logo</c>（启动台/Dock 的图标栅格正对口）→ <c>Square150x150Logo</c> →
///    <c>Square310x310Logo</c> → <c>Square71x71Logo</c> → <c>Logo</c>（Win8 世代老属性，垫底）；
/// ② 每个基名先展开 <b>targetsize</b> 变体（尺寸策略：命中请求尺寸 → 从小到大的更大档 → 从大到小的
///    更小档——宁可拿小一档缩小，也不放大一张小图；本机探针 want=256 时 Microsoft Family / 记事本 /
///    计算器 / 画图 / Store 全部命中 <c>targetsize-256</c>），同尺寸下 <c>_altform-unplated</c>
///    （无底板、透明背景，启动台直接摆上画布不违和）先于普通版；
/// ③ 再展开 <b>scale</b> 变体（像素 = 基名声明尺寸 × 倍率，折算后走同一尺寸策略；Win8 世代包
///    只有 scale 资产、无 targetsize 时靠它兜住）；
/// ④ 最后是清单声明的原路径（裸资产——个别包只放一张定稿图、没有任何变体命名）。
/// </para>
/// <para>
/// 【命名空间无关】Win8（2010 命名空间）与 uap 世代的清单元素/属性<b>本地名</b>一致、前缀与命名空间
/// 随世代变，比较一律按 <see cref="XName.LocalName"/>——两种清单同一套代码。
/// </para>
/// </summary>
public static class AppxLogoResolver
{
    /// <summary>manifest 里参与选择的 logo 基名属性（VisualElements 上，按优先级）。</summary>
    private static readonly string[] LogoAttributes =
    {
        "Square44x44Logo", "Square150x150Logo", "Square310x310Logo", "Square71x71Logo", "Logo",
    };

    /// <summary>targetsize 规格（MSIX 图标资产标准集，设备像素）。</summary>
    private static readonly int[] TargetSizes = { 16, 20, 24, 30, 32, 36, 48, 64, 96, 256 };

    /// <summary>scale 规格（倍率；像素按基名声明尺寸折算，见 <see cref="ScaleOrder"/>）。</summary>
    private static readonly int[] Scales = { 400, 200, 150, 125, 100 };

    private static readonly char[] DirectorySeparators = { '\\', '/' };

    /// <summary>从基名文件名解析声明尺寸（"Square44x44Logo.png" → 44、"SquareTile44x44.png" → 44）。</summary>
    private static readonly Regex SizeInName = new(@"(\d+)[xX](\d+)", RegexOptions.Compiled);

    /// <summary>
    /// 清单的全部 logo 候选（次序即降级链）。applicationId 是 AUMID 里 '!' 后的那段（与清单
    /// Application 的 Id 对应）；给得对就取那个 Application 的 logo，取不到回落第一个带 logo
    /// 声明的 Application（单应用包 = 绝大多数包）。
    /// </summary>
    public static IReadOnlyList<AppxLogoCandidate> Candidates(XDocument? manifest, int requestedPx, string? applicationId = null)
    {
        if (manifest is null) return [];
        var want = Math.Clamp(requestedPx, 16, 512);
        var result = new List<AppxLogoCandidate>();
        foreach (var basePath in LogoBasePaths(manifest, applicationId))
            result.AddRange(VariantCandidates(basePath, want));
        return result;
    }

    /// <summary>单个 logo 基名的全部变体候选（次序即降级链，见类型注释；空/ms-resource/无扩展名只给裸路径）。</summary>
    public static IReadOnlyList<AppxLogoCandidate> VariantCandidates(string? logoBasePath, int requestedPx)
    {
        var want = Math.Clamp(requestedPx, 16, 512);
        if (string.IsNullOrWhiteSpace(logoBasePath)
            || logoBasePath.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return [];

        var separator = logoBasePath.LastIndexOfAny(DirectorySeparators);
        var directory = separator >= 0 ? logoBasePath[..separator] : string.Empty;
        var directorySeparator = separator >= 0 ? logoBasePath[separator] : '\\';   // 产出的相对路径沿用声明里的分隔符风格
        var fileName = separator >= 0 ? logoBasePath[(separator + 1)..] : logoBasePath;
        var dot = fileName.LastIndexOf('.');
        var declaredPx = DeclaredPxOf(fileName);
        // 声明值不带扩展名时没有变体插值的锚点：只给裸路径，绝不猜扩展名（猜 = 假路径）
        if (dot <= 0)
            return [new AppxLogoCandidate(Join(directory, directorySeparator, fileName), declaredPx)];

        var stem = fileName[..dot];
        var extension = fileName[dot..];
        var candidates = new List<AppxLogoCandidate>();
        // ① targetsize 变体（首选）；同尺寸下 unplated（无底板）先于普通版
        foreach (var size in TargetSizeOrder(want))
        {
            candidates.Add(new(Join(directory, directorySeparator,
                stem + ".targetsize-" + size + "_altform-unplated" + extension), size));
            candidates.Add(new(Join(directory, directorySeparator,
                stem + ".targetsize-" + size + extension), size));
        }
        // ② scale 变体（Win8 世代包只有 scale 资产时兜住）；声明尺寸解析不出就没有这一阶
        foreach (var scale in ScaleOrder(want, declaredPx))
        {
            var px = (int)Math.Round(declaredPx * scale / 100.0);
            candidates.Add(new(Join(directory, directorySeparator,
                stem + ".scale-" + scale + "_altform-unplated" + extension), px));
            candidates.Add(new(Join(directory, directorySeparator,
                stem + ".scale-" + scale + extension), px));
        }
        // ③ 裸资产（最后兜底）：清单声明的原路径
        candidates.Add(new(Join(directory, directorySeparator, fileName), declaredPx));
        return candidates;
    }

    /// <summary>
    /// 清单里参与选择的 logo 基名（按 <see cref="LogoAttributes"/> 优先级；声明为空或 ms-resource
    /// 本地化占位的跳过）。ms-resource 在 logo 上极罕见，但真存在——它不是文件路径，替它找文件必是假路径。
    /// </summary>
    internal static IReadOnlyList<string> LogoBasePaths(XDocument manifest, string? applicationId = null)
    {
        var paths = new List<string>();
        foreach (var application in ApplicationsOf(manifest, applicationId))
        {
            // 命名空间无关：只比本地名（uap:VisualElements 与 2010 清单的裸 <VisualElements> 同名）
            var visualElements = application.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            if (visualElements is null) continue;
            foreach (var attribute in LogoAttributes)
            {
                var value = visualElements.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value;
                if (string.IsNullOrWhiteSpace(value)
                    || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                    || paths.Contains(value, StringComparer.OrdinalIgnoreCase))
                    continue;
                paths.Add(value);
            }
            if (paths.Count > 0) break;   // 只认第一个"带 logo 声明"的 Application；多应用包逐 appId 各自调本方法
        }
        return paths;
    }

    /// <summary>清单的全部 Application；applicationId 能对上就只回它，对不上回全部（由调用方按声明取第一个）。</summary>
    private static IEnumerable<XElement> ApplicationsOf(XDocument manifest, string? applicationId)
    {
        var applications = manifest.Root?.Descendants().Where(e => e.Name.LocalName == "Application") ?? [];
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            var matched = applications.FirstOrDefault(a => string.Equals(
                a.Attributes().FirstOrDefault(x => x.Name.LocalName == "Id")?.Value,
                applicationId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null) return [matched];
        }
        return applications;
    }

    /// <summary>targetsize 的降级次序：命中请求 → 从小到大的更大档 → 从大到小的更小档。</summary>
    private static IEnumerable<int> TargetSizeOrder(int want)
    {
        return TargetSizes.Where(s => s == want)
            .Concat(TargetSizes.Where(s => s > want).OrderBy(s => s))
            .Concat(TargetSizes.Where(s => s < want).OrderByDescending(s => s));
    }

    /// <summary>scale 的降级次序（折算像素后与 targetsize 同一策略）；基名解析不出声明尺寸（如 "Logo"）就没有 scale 阶。</summary>
    private static IEnumerable<int> ScaleOrder(int want, int declaredPx)
    {
        if (declaredPx <= 0) return [];
        var pairs = Scales.Select(s => (Scale: s, Px: (int)Math.Round(declaredPx * s / 100.0))).ToList();
        return pairs.Where(p => p.Px >= want).OrderBy(p => p.Px)
            .Concat(pairs.Where(p => p.Px < want).OrderByDescending(p => p.Px))
            .Select(p => p.Scale);
    }

    /// <summary>
    /// 基名文件名里的声明尺寸：取第一个 "宽x高" 的宽（"Square44x44Logo" → 44、"SquareTile44x44" → 44）。
    /// 误读（如 "App1920x1080Tile" → 1920）无害：折算只影响 scale 候选的次序与 PixelSize 诊断值，
    /// 资产是否真实存在由 App 侧 File.Exists 把关。
    /// </summary>
    private static int DeclaredPxOf(string fileName)
    {
        var match = SizeInName.Match(fileName);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static string Join(string directory, char directorySeparator, string fileName) =>
        directory.Length == 0 ? fileName : directory + directorySeparator + fileName;
}
