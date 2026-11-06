using System.Text.Json;

namespace Beautiful.Core.Themes;

/// <summary>
/// 图标覆盖表的内存模型（<c>config/icon-theme.json</c>）。键 = <see cref="IconThemeKey"/> 应用键，
/// 值 = 两种引用之一：<c>asset:&lt;图集键&gt;</c>（内置 TahoeIcons 图集）或本地图片文件的绝对路径。
/// </summary>
public sealed record IconThemeData(int Version, IReadOnlyDictionary<string, string> Overrides)
{
    /// <summary>空表（无任何覆盖；文件缺失时的代码默认，不落盘）。</summary>
    public static IconThemeData Empty { get; } = new(IconThemeFile.CurrentVersion, new Dictionary<string, string>());
}

/// <summary>
/// 图标覆盖表文件的**纯解析/序列化**（不碰文件系统、不碰 WPF，App 侧存储与加载在
/// Beautiful.App.Services.IconThemeService）。规则：
/// <list type="bullet">
/// <item>信封：对象 + <c>Version</c>（必须等于 <see cref="CurrentVersion"/>）+ <c>Overrides</c>（对象）。</item>
/// <item>键：trim 后 1..<see cref="MaxKeyLength"/> 字符，统一小写；重复键后者胜（JSON 语义）。</item>
/// <item>值：<c>asset:</c> 前缀 → 图集键（1..64、<c>[a-z0-9_-]</c>）；否则按本地文件路径收（shape 校验），
/// 文件真实存在/格式/大小由 App 层用 Core 的 <c>DockIconOverride.Validate</c> 复核——Core 不碰 IO。</item>
/// <item>坏条目逐条丢弃并记错误，**信封坏了才整体失败**：半坏文件不清掉用户的其余覆盖。</item>
/// </list>
/// </summary>
public static class IconThemeFile
{
    public const int CurrentVersion = 1;

    /// <summary>覆盖条目上限：图标集量级（110 张）的一档余量，防手改文件把表撑爆。</summary>
    public const int MaxOverrides = 512;

    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 1024;

    /// <summary>图集引用前缀（值形如 <c>asset:finder</c>）。</summary>
    public const string AssetPrefix = "asset:";

    /// <summary>图集键的合法字符（Tahoe 图集现键全为小写字母/数字/-/_，如 ms-teams、trash_empty、final_cut_pro）。</summary>
    private static bool IsAssetKeyChar(char c)
        => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// 解析 JSON 文本。<paramref name="data"/> 非 null 当且仅当**信封**合法（版本 + Overrides 结构）；
    /// 单条目问题只进 <paramref name="errors"/>，不阻断其余条目。
    /// </summary>
    public static bool TryParse(string? json, out IconThemeData? data, out IReadOnlyList<string> errors)
    {
        data = null;
        var list = new List<string>();
        errors = list;

        if (string.IsNullOrWhiteSpace(json))
        {
            list.Add("文件内容为空");
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            list.Add($"不是合法 JSON：{ex.Message}");
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                list.Add("根节点不是对象");
                return false;
            }
            if (!doc.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number)
            {
                list.Add("缺少 Version 字段");
                return false;
            }
            if (version.GetInt32() != CurrentVersion)
            {
                list.Add($"不支持的版本 {version.GetInt32()}（当前支持 {CurrentVersion}）");
                return false;
            }
            if (!doc.RootElement.TryGetProperty("Overrides", out var overrides) || overrides.ValueKind != JsonValueKind.Object)
            {
                list.Add("缺少 Overrides 对象");
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in overrides.EnumerateObject())
            {
                var key = entry.Name.Trim().ToLowerInvariant();
                if (key.Length == 0 || key.Length > MaxKeyLength)
                {
                    list.Add($"丢弃条目：键长度非法（{Summarize(entry.Name)}）");
                    continue;
                }
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    list.Add($"丢弃条目：键 {key} 的值不是字符串");
                    continue;
                }
                var value = entry.Value.GetString()?.Trim() ?? string.Empty;
                if (value.Length == 0 || value.Length > MaxValueLength)
                {
                    list.Add($"丢弃条目：键 {key} 的值为空或超长");
                    continue;
                }
                if (ValidateValueShape(value) is { } reason)
                {
                    list.Add($"丢弃条目：键 {key} —— {reason}");
                    continue;
                }
                map[key] = value;   // 重复键后者胜（JSON 对象语义）
            }

            data = new IconThemeData(CurrentVersion, map);
            return true;
        }
    }

    /// <summary>
    /// 收敛：键小写去空、去重（后者胜）、值去空、条目数钳到 <see cref="MaxOverrides"/>（保留先出现的）。
    /// 编辑/导入路径写入前都过一遍，内存态与落盘态同源。
    /// </summary>
    public static IconThemeData Normalize(IconThemeData data)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in data.Overrides)
        {
            var k = key.Trim().ToLowerInvariant();
            var v = value.Trim();
            if (k.Length == 0 || k.Length > MaxKeyLength) continue;
            if (v.Length == 0 || v.Length > MaxValueLength) continue;
            if (ValidateValueShape(v) is not null) continue;
            map[k] = v;
            if (map.Count >= MaxOverrides) break;
        }
        return new IconThemeData(data.Version, map);
    }

    public static string Serialize(IconThemeData data) => JsonSerializer.Serialize(data, WriteOptions);

    /// <summary>
    /// 值的**形状**校验（纯函数，不查文件）：合法返回 null，非法返回给用户看的原因。
    /// asset 引用查图集键形状；文件路径只查长度与非法字符，存在性归 App 层。
    /// </summary>
    public static string? ValidateValueShape(string value)
    {
        if (value.Length == 0) return "值为空";
        if (value.Length > MaxValueLength) return $"值超长（上限 {MaxValueLength}）";
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('"')) return "值含非法字符";

        if (value.StartsWith(AssetPrefix, StringComparison.Ordinal))
        {
            var assetKey = value[AssetPrefix.Length..];
            if (assetKey.Length == 0 || assetKey.Length > 64) return $"图集键长度非法：{Summarize(assetKey)}";
            if (assetKey.Any(c => !IsAssetKeyChar(c))) return $"图集键含非法字符：{Summarize(assetKey)}";
            return null;
        }
        return null;   // 文件路径形状（App 层再验存在/格式/大小）
    }

    private static string Summarize(string text)
        => text.Length <= 40 ? text : text[..40] + "…";
}
