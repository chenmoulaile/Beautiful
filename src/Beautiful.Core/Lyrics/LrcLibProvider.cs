using System.Text.Json;

namespace Beautiful.Core.Lyrics;

/// <summary>LRCLIB 开放歌词 API（https://lrclib.net/docs）。数据来源标注：lrclib.net。</summary>
public sealed class LrcLibProvider(HttpClient http) : ILyricsProvider
{
    public string Name => "LRCLIB";

    public async Task<LyricFetch?> FetchAsync(TrackMeta meta, CancellationToken ct)
    {
        var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(TitleNormalizer.Clean(meta.Title))}" +
                  $"&artist_name={Uri.EscapeDataString(meta.Artist)}" +
                  $"&album_name={Uri.EscapeDataString(meta.Album ?? "")}" +
                  $"&duration={(int)meta.Duration.TotalSeconds}";
        using var resp = await http.GetAsync(url, ct);
        string? synced = null;
        if (resp.IsSuccessStatusCode)
        {
            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            synced = root.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(synced))
        {
            // 回退搜索：取时长最接近且带同步歌词的结果
            var sUrl = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(TitleNormalizer.Clean(meta.Title))}" +
                       $"&artist_name={Uri.EscapeDataString(meta.Artist)}";
            using var sResp = await http.GetAsync(sUrl, ct);
            if (!sResp.IsSuccessStatusCode) return null;
            using var sDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync(ct));
            var arr = sDoc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
            string? best = null; long bestDiff = long.MaxValue;
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var sl) || sl.GetString() is not { Length: > 0 }) continue;
                var dur = item.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
                var diff = Math.Abs(dur - (long)meta.Duration.TotalSeconds);
                if (diff < bestDiff) { bestDiff = diff; best = sl.GetString(); }
            }
            synced = best;
        }
        if (string.IsNullOrWhiteSpace(synced)) return null;
        return new LyricFetch(LrcParser.Parse(synced), null, Name);
    }
}
