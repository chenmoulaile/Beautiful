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

    public AppSettings Load(Action<string>? onMigration = null)
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings(); // 缺文件：代码默认，不落盘
            var text = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(text);
            if (settings is null) return Corrupt();               // "null" 字面量也视为损坏
            var normalized = settings.Normalized();
            ReportMigrations(text, settings, normalized, onMigration);
            return normalized;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Corrupt();
        }
    }

    /// <summary>
    /// 迁移取证：值域收敛（旧主题/旧切换器样式等）与已删除字段（v4 Phase 1 的歌词偏移/玻璃壳/壁纸取色）
    /// 逐条回调。App 侧把回调接到 Logger.Info，于是"用户旧配置被自愈"有可 grep 的中文证据行；
    /// Core 自身不依赖日志设施（纯函数 + 回调），单测可直接收集这些行。
    /// </summary>
    private static void ReportMigrations(
        string text, AppSettings raw, AppSettings normalized, Action<string>? report)
    {
        if (report is null) return;
        if (!string.Equals(raw.Theme, normalized.Theme, StringComparison.Ordinal))
            report($"设置自愈：Theme={raw.Theme} → {normalized.Theme}（v4 只保留 macOS）");
        if (!string.Equals(raw.SwitcherStyle, normalized.SwitcherStyle, StringComparison.Ordinal))
            report($"设置自愈：SwitcherStyle={raw.SwitcherStyle} → {normalized.SwitcherStyle}（v4 只保留 macOS 卡片）");
        if (!string.Equals(raw.TaskbarPolicy, normalized.TaskbarPolicy, StringComparison.Ordinal))
            report($"设置自愈：TaskbarPolicy={raw.TaskbarPolicy} → {normalized.TaskbarPolicy}");
        if (!string.Equals(raw.DockPosition, normalized.DockPosition, StringComparison.Ordinal))
            report($"设置自愈：DockPosition={raw.DockPosition} → {normalized.DockPosition}");
        if (!string.Equals(raw.GenieEngine, normalized.GenieEngine, StringComparison.Ordinal))
            report($"设置自愈：GenieEngine={raw.GenieEngine} → {normalized.GenieEngine}");
        if (!string.Equals(raw.IconTreatment, normalized.IconTreatment, StringComparison.Ordinal))
            report($"设置自愈：IconTreatment={raw.IconTreatment} → {normalized.IconTreatment}");

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var removed in RemovedFields)
                if (doc.RootElement.TryGetProperty(removed, out _))
                    report($"设置迁移：字段 {removed} 已随非 macOS 功能删除（v4 Phase 1），旧值忽略");
        }
        catch (JsonException)
        {
            // 结构损坏已在上层走 Corrupt 路径；这里只做取证，不改变加载结果
        }
    }

    /// <summary>v4 Phase 1 删除的持久化字段（旧文件里出现时只记迁移日志，不删用户的其余配置）。</summary>
    private static readonly string[] RemovedFields = ["LyricsOffsetMs", "TaskbarGlass", "UseWallpaperAccent"];

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
