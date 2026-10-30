using System;
using System.Collections.Generic;
using System.Linq;

namespace Beautiful.Core.Launcher;

/// <summary>
/// Launchpad 文件夹（Phase 5 收尾）的纯规则。
/// <para>
/// macOS Launchpad 允许把一个应用拖到另一个应用上形成文件夹，文件夹里最多 24 个应用，
/// 名字可改（默认取"<第一个应用名> 的文件夹"），空文件夹自动消失。这里只做**纯数据变换**：
/// 入参是"现有文件夹 + 应用 id"，出参是新文件夹或失败原因。UI 与持久化都不在这里，
/// 所以每条规则都能单测——拖拽类交互在真机上很难复现，只能靠这层把语义钉死。
/// </para>
/// <para>
/// 边界：应用 id 用字符串（与 LauncherService 的条目 key 同源），空 id 一律拒绝；
/// 同一文件夹内 id 去重；合并两个文件夹时按"目标在前、来源在后"保序去重。
/// </para>
/// </summary>
public static class LaunchpadFolderRules
{
    /// <summary>一个文件夹最多容纳的应用数（macOS 是 24 格）。</summary>
    public const int MaxAppsPerFolder = 24;

    /// <summary>文件夹名的字符上限。</summary>
    public const int MaxNameChars = 40;

    /// <summary>默认文件夹名：<c>&lt;应用名&gt; 的文件夹</c>。</summary>
    public static string DefaultName(string appLabel)
    {
        var trimmed = string.IsNullOrWhiteSpace(appLabel) ? "应用" : appLabel.Trim();
        return NormalizeName($"{trimmed} 的文件夹");
    }

    /// <summary>名字归一：去控制字符/多余空白、截断到 40 字符；空则回落"文件夹"。</summary>
    public static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "文件夹";
        }

        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            // 控制字符（含制表符/换行）当成分隔符而不是直接删掉：
            // "a\tb" 应归一成 "a b"，而不是粘成 "ab"。
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var text = string.Join(" ", builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return "文件夹";
        }

        return text.Length <= MaxNameChars ? text : text[..MaxNameChars].TrimEnd();
    }

    /// <summary>能否再放进一个应用到该文件夹；不能时给出原因（给状态栏用，不抛异常）。</summary>
    public static bool CanAccept(LaunchpadFolder folder, string appId, out string reason)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            reason = "应用标识为空";
            return false;
        }

        if (folder.AppIds.Contains(appId, StringComparer.Ordinal))
        {
            reason = "该应用已在此文件夹内";
            return false;
        }

        if (folder.AppIds.Count >= MaxAppsPerFolder)
        {
            reason = $"文件夹已满（最多 {MaxAppsPerFolder} 个）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>把一个应用加入文件夹；失败返回 null 并给出原因。</summary>
    public static LaunchpadFolder? Add(LaunchpadFolder folder, string appId, out string reason)
    {
        if (!CanAccept(folder, appId, out reason))
        {
            return null;
        }

        var ids = folder.AppIds.ToList();
        ids.Add(appId);
        return folder with { AppIds = ids };
    }

    /// <summary>从文件夹移除一个应用；移空后返回 null（UI 据此删掉文件夹）。</summary>
    public static LaunchpadFolder? Remove(LaunchpadFolder folder, string appId)
    {
        var ids = folder.AppIds.Where(id => !string.Equals(id, appId, StringComparison.Ordinal)).ToList();
        return ids.Count == 0 ? null : folder with { AppIds = ids };
    }

    /// <summary>把 <paramref name="source"/> 合并进 <paramref name="target"/>（保序去重）。
    /// 合并后超过上限返回 null 并给出原因。</summary>
    public static LaunchpadFolder? Merge(LaunchpadFolder target, LaunchpadFolder source, out string reason)
    {
        var merged = target.AppIds.ToList();
        foreach (var id in source.AppIds)
        {
            if (merged.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            if (merged.Count >= MaxAppsPerFolder)
            {
                reason = $"合并后超过 {MaxAppsPerFolder} 个，未执行";
                return null;
            }

            merged.Add(id);
        }

        reason = string.Empty;
        return target with { AppIds = merged };
    }
}

/// <summary>Launchpad 里的一个文件夹（不可变记录；AppIds 保序）。</summary>
/// <param name="Id">稳定 id（首次创建时生成，改名不改 id）。</param>
/// <param name="Name">显示名（已归一）。</param>
/// <param name="AppIds">成员应用 id（与启动器条目 key 同源）。</param>
public sealed record LaunchpadFolder(string Id, string Name, IReadOnlyList<string> AppIds)
{
    /// <summary>新建一个只含一个应用的文件夹（id 由调用方给，便于持久化）。</summary>
    public static LaunchpadFolder Create(string id, string name, string appId)
        => new(id, LaunchpadFolderRules.NormalizeName(name), [appId]);
}
