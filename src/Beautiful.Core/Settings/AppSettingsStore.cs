using System.IO;
using System.Text.Json;

namespace Beautiful.Core.Settings;

/// <summary>
/// AppSettings JSON 存储（config/settings.json）。目录可注入（测试用临时目录；运行期
/// App 传 AppContext.BaseDirectory\config）。损坏 JSON / null 字面量 / 类型不符 → 删除文件
/// 并返回代码默认（自愈，避免每次启动反复读到坏文件）。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _directory;

    /// <param name="directory">配置目录；null 时取 AppContext.BaseDirectory\config。</param>
    public AppSettingsStore(string? directory = null)
        => _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "config");

    private string FilePath => Path.Combine(_directory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings(); // 缺文件：代码默认，不落盘
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings is null) return Corrupt();               // "null" 字面量也视为损坏
            return settings.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Corrupt();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, WriteOptions));
    }

    /// <summary>损坏恢复：尽力删除坏文件后返回默认（删除失败不影响返回默认）。</summary>
    private AppSettings Corrupt()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 尽力而为 */ }
        return new AppSettings();
    }
}
