using System;
using System.Collections.Generic;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock「一格 = 一个应用」的分组键口径：把一个条目的图标源/启动路径折成可比较的应用名。
/// <para>
/// 【为什么空键要单独一档】解析不出 exe 的窗口（权限受限进程）、内置功能格（访达/启动台/废纸篓）、
/// 带命令行参数的伪路径，一旦被折出个碰巧相同的键，就会把两个不相干的东西并成一格 ——
/// 点 A 切到 B 的窗。这类误并没有任何异常可看，所以口径是「宁可不合并」：拿不准就返回空串，
/// 由 <see cref="DockAppGrouper"/> 让它们各自成格。
/// </para>
/// </summary>
public static class DockAppKey
{
    /// <summary>Windows 资源管理器快捷方式的常见写法 → 与 explorer.exe 同键（否则会出现两颗访达）。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fileexplorer"] = "explorer",
        ["windowsexplorer"] = "explorer",
        ["资源管理器"] = "explorer",
        ["此电脑"] = "explorer",
    };

    private static readonly string[] ExecutableExtensions = { ".exe", ".lnk", ".com", ".bat" };

    /// <summary>返回小写应用名；不可合并的源一律返回空串。</summary>
    public static string Of(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var text = source.Trim();

        // 内置功能格 / shell 命名空间 / 协议路径不是应用，不参与合并。
        if (text.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("::{", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var name = LastSegment(text);
        if (name.Length == 0) return string.Empty;

        // 基名里带空格：要么是带参数的伪路径（"explorer.exe shell:AppsFolder"），要么是
        // "File Explorer.lnk" 这种人起的快捷方式名。前者必须弃权，后者只有命中别名表才收。
        var hasSpace = name.Contains(' ', StringComparison.Ordinal);
        var compact = hasSpace ? name.Replace(" ", "") : name;
        compact = StripExtension(compact).ToLowerInvariant();

        if (hasSpace)
            return Aliases.TryGetValue(compact, out var alias) ? alias : string.Empty;

        return Aliases.TryGetValue(compact, out var mapped) ? mapped : compact;
    }

    private static string LastSegment(string path)
    {
        var name = path.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        return slash >= 0 ? name[(slash + 1)..].Trim() : name.Trim();
    }

    private static string StripExtension(string name)
    {
        foreach (var ext in ExecutableExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }
        return name;
    }
}
