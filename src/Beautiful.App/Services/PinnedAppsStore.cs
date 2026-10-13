using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Beautiful.App.Services;

/// <summary>
/// 固定应用列表存储（config/dock.json，JSON List&lt;string&gt; exe 路径；控制器裁定：固定应用
/// 只落 dock.json，与 settings.json 分离）。文件缺失 → 代码默认兜底（资源管理器/记事本/
/// Edge-if-exists，与 T12 硬编码一致，不写盘）；损坏 → 默认（不删文件，下次 Save 覆盖）。
/// </summary>
public sealed class PinnedAppsStore
{
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <param name="directory">配置目录；null 时取 AppContext.BaseDirectory\config。</param>
    public PinnedAppsStore(string? directory = null)
        => _filePath = Path.Combine(directory ?? Path.Combine(AppContext.BaseDirectory, "config"), "dock.json");

    public List<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return Defaults();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath));
            if (list is null) return Defaults();
            // 去空行 + 去重（忽略大小写，Windows 路径不区分大小写）
            return list.Where(p => !string.IsNullOrWhiteSpace(p))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"dock.json 读取失败，回退默认固定项: {ex.Message}");
            return Defaults();
        }
    }

    public void Save(IReadOnlyList<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(paths, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为保存：瞬时文件锁/权限失败不拖垮 UI（下次 Save 可重试；T17 接 Logger）
            Trace.WriteLine($"dock.json 保存失败: {ex.Message}");
        }
    }

    /// <summary>代码默认固定项（dock.json 缺失时的兜底；不落盘，首次固定操作才写）。</summary>
    public static List<string> Defaults()
    {
        var list = new List<string> { @"C:\Windows\explorer.exe", @"C:\Windows\System32\notepad.exe" };
        if (File.Exists(EdgePath)) list.Add(EdgePath);
        return list;
    }
}
