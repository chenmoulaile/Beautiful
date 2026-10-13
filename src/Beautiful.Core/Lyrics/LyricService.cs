using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Beautiful.Core.Lyrics;

public sealed class LyricService(IEnumerable<ILyricsProvider> providers, LyricCacheService cache)
{
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TransportRetryDelay = TimeSpan.FromMilliseconds(1500);

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
                var fetch = await FetchWithRetryAsync(provider, meta);
                if (fetch is not { Original.Count: > 0 }) continue;
                var doc = BilingualAligner.Align(fetch.Original, fetch.Translation, fetch.Source);
                if (doc.Lines.Count == 0) continue;
                _cache.Put(key, doc);
                return doc;
            }
            catch (Exception) { /* 重试后仍失败的传输异常：单源降级下一源 */ }
        }
        return null;
    }

    /// <summary>
    /// 单源获取：每源独立 3s 超时；仅传输类失败（超时/网络异常/IO）重试 1 次（间隔 1.5s），
    /// 共至多 2 次尝试。干净未命中（404/无结果，provider 返回 null）不重试。
    /// </summary>
    private static async Task<LyricFetch?> FetchWithRetryAsync(ILyricsProvider provider, TrackMeta meta)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(PerSourceTimeout);
                return await provider.FetchAsync(meta, cts.Token);
            }
            catch (Exception ex) when (attempt == 1 && IsTransport(ex))
            {
                await Task.Delay(TransportRetryDelay);
            }
        }
    }

    /// <summary>传输类失败判定：超时取消、HTTP 层错误、socket/IO 异常。provider 返回 null 的干净未命中不在此列。</summary>
    private static bool IsTransport(Exception ex)
        => ex is OperationCanceledException or HttpRequestException or IOException or SocketException;

    public static string CacheKey(TrackMeta meta)
    {
        var raw = $"{TitleNormalizer.Key(meta.Title)}|{TitleNormalizer.Key(meta.Artist)}|{(int)meta.Duration.TotalSeconds}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
