using System.Text.Json;

namespace Beautiful.Core.Themes;

/// <summary>
/// 图标主题包 manifest（zip 内 <see cref="ThemePack.ManifestEntryName"/>）的内存模型。
/// <see cref="IconOverrides"/> 的键 = <see cref="IconThemeKey"/> 应用键；值 = 两种引用之一：
/// <c>asset:&lt;图集键&gt;</c>（引用目标机器内置 TahoeIcons 图集，不占包体积）或包内相对路径
/// <c>icons/&lt;文件名&gt;.png</c>。
/// </summary>
public sealed record ThemePackManifest(
    int SchemaVersion,
    string Name,
    string Author,
    string Description,
    IReadOnlyDictionary<string, string> IconOverrides);

/// <summary>
/// 图标主题包的**纯解析/校验**（不碰文件系统；zip 的开合与字节搬运在 App 层 IconThemeService）。
/// <para>
/// 【安全姿态】zip 里的条目**只按 manifest 引用的名字读取**，绝不全量解包；路径一律校验
/// 「<see cref="IconsFolderPrefix"/> 内、正斜杠、无 <c>..</c>、非绝对路径、.png 结尾」，
/// 引用不存在或不合规即拒。这条规则配合大小/条数上限，把「恶意 zip」（路径穿越、zip 炸弹）
/// 挡在解包之前——校验函数是纯函数，单测直接覆盖。
/// </para>
/// </summary>
public static class ThemePack
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>manifest 在包内的固定条目名。</summary>
    public const string ManifestEntryName = "theme.json";

    /// <summary>包内图标文件的固定目录前缀（正斜杠写法，与 zip 条目名一致）。</summary>
    public const string IconsFolderPrefix = "icons/";

    /// <summary>包内图标只收 PNG（导入/导出统一转存 PNG，ico/jpg/bmp 等由 App 层先转码）。</summary>
    public const string IconExtension = ".png";

    public const int MaxNameLength = 64;
    public const int MaxAuthorLength = 128;
    public const int MaxDescriptionLength = 512;

    /// <summary>覆盖条目上限（与 IconThemeFile.MaxOverrides 同档）。</summary>
    public const int MaxOverrides = 512;

    /// <summary>单个图标文件解包上限（8MB，与 Core DockIconOverride.MaxBytes 同源量级）。</summary>
    public const long MaxIconBytes = 8L * 1024 * 1024;

    /// <summary>整个包被**实际读取**的字节上限（防 zip 炸弹：只按引用读，读累计超标即拒）。</summary>
    public const long MaxReadBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 解析 manifest JSON。<paramref name="manifest"/> 非 null 当且仅当结构合法（版本/名称/overrides）；
    /// 条目校验（路径规则、包内存在性）在 <see cref="ValidateAgainstArchive"/>。
    /// </summary>
    public static bool TryParseManifest(string? json, out ThemePackManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        var list = new List<string>();
        errors = list;

        if (string.IsNullOrWhiteSpace(json))
        {
            list.Add("manifest 为空");
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            list.Add($"manifest 不是合法 JSON：{ex.Message}");
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                list.Add("manifest 根节点不是对象");
                return false;
            }

            if (!doc.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != CurrentSchemaVersion)
            {
                list.Add($"SchemaVersion 必须是 {CurrentSchemaVersion}(number)（旧版本包不受支持，请在编辑器重新导出）");
                return false;
            }

            var name = ReadString(doc.RootElement, "Name");
            if (name is null || name.Length == 0 || name.Length > MaxNameLength)
            {
                list.Add($"Name 必填且长度 1..{MaxNameLength}");
                return false;
            }

            var author = ReadString(doc.RootElement, "Author") ?? string.Empty;
            var description = ReadString(doc.RootElement, "Description") ?? string.Empty;

            if (!doc.RootElement.TryGetProperty("IconOverrides", out var overrides) || overrides.ValueKind != JsonValueKind.Object)
            {
                list.Add("缺少 IconOverrides 对象");
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in overrides.EnumerateObject())
            {
                var key = entry.Name.Trim().ToLowerInvariant();
                if (key.Length == 0 || key.Length > IconThemeFile.MaxKeyLength)
                {
                    list.Add($"丢弃条目：应用键长度非法（{key.Length}）");
                    continue;
                }
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    list.Add($"丢弃条目：应用键 {key} 的值不是字符串");
                    continue;
                }
                var value = entry.Value.GetString()?.Trim() ?? string.Empty;
                if (ValidateReferenceShape(value) is { } reason)
                {
                    list.Add($"丢弃条目：应用键 {key} —— {reason}");
                    continue;
                }
                map[key] = value;
            }

            if (map.Count == 0)
            {
                list.Add("IconOverrides 为空：主题包必须至少带一条图标覆盖");
                return false;
            }

            manifest = new ThemePackManifest(CurrentSchemaVersion, name, author, description, map);
            return true;
        }
    }

    /// <summary>
    /// 把 manifest 与**包内实际条目名**对账：每个 <c>icons/…</c> 引用都必须指向包内真实存在的 .png 条目。
    /// asset 引用不占包内条目（目标机器的 TahoeIcons 图集）。返回错误列表；空 = 校验通过。
    /// </summary>
    public static IReadOnlyList<string> ValidateAgainstArchive(
        ThemePackManifest manifest, IEnumerable<string> archiveEntryNames)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(archiveEntryNames, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in manifest.IconOverrides)
        {
            if (value.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)) continue;
            if (!names.Contains(value))
            {
                errors.Add($"应用键 {key} 引用的 {value} 不在包内");
                continue;
            }
            // 目录条目以 / 结尾：图标引用不可能指向目录
            if (value.EndsWith("/", StringComparison.Ordinal))
                errors.Add($"应用键 {key} 引用了目录而不是文件：{value}");
        }
        return errors;
    }

    /// <summary>
    /// manifest 引用值的**形状**校验（纯函数）：合法返回 null，非法返回原因。
    /// asset 引用复用 <see cref="IconThemeFile.ValidateValueShape"/> 的图集键规则；
    /// 包内路径必须是 <see cref="IconsFolderPrefix"/> 下的正斜杠相对路径、.png 结尾、无 <c>..</c>。
    /// </summary>
    public static string? ValidateReferenceShape(string value)
    {
        if (value.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal))
            return IconThemeFile.ValidateValueShape(value);

        if (value.Length == 0 || value.Length > 256) return "包内路径为空或超长";
        if (value.Contains('\\')) return "包内路径必须用正斜杠";
        if (value.Contains("..")) return "包内路径不允许 ..（路径穿越）";
        if (Path.IsPathRooted(value)) return "包内路径必须是相对路径";
        if (!value.StartsWith(IconsFolderPrefix, StringComparison.Ordinal))
            return $"包内路径必须在 {IconsFolderPrefix} 目录下";
        if (!value.EndsWith(IconExtension, StringComparison.OrdinalIgnoreCase))
            return $"包内图标必须是 {IconExtension} 文件";
        var fileName = value[IconsFolderPrefix.Length..];
        if (fileName.Length == 0 || fileName.Contains('/')) return "包内图标路径层级非法";
        return null;
    }

    /// <summary>构造 manifest JSON（导出用）：覆盖表逐条转成包内引用（<c>icons/&lt;键&gt;.png</c>）或 asset 引用。</summary>
    public static string BuildManifestJson(
        string name, string author, string description, IReadOnlyDictionary<string, string> overrides)
    {
        var mapped = overrides.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)
                ? pair.Value
                : $"{IconsFolderPrefix}{pair.Key}{IconExtension}",
            StringComparer.Ordinal);
        var payload = new
        {
            SchemaVersion = CurrentSchemaVersion,
            Name = name,
            Author = author,
            Description = description,
            IconOverrides = mapped,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>包内图标条目名（导出用）：键即文件名，键的合法字符已由应用键规则保证。</summary>
    public static string IconEntryName(string appKey) => $"{IconsFolderPrefix}{appKey}{IconExtension}";

    private static string? ReadString(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
