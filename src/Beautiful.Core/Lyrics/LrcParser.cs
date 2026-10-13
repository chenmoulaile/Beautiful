using System.Text.RegularExpressions;

namespace Beautiful.Core.Lyrics;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\[(ti|ar|al|by|offset|length):(.*)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    public static List<LrcLine> Parse(string lrc, int offsetMs = 0)
    {
        var result = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;
        var lines = lrc.Replace("\r\n", "\n").Split('\n');

        // 第一遍：收集歌词文件内嵌的 [offset:] 修正量（与 offsetMs 参数叠加）
        var inlineOffset = 0;
        foreach (var raw in lines)
        {
            var meta = MetaRegex().Match(raw.Trim());
            if (meta.Success &&
                meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(meta.Groups[2].Value.Trim(), out var delta))
            {
                inlineOffset += delta;
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || MetaRegex().IsMatch(line)) continue;
            var tags = TagRegex().Matches(line);
            if (tags.Count == 0) continue;
            var last = tags[^1];
            var text = line[(last.Index + last.Length)..].Trim();
            if (text.Length == 0) continue;
            foreach (Match m in tags)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var ms = 0;
                if (m.Groups[3].Success)
                {
                    var fs = m.Groups[3].Value;
                    ms = int.Parse(fs) * (int)Math.Pow(10, 3 - fs.Length);
                }
                var t = TimeSpan.FromMilliseconds((min * 60 + sec) * 1000 + ms - offsetMs - inlineOffset);
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                result.Add(new LrcLine(t, text));
            }
        }
        return result
            .OrderBy(l => l.Time)
            .ThenBy(l => l.Text, StringComparer.Ordinal)
            .ToList();
    }
}
