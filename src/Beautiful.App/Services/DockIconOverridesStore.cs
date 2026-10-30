using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// 自定义 Dock 图标（P7 补）：<c>config/dock-icons.json</c>，键 = 固定项的路径，值 = 用户选的图片路径。
/// <para>
/// 只存**用户自己选的路径**，不内置、不分发任何图标文件；某个覆盖失效（文件被删/格式不支持）时
/// 读取阶段就丢弃该条并回落系统图标，绝不让 Dock 出现一块空白。
/// </para>
/// </summary>
public sealed class DockIconOverridesStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public DockIconOverridesStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(AppContext.BaseDirectory, "config");
        _filePath = Path.Combine(dir, "dock-icons.json");
        Load();
    }

    /// <summary>某固定项的自定义图标路径；没有/已失效返回 null。</summary>
    public string? For(string pinnedPath) => _map.TryGetValue(pinnedPath, out var icon) ? icon : null;

    public bool Has(string pinnedPath) => _map.ContainsKey(pinnedPath);

    /// <summary>设置覆盖（路径先经 Core 规则校验，不合法返回 false 并给出原因）。</summary>
    public bool Set(string pinnedPath, string iconPath, out string reason)
    {
        var valid = DockIconOverride.Validate(iconPath, out reason);
        if (valid is null)
        {
            return false;
        }

        _map[pinnedPath] = valid;
        Save();
        return true;
    }

    /// <summary>恢复系统图标。</summary>
    public void Reset(string pinnedPath)
    {
        if (_map.Remove(pinnedPath))
        {
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data is null)
            {
                return;
            }

            foreach (var pair in data)
            {
                if (DockIconOverride.Validate(pair.Value, out _) is { } valid)
                {
                    _map[pair.Key] = valid;
                }
                else
                {
                    Logger.Warn("自定义图标已失效（文件缺失或格式不支持），回落系统图标");
                }
            }

            Logger.Info($"自定义 Dock 图标已载入：{_map.Count} 条");
        }
        catch (Exception ex)
        {
            Logger.Warn($"自定义图标读取失败，按空表继续：{ex.GetType().Name} {ex.Message}");
            _map.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_map,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Warn($"自定义图标写入失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}
