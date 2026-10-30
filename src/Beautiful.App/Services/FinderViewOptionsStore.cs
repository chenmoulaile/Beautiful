using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beautiful.Core.Finder;

namespace Beautiful.App.Services;

/// <summary>
/// 访达"每个文件夹的显示方式"存储（<c>config/finder-view-options.json</c>，Phase 6-b）。
/// <para>
/// macOS 把视图模式/排序/图标尺寸记在每个文件夹上；Windows 上等价物是 Explorer 的 Bags/ViewBag
/// 注册表，但那是系统级写入，本版按仓库惯例落 exe 旁的 <c>config/</c>，绝不往用户目录写
/// <c>.DS_Store</c> 之类的东西。
/// </para>
/// <para>
/// 结构损坏（语法错/类型错/顶层不是对象）→ 回退空表且**不删文件**（下次 Save 覆盖），
/// 与 <see cref="PinnedAppsStore"/> 的自愈 idiom 一致。值域钳制/键归一/容量上限全在 Core 的
/// <see cref="FinderViewOptionRules"/>（可单测），本类只做 IO + 日志。
/// </para>
/// </summary>
public sealed class FinderViewOptionsStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;

    /// <param name="directory">配置目录；null 时取 AppContext.BaseDirectory\config。</param>
    public FinderViewOptionsStore(string? directory = null)
        => _filePath = Path.Combine(directory ?? Path.Combine(AppContext.BaseDirectory, "config"),
            "finder-view-options.json");

    /// <summary>读取整张表；文件缺失返回空表（不写盘），损坏返回空表并记日志。</summary>
    public IReadOnlyDictionary<string, FinderViewOptions> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
            }

            var file = JsonSerializer.Deserialize<ViewOptionsFile>(File.ReadAllText(_filePath), ReadOptions);
            if (file?.Folders is null)
            {
                Logger.Warn("finder-view-options.json 结构不符，回退默认视图");
                return new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
            foreach (var pair in file.Folders)
            {
                if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                var options = new FinderViewOptions(
                    FinderViewOptionRules.ParseMode(pair.Value.Mode),
                    FinderViewOptionRules.ParseSortKey(pair.Value.Sort),
                    pair.Value.Descending,
                    pair.Value.Hidden,
                    pair.Value.Icon);
                result[FinderViewOptionRules.KeyOf(pair.Key)] = FinderViewOptionRules.Normalize(options);
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            Logger.Warn($"finder-view-options.json 读取失败，回退默认视图：{ex.Message}");
            return new Dictionary<string, FinderViewOptions>(StringComparer.Ordinal);
        }
    }

    /// <summary>整表落盘；失败只记日志（瞬时锁/权限问题不该拖垮窗口）。</summary>
    public void Save(IReadOnlyDictionary<string, FinderViewOptions> map)
    {
        try
        {
            var folders = new Dictionary<string, ViewOptionsDto>(StringComparer.Ordinal);
            foreach (var pair in map)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                var options = FinderViewOptionRules.Normalize(pair.Value);
                folders[pair.Key] = new ViewOptionsDto
                {
                    Mode = FinderViewOptionRules.ModeName(options.Mode),
                    Sort = FinderViewOptionRules.SortKeyName(options.SortKey),
                    Descending = options.Descending,
                    Hidden = options.ShowHidden,
                    Icon = options.IconSize
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var file = new ViewOptionsFile { Version = CurrentVersion, Folders = folders };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or NotSupportedException)
        {
            Logger.Warn($"finder-view-options.json 保存失败：{ex.Message}");
        }
    }

    /// <summary>json 顶层结构（version 便于将来迁移；未知字段忽略）。</summary>
    private sealed class ViewOptionsFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("folders")]
        public Dictionary<string, ViewOptionsDto>? Folders { get; set; }
    }

    /// <summary>单文件夹记录：全部用稳定字符串/原始数值，避免枚举改名污染旧文件。</summary>
    private sealed class ViewOptionsDto
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("sort")]
        public string? Sort { get; set; }

        [JsonPropertyName("descending")]
        public bool Descending { get; set; }

        [JsonPropertyName("hidden")]
        public bool Hidden { get; set; }

        [JsonPropertyName("icon")]
        public double Icon { get; set; }
    }
}
