using System.IO;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 读取 Windows 原生任务栏的固定应用清单，用于"把用户已经固定好的东西接过来"，
/// 而不是要求他在 Dock 里再固定一遍。
/// <para>
/// 【为什么保留 .lnk 本体而不是解析后的 exe】UWP/MSIX 的任务栏 pin 在这份目录里同样是 .lnk，
/// 但它的 <c>TargetPath</c> 是 <c>C:\Windows\explorer.exe</c>、真正的身份在 <c>Arguments</c> 里的
/// <c>shell:AppsFolder\&lt;AUMID&gt;</c>。按 exe 收录会把一堆商店应用塌成同一个"资源管理器"，
/// 而 shell 本来就认得这条 .lnk —— Dock 的图标提取与启动路径也都已经支持 .lnk（拖入固定走的就是它）。
/// </para>
/// <para>
/// 【顺序是近似值】Win11 的固定顺序存在 <c>HKCU\...\Explorer\Taskband\Preferences</c> 的私有二进制块里，
/// 跨版本会变，为它写解析器不划算。这里按 .lnk 的创建时间排序（≈ 固定先后），失败再按文件名，
/// 并在日志里说明这是近似 —— 用户真正在意的是"东西在不在"，不是"第 3 个和第 4 个换没换位置"。
/// </para>
/// </summary>
public static class TaskbarPinsReader
{
    /// <summary>任务栏固定项的 .lnk 所在目录（shell 的"用户固定"落盘位置）。</summary>
    private static string TaskBarLinksDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>候选 .lnk 路径（按创建时间近似排序）；目录不存在/无权限 → 空表，不抛。</summary>
    public static List<string> Read()
    {
        var dir = TaskBarLinksDirectory;
        try
        {
            if (!Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir)
                .EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.CreationTimeUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"任务栏固定项读取失败（跳过导入）: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 去重键：普通条目就是自身；<c>.lnk</c> 解析成"目标 + 参数"，这样同一个 exe 的快捷方式与本体
    /// 不会双份固定，而 UWP 的 <c>explorer.exe + shell:AppsFolder\…</c> 也不会互相塌成同一个键。
    /// 与 <c>DockPinOps.Normalize</c> 的 <c>targetOf</c> 口径一致（Core 不引 COM，由调用方注入）。
    /// </summary>
    public static string DedupKeyOf(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Trim(path);
        var r = ShellLink.Resolve(path);
        // 解析失败时 ShellLink 会把 TargetPath 回落成 .lnk 自身——此时按本体去重即可。
        if (string.Equals(r.TargetPath, path, StringComparison.OrdinalIgnoreCase)) return Trim(path);
        return string.IsNullOrWhiteSpace(r.Arguments) ? Trim(r.TargetPath) : $"{Trim(r.TargetPath)} {Trim(r.Arguments)}";
    }

    private static string Trim(string s) => s.Trim().Trim('"').ToLowerInvariant();
}
