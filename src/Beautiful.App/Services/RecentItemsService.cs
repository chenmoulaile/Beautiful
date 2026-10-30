using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Beautiful.App.Interop;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// "最近使用的项目"（v4 Phase 3-T4 起步）：从 <c>%APPDATA%\Microsoft\Windows\Recent\*.lnk</c> 读取，
/// 用目标的默认打开程序（AssocQueryString）匹配到 Dock 应用。
/// <para>
/// 【诚实边界】这不是 AutomaticDestinations-ms / Jump List 解析：对"文档由哪个应用打开"只按扩展名默认
/// 处理程序判断；浏览器/自定义 AppID 的程序可能匹配不到。匹配不到就返回空表，菜单里整段不出现，
/// 绝不拿全局最近文件冒充"这个应用的最近项目"。
/// </para>
/// <para>只读、无第三方依赖；结果按 app 缓存 60s，避免每次右键都扫一遍 Recent。</para>
/// </summary>
internal static class RecentItemsService
{
    private const int MaxScan = 80;
    private const int CacheSeconds = 60;
    private const uint AssocfNone = 0;
    private const uint AssocstrExecutable = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<DockRecentItem> Items)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DockRecentItem> FindForApp(string appExe, int take)
    {
        if (string.IsNullOrWhiteSpace(appExe) || take <= 0) return Array.Empty<DockRecentItem>();
        try
        {
            var app = Path.GetFullPath(appExe);
            lock (Gate)
            {
                if (Cache.TryGetValue(app, out var cached) &&
                    (DateTime.UtcNow - cached.At).TotalSeconds < CacheSeconds)
                    return cached.Items;
            }

            var items = Scan(app, take);
            lock (Gate) Cache[app] = (DateTime.UtcNow, items);
            if (items.Count > 0) Logger.Info($"最近项目：{Path.GetFileName(app)} 命中 {items.Count} 条（Recent 快捷方式）");
            return items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Warn($"最近项目读取失败：{ex.GetType().Name} {ex.Message}");
            return Array.Empty<DockRecentItem>();
        }
    }

    private static IReadOnlyList<DockRecentItem> Scan(string appExe, int take)
    {
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrWhiteSpace(recent) || !Directory.Exists(recent)) return Array.Empty<DockRecentItem>();

        var links = Directory.EnumerateFiles(recent, "*.lnk")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxScan)
            .ToList();

        var result = new List<DockRecentItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (result.Count >= take) break;
            string target;
            try { target = ShellLink.Resolve(link.FullName).TargetPath; }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(target)) continue;

            var handler = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? target
                : ExecutableForExtension(Path.GetExtension(target));
            if (string.IsNullOrWhiteSpace(handler)) continue;
            string handlerFull;
            try { handlerFull = Path.GetFullPath(handler); }
            catch { continue; }
            if (!string.Equals(handlerFull, appExe, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(target)) continue;

            result.Add(new DockRecentItem(
                Path.GetFileName(target),
                target,
                link.LastWriteTimeUtc,
                Exists(target)));
        }
        return result;
    }

    /// <summary>目标是否还在（Recent 快捷方式常常指向已删除/移动的文件；菜单里置灰而不是给死项）。</summary>
    private static bool Exists(string target)
    {
        try
        {
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
            return File.Exists(target) || Directory.Exists(target);
        }
        catch { return false; }
    }

    private static string? ExecutableForExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        try
        {
            uint size = 0;
            AssocQueryStringW(AssocfNone, AssocstrExecutable, ext, "open", null, ref size);
            if (size == 0 || size > 4096) return null;
            var sb = new StringBuilder((int)size);
            var hr = AssocQueryStringW(AssocfNone, AssocstrExecutable, ext, "open", sb, ref size);
            return hr == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(
        uint flags, uint str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
}
