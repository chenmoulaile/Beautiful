using System;
using System.Collections.Generic;
using System.Text;

namespace Beautiful.Core.Icons;

/// <summary>
/// Windows 快捷方式**显示名** → macOS Tahoe 图标集键。
/// <para>
/// 【为什么单独一层】<see cref="MacIconNames"/> 解析的是可执行文件名（<c>code.exe</c> → <c>code</c>），
/// 但 <c>dock.json</c> 里大量条目是开始菜单导入的 .lnk：「Visual Studio Code.lnk」、
/// 「Google Chrome.lnk」、「Windows Terminal.lnk」。这些名字带空格、不是 exe 基名，
/// 走原来的别名表一律落空，Dock 上于是留着 Windows 系统图标。
/// </para>
/// <para>
/// 【诚实边界】只映射 <c>Assets/TahoeIcons/</c> 里**确实存在对应 PNG** 的名字。
/// 没有对应图标的（Sublime Text、Chrome 等）一律返回 null，让 UI 回退系统图标——
/// 把 Chrome 画成 Safari、把普通程序画成访达，比不换图标更糟。
/// </para>
/// </summary>
public static class MacDisplayName
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- 开发工具 ----------
        ["visualstudiocode"] = "code",
        ["visualstudiocodeinsiders"] = "code",
        ["githubdesktop"] = "githubdesktop",
        ["windowsterminal"] = "windowsterminal",
        ["terminal"] = "terminal",
        ["终端"] = "terminal",
        ["obsstudio"] = "obs",
        ["git"] = "githubdesktop",

        // ---------- 浏览器 / 系统 ----------
        ["microsoftedge"] = "msedge",
        ["fileexplorer"] = "explorer",
        ["资源管理器"] = "explorer",
        ["notepad"] = "notepad",
        ["记事本"] = "notepad",
        ["calculator"] = "calculator",
        ["计算器"] = "calculator",
        ["calendar"] = "calendar",
        ["日历"] = "calendar",
        ["settings"] = "system_settings",
        ["设置"] = "system_settings",
        ["microsoftstore"] = "winstore.app",
        ["photos"] = "photos",
        ["照片"] = "photos",
        ["mail"] = "mail",
        ["邮件"] = "mail",

        // ---------- 通讯 / 媒体 ----------
        ["telegramdesktop"] = "telegram",
        ["telegram"] = "telegram",
        ["微信"] = "wechat",
        ["weixin"] = "wechat",
        ["qq"] = "qq",
        ["网易云音乐"] = "cloudmusic",
        ["spotify"] = "spotify",
        ["firefox"] = "firefox",
        ["bilibili"] = "bilibili",
        ["哔哩哔哩"] = "bilibili",
        ["wpsoffice"] = "wps",
        ["wps"] = "wps",
        ["steam"] = "steam",
        ["启动台"] = "pad",
        ["launchpad"] = "pad",
        ["回收站"] = "trash_empty",
    };

    private static readonly string[] ShortcutExtensions = { ".lnk", ".exe", ".url", ".bat", ".com" };

    /// <summary>返回图标集键；没有可信映射返回 null（调用方回退系统图标，不猜）。</summary>
    public static string? KeyFor(string? pathOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(pathOrDisplayName)) return null;
        var text = pathOrDisplayName.Trim();

        // 只取最后一段路径；.lnk / .exe 等后缀剥掉。
        var name = text.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.Trim();
        foreach (var ext in ShortcutExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }

        var compact = Normalize(name);
        return compact.Length > 0 && Map.TryGetValue(compact, out var key) ? key : null;
    }

    /// <summary>去空白 / 全角转半角 / 转小写 / 去掉「- 快捷方式」这类尾巴。</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = raw;
            if (ch == '\u3000') ch = ' ';
            else if (ch >= 0xFF01 && ch <= 0xFF5E) ch = (char)(ch - 0xFEE0);
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        var result = sb.ToString();
        foreach (var tail in new[] { "-快捷方式", "-shortcut" })
        {
            if (result.EndsWith(tail, StringComparison.Ordinal))
            {
                result = result[..^tail.Length];
                break;
            }
        }
        return result;
    }
}
