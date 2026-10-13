using System.Security.Cryptography;
using System.Text;

namespace Beautiful.Core.Lyrics;

public sealed class LyricService(IEnumerable<ILyricsProvider> providers, LyricCacheService cache)
{
    private readonly ILyricsProvider[] _providers = providers.ToArray();
    private readonly LyricCacheService _cache = cache;

    public async Task<LyricDocument?> GetLyricsAsync(TrackMeta meta)
    {
        var key = CacheKey(meta);
        var cached = _cache.TryGet(key);
        if (cached is { Lines.Count: > 0 }) return cached;

        foreach (var provider in _providers)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var fetch = await provider.FetchAsync(meta, cts.Token);
                if (fetch is not { Original.Count: > 0 }) continue;
                var doc = BilingualAligner.Align(fetch.Original, fetch.Translation, fetch.Source);
                if (doc.Lines.Count == 0) continue;
                _cache.Put(key, doc);
                return doc;
            }
            catch (OperationCanceledException) { /* 超时降级下一源 */ }
            catch (Exception) { /* 单源异常降级下一源 */ }
        }
        return null;
    }

    public static string CacheKey(TrackMeta meta)
    {
        var raw = $"{TitleNormalizer.Key(meta.Title)}|{TitleNormalizer.Key(meta.Artist)}|{(int)meta.Duration.TotalSeconds}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
