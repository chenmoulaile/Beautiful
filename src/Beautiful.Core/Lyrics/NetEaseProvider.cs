using System.Text.Json;

namespace Beautiful.Core.Lyrics;

/// <summary>网易云音乐歌词源：search + lyric(lrc+tlyric)。非官方接口，仅个人使用。</summary>
/// <remarks>端点用法参考 MIT 项目 wwh1004/NeteaseCloudMusicApi 的文档；数据来源标注：网易云音乐。</remarks>
public sealed class NetEaseProvider : ILyricsProvider
{
    private readonly HttpClient _http;
    public string Name => "NetEase";

    public NetEaseProvider(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    }

    public async Task<LyricFetch?> FetchAsync(TrackMeta meta, CancellationToken ct)
    {
        var q = $"{TitleNormalizer.Key(meta.Title)} {meta.Artist}";
        var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(q)}&type=1&limit=8&offset=0";
        using var resp = await _http.GetAsync(searchUrl, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) || songs.GetArrayLength() == 0)
            return null;

        long? bestId = null; long bestDiff = long.MaxValue;
        foreach (var song in songs.EnumerateArray())
        {
            // 单条畸形搜索结果（缺 id）跳过整条，不让整个源失败
            if (!song.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
            var dur = song.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            var diff = Math.Abs(dur - (long)meta.Duration.TotalMilliseconds);
            if (diff < bestDiff) { bestDiff = diff; bestId = id; }
        }
        if (bestId is null || bestDiff > 3000) return null;

        var lyricUrl = $"https://music.163.com/api/song/lyric?id={bestId}&lv=-1&tv=-1&kv=-1";
        using var lResp = await _http.GetAsync(lyricUrl, ct);
        if (!lResp.IsSuccessStatusCode) return null;
        using var lDoc = JsonDocument.Parse(await lResp.Content.ReadAsStringAsync(ct));
        var lrc = lDoc.RootElement.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var ll) ? ll.GetString() : null;
        var tlyric = lDoc.RootElement.TryGetProperty("tlyric", out var t) && t.TryGetProperty("lyric", out var tl) ? tl.GetString() : null;
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        return new LyricFetch(
            LrcParser.Parse(lrc),
            string.IsNullOrWhiteSpace(tlyric) ? null : LrcParser.Parse(tlyric),
            Name);
    }
}
