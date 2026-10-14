using System.IO;
using System.Text.Json;

namespace Beautiful.Core.Settings;

/// <summary>
/// AppSettings JSON 存储（config/settings.json）。目录可注入（测试用临时目录；运行期
/// App 传 AppContext.BaseDirectory\config）。损坏 JSON / null 字面量 / 类型不符 → 删除文件
/// 并返回代码默认（自愈，避免每次启动反复读到坏文件）。
/// 结构可解析但值域越界（v2 新增：图标尺寸/放大强度/样式与停靠边标识/热键为空）不是结构损坏，
/// 交由 <see cref="AppSettings.Normalized"/> 钳制与收敛并保留文件，避免清掉用户的其余配置。
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
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为保存：瞬时文件锁/权限失败不拖垮 UI（下次 Save 可重试；T17 接 Logger）
            System.Diagnostics.Trace.WriteLine($"settings.json 保存失败: {ex.Message}");
        }
    }

    /// <summary>损坏恢复：尽力删除坏文件后返回默认（删除失败不影响返回默认）。</summary>
    private AppSettings Corrupt()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 尽力而为 */ }
        return new AppSettings();
    }
}
