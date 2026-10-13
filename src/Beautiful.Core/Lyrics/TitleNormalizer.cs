using System.Text.RegularExpressions;

namespace Beautiful.Core.Lyrics;

public static partial class TitleNormalizer
{
    [GeneratedRegex(@"[(\[【]([^\)\]】]{1,40})[)\]】]")]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"^(official\s+)?(music\s+)?(mv|video|lyrics?|audio|visualizer|live|acoustic|remaster(ed)?|ver\.?|version|hd|4k|hq|字幕|歌词|现场版?|完整版?|纯享版?|官方\S{0,4})(?:\s+(?:mv|video|lyrics?|audio|visualizer|live|acoustic|remaster(ed)?|ver\.?|version|hd|4k|hq|字幕|歌词|现场版?|完整版?|纯享版?|官方\S{0,4}))*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrimKeyword();

    [GeneratedRegex(@"\s+\(?\s*(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Feat();

    [GeneratedRegex(@"\s+-\s+(live|remaster(ed)?|acoustic|ver\.?|version)\S*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();

    public static string Clean(string title)
    {
        var s = (title ?? "").Trim();
        s = Feat().Replace(s, "");
        string prev;
        do
        {
            prev = s;
            s = BracketGroup().Replace(s, m =>
                TrimKeyword().IsMatch(m.Groups[1].Value.Trim()) ? "" : m.Value);
        } while (s != prev);
        s = DashSuffix().Replace(s, "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim().TrimEnd('-', '—', ' ').Trim();
        return s;
    }

    public static string Key(string title) => Clean(title).ToLowerInvariant();
}
