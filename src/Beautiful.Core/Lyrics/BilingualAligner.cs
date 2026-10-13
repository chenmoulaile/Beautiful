namespace Beautiful.Core.Lyrics;

public static class BilingualAligner
{
    public static LyricDocument Align(
        IReadOnlyList<LrcLine> original,
        IReadOnlyList<LrcLine>? translation,
        string source,
        TimeSpan? tolerance = null)
    {
        var tol = tolerance ?? TimeSpan.FromMilliseconds(150);
        // 原文去重：同时间同文本只保留一条
        var orig = original
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .GroupBy(l => (l.Time, l.Text))
            .Select(g => g.First())
            .OrderBy(l => l.Time)
            .ToList();
        if (translation is not { Count: > 0 })
            return new LyricDocument(orig.Select(l => new LyricLine(l.Time, l.Text, null)).ToList(), source, false);

        // 译文就近匹配：每条译文匹配最近的原文行（±tol），每条原文行最多被占用一次
        var assigned = new string?[orig.Count];
        var used = new bool[orig.Count];
        foreach (var t in translation)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            var best = -1;
            var bestDiff = TimeSpan.MaxValue;
            for (var i = 0; i < orig.Count; i++)
            {
                if (used[i]) continue;
                var diff = (orig[i].Time - t.Time).Duration();
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            if (best >= 0 && bestDiff <= tol)
            {
                assigned[best] = t.Text;
                used[best] = true;
            }
        }
        var lines = orig
            .Select((l, i) => new LyricLine(l.Time, l.Text, assigned[i]))
            .ToList();
        return new LyricDocument(lines, source, lines.Any(l => l.Translation != null));
    }

    public static LyricDocument WithOffset(LyricDocument doc, int offsetMs)
        => doc with
        {
            Lines = doc.Lines
                .Select(l => l with { Time = l.Time - TimeSpan.FromMilliseconds(offsetMs) < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : l.Time - TimeSpan.FromMilliseconds(offsetMs) })
                .ToList()
        };
}
