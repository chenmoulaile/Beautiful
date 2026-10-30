using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beautiful.Core.Launchpad;
using Beautiful.Core.Launcher;

namespace Beautiful.App.Services;

/// <summary>一个 Launchpad 文件夹的持久化形态（成员按拖入顺序）。</summary>
public sealed record LaunchpadFolderRecord(string Id, string Name, List<string> MemberIds);

/// <summary>
/// Launchpad 文件夹的持久化（<c>config/launchpad.json</c>，Phase 5 收尾）。
/// <para>
/// 语义全部复用 Core 的 <see cref="LaunchpadFolders"/>（创建/加成员/移成员/合并、解散判据），
/// 本类只做三件事：读写 JSON、给文件夹发稳定 id、把"成员 id"映射回 <see cref="LauncherApp"/>。
/// 损坏文件按"空布局"自愈并只记一条日志——启动台打不开比丢布局严重得多。
/// </para>
/// <para>
/// 应用身份键 = <see cref="KeyOf"/>（启动路径 + 参数）。与启动器索引的去重键同源，
/// 这样"同一个 exe 带不同参数"不会被误并成一个应用。
/// </para>
/// </summary>
public sealed class LaunchpadLayoutStore
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
    private readonly List<LaunchpadFolderRecord> _folders = [];

    public LaunchpadLayoutStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(AppContext.BaseDirectory, "config");
        _filePath = Path.Combine(dir, "launchpad.json");
        Load();
    }

    /// <summary>当前文件夹（顺序即启动台里的展示顺序）。</summary>
    public IReadOnlyList<LaunchpadFolderRecord> Folders => _folders;

    /// <summary>应用身份键：启动路径 + 参数（与启动器索引同一口径）。</summary>
    public static string KeyOf(LauncherApp app)
        => string.IsNullOrEmpty(app.Arguments) ? app.LaunchPath : $"{app.LaunchPath}|{app.Arguments}";

    /// <summary>某应用所属的文件夹（不在任何文件夹返回 null）。</summary>
    public LaunchpadFolderRecord? FolderOf(string appKey)
        => _folders.FirstOrDefault(f => f.MemberIds.Contains(appKey, StringComparer.Ordinal));

    /// <summary>
    /// 把 <paramref name="sourceKey"/> 拖到 <paramref name="targetKey"/> 上：
    /// 两个都自由 → 新建文件夹；目标已在文件夹里 → 加进那个文件夹。
    /// 返回处理结果说明（给状态栏用）。
    /// </summary>
    public string DropOnApp(string targetKey, string sourceKey, string targetLabel)
    {
        if (string.Equals(targetKey, sourceKey, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var folder = FolderOf(targetKey);
        if (folder is not null)
        {
            return AddToFolder(folder.Id, sourceKey);
        }

        // 目标在别的文件夹里（source 已在某文件夹）→ 先把 source 从原文件夹移出
        var sourceFolder = FolderOf(sourceKey);
        if (sourceFolder is not null)
        {
            RemoveFromFolder(sourceFolder.Id, sourceKey);
        }

        var created = LaunchpadFolders.Create(targetKey, sourceKey, targetLabel);
        _folders.Add(new LaunchpadFolderRecord(Guid.NewGuid().ToString("N"), created.Name,
            created.MemberIds.ToList()));
        Save();
        return $"已创建文件夹「{created.Name}」";
    }

    /// <summary>把应用加入指定文件夹（已存在则不重复）。</summary>
    public string AddToFolder(string folderId, string appKey)
    {
        var index = _folders.FindIndex(f => f.Id == folderId);
        if (index < 0)
        {
            return string.Empty;
        }

        var current = _folders[index];
        var updated = LaunchpadFolders.AddMember(
            new LaunchpadItem.Folder(current.Name, current.MemberIds), appKey);

        if (updated.MemberIds.Count == current.MemberIds.Count)
        {
            return "该应用已在此文件夹内";
        }

        _folders[index] = current with { MemberIds = updated.MemberIds.ToList() };
        Save();
        return $"已加入「{updated.Name}」";
    }

    /// <summary>把应用移出文件夹；只剩 1 个成员时按 Core 的解散判据整个文件夹消失。</summary>
    public string RemoveFromFolder(string folderId, string appKey)
    {
        var index = _folders.FindIndex(f => f.Id == folderId);
        if (index < 0)
        {
            return string.Empty;
        }

        var current = _folders[index];
        var (result, dissolved) = LaunchpadFolders.RemoveMember(
            new LaunchpadItem.Folder(current.Name, current.MemberIds), appKey);

        if (dissolved)
        {
            _folders.RemoveAt(index);
            Save();
            return "文件夹已解散";
        }

        if (result is LaunchpadItem.Folder folder)
        {
            _folders[index] = current with { MemberIds = folder.MemberIds.ToList() };
            Save();
            return "已移出文件夹";
        }

        return string.Empty;
    }

    /// <summary>改名（空名回落到原名）。</summary>
    public void Rename(string folderId, string name)
    {
        var index = _folders.FindIndex(f => f.Id == folderId);
        if (index < 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _folders[index] = _folders[index] with { Name = name.Trim() };
        Save();
    }

    /// <summary>把成员 id 映射回应用；索引里已不存在的应用直接跳过（卸载过的应用不该留空框）。</summary>
    public IReadOnlyList<LauncherApp> MembersOf(
        LaunchpadFolderRecord folder, IReadOnlyList<LauncherApp> apps)
    {
        var byKey = new Dictionary<string, LauncherApp>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            byKey[KeyOf(app)] = app;
        }

        var result = new List<LauncherApp>(folder.MemberIds.Count);
        foreach (var id in folder.MemberIds)
        {
            if (byKey.TryGetValue(id, out var app))
            {
                result.Add(app);
            }
        }

        return result;
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
            var data = JsonSerializer.Deserialize<LayoutFile>(json, ReadOptions);
            if (data?.Folders is null)
            {
                return;
            }

            foreach (var folder in data.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Id) || folder.MemberIds is null)
                {
                    continue;
                }

                var members = folder.MemberIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (members.Count < 2)
                {
                    continue;   // 只剩一个成员的"文件夹"在语义上就是普通应用
                }

                _folders.Add(new LaunchpadFolderRecord(folder.Id, folder.Name ?? "文件夹", members));
            }

            Logger.Info($"启动台布局已载入：{_folders.Count} 个文件夹");
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动台布局读取失败，按空布局继续：{ex.GetType().Name} {ex.Message}");
            _folders.Clear();
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

            var data = new LayoutFile
            {
                Version = CurrentVersion,
                Folders = _folders.Select(f => new LaunchpadFolderRecord(f.Id, f.Name, f.MemberIds)).ToList()
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(data, WriteOptions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动台布局写入失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private sealed class LayoutFile
    {
        public int Version { get; set; } = CurrentVersion;
        public List<LaunchpadFolderRecord> Folders { get; set; } = [];
    }
}
