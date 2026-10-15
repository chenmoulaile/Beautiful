using System.IO;
using System.Text.Json;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// 固定应用列表存储（config/dock.json，JSON List&lt;string&gt; exe 路径；控制器裁定：固定应用
/// 只落 dock.json，与 settings.json 分离）。文件缺失 → 代码默认兜底（资源管理器/记事本/
/// Edge-if-exists，与 T12 硬编码一致，不写盘）；损坏 → 默认（不删文件，下次 Save 覆盖）。
/// <para>
/// 解析与去重的**算术**在 Core（<see cref="DockPinOps"/>，可单测）：结构损坏 JSON 走 TryParsePaths
/// 的自愈信号（与 AppSettingsStore 的 Corrupt 同一 idiom），空行/重复走 Normalize。本类只负责
/// 文件 IO + Logger + 代码默认，不再自带一份去重逻辑（过去 Load 里的 Distinct 与插入路径的去重是两套，
/// 拖放固定/排序引入"按解析目标去重"后必须统一到一个来源）。
/// </para>
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
            if (!DockPinOps.TryParsePaths(File.ReadAllText(_filePath), out var raw))
            {
                // 结构损坏（语法错/非数组/"null"）：回落默认但不删文件——与历史行为一致，下次 Save 覆盖
                Logger.Warn("dock.json 结构损坏，回退默认固定项");
                return Defaults();
            }
            // 值域自愈：去空 + 按原始路径（忽略大小写）去重。目标级去重（.lnk vs exe）在编辑路径注入解析器完成。
            return DockPinOps.Normalize(raw);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"dock.json 读取失败，回退默认固定项: {ex.Message}");
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
            // 尽力而为保存：瞬时文件锁/权限失败不拖垮 UI（下次 Save 可重试）
            Logger.Warn($"dock.json 保存失败: {ex.Message}");
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
