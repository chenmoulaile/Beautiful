using System;
using System.Collections.Generic;
using System.Text;

namespace Beautiful.Core.Tray;

/// <summary>托盘图标快照存单的一条候选（已由 App 层按应用分组、只保留最新那条）。</summary>
/// <param name="Tooltip">注册表 <c>InitialTooltip</c>（系统记下的初次注册名）。</param>
/// <param name="ExePath">注册表 <c>ExecutablePath</c>。</param>
public readonly record struct TraySnapshotCandidate(string Tooltip, string ExePath);

/// <summary>
/// 把 UIA 读到的托盘图标名字，匹配到 Windows 自己在
/// <c>HKCU\Control Panel\NotifyIconSettings\*\IconSnapshot</c> 里存的那张 PNG。
/// <para>
/// 【为什么宁可不命中】存单是历史（应用退出后记录还在），UIA 的 Name 又常带动态后缀。
/// 错匹配 = 把 A 应用的图标画到 B 应用头上，比画一个中性占位更糟。所以规则设计为
/// 「三档打分 + 同分多候选一律弃权」，把不确定性挡在纯函数里，而不是留给 UI 猜。
/// </para>
/// </summary>
public static class TraySnapshotMatch
{
    /// <summary>tooltip 规范化后完全相等。</summary>
    public const int ExactTooltip = 3;

    /// <summary>exe 基名（去 .exe）规范化后完全相等。</summary>
    public const int ExactExe = 2;

    /// <summary>两边都不少于 4 字且一方包含另一方（应对"华为电脑管家 (已连接)"这类动态后缀）。</summary>
    public const int Contains = 1;

    /// <summary>去空白 / 全角转半角 / 转小写。null 与纯空白一律收敛成空串。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = raw;
            if (ch == '\u3000') ch = ' ';
            else if (ch >= 0xFF01 && ch <= 0xFF5E) ch = (char)(ch - 0xFEE0);
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>单个候选的匹配档位；0 = 不许用这张图。</summary>
    public static int Score(string? label, TraySnapshotCandidate candidate)
    {
        var name = Normalize(label);
        if (name.Length == 0) return 0;

        var tooltip = Normalize(candidate.Tooltip);
        if (tooltip.Length > 0 && name == tooltip) return ExactTooltip;

        var exe = Normalize(ExeBaseName(candidate.ExePath));
        if (exe.Length > 0 && name == exe) return ExactExe;

        if (MutualContains(name, tooltip) || MutualContains(name, exe)) return Contains;
        return 0;
    }

    /// <summary>
    /// 唯一最佳候选的下标；没有得分、清单为空、或最高分出现并列时返回 -1（弃权）。
    /// 调用方应按「同一应用只留最新一条存单」先去重，否则多条历史存单会互相制造并列。
    /// </summary>
    public static int MatchIndex(IReadOnlyList<TraySnapshotCandidate>? candidates, string? label)
    {
        if (candidates is null || candidates.Count == 0) return -1;
        var best = 0;
        var bestIndex = -1;
        var ties = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = Score(label, candidates[i]);
            if (score == 0) continue;
            if (score > best)
            {
                best = score;
                bestIndex = i;
                ties = 1;
            }
            else if (score == best)
            {
                ties++;
            }
        }
        return best > 0 && ties == 1 ? bestIndex : -1;
    }

    private static bool MutualContains(string a, string b)
        => a.Length >= 4 && b.Length >= 4
           && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    private static string ExeBaseName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var name = path.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
