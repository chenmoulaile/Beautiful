using System.Net;
using Beautiful.Core.Lyrics;
using Xunit;

public class LyricServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new();
        public List<string> Requests { get; } = new();
        public FakeHandler Add(string urlContains, string json) { _responses[urlContains] = json; return this; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r.RequestUri!.ToString());
            foreach (var (k, v) in _responses)
                if (r.RequestUri.ToString().Contains(k))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(v) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static TrackMeta Meta(string t = "Song", string a = "Artist", int sec = 200)
        => new(t, a, "Album", TimeSpan.FromSeconds(sec));

    private static HttpClient Http(FakeHandler h) => new(h) { BaseAddress = new Uri("https://unit.test/") };

    /// <summary>首次调用抛传输异常，重试后成功返回歌词（验证传输失败重试路径）。</summary>
    private sealed class TransportThenSuccessProvider : ILyricsProvider
    {
        public string Name => "FakeTransport";
        public int Calls;
        public Task<LyricFetch?> FetchAsync(TrackMeta meta, CancellationToken ct)
            => ++Calls == 1
                ? throw new HttpRequestException("boom")
                : Task.FromResult<LyricFetch?>(new LyricFetch(LrcParser.Parse("[00:01.00]Hi"), null, Name));
    }

    /// <summary>恒返回 null 的干净未命中源（验证不重试）。</summary>
    private sealed class CleanNullProvider : ILyricsProvider
    {
        public string Name => "FakeNull";
        public int Calls;
        public Task<LyricFetch?> FetchAsync(TrackMeta meta, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<LyricFetch?>(null);
        }
    }

    [Fact]
    public async Task NetEase_命中_返回双语()
    {
        var handler = new FakeHandler()
            .Add("/api/search/get/web", """{"result":{"songs":[{"id":42,"name":"Song (Live)","duration":200000,"artists":[{"name":"Artist"}]}]}}""")
            .Add("/api/song/lyric", """{"lrc":{"lyric":"[00:01.00]Hello"},"tlyric":{"lyric":"[00:01.00]你好"}}""");
        var provider = new NetEaseProvider(Http(handler));
        var fetch = await provider.FetchAsync(Meta(), CancellationToken.None);
        Assert.NotNull(fetch);
        Assert.Equal("NetEase", fetch!.Source);
        Assert.Equal("Hello", fetch.Original[0].Text);
        Assert.Equal("你好", fetch.Translation![0].Text);
        Assert.Contains("s=", handler.Requests[0]);
    }

    [Fact]
    public async Task NetEase_搜索无结果_返回null()
    {
        var provider = new NetEaseProvider(Http(new FakeHandler()));
        Assert.Null(await provider.FetchAsync(Meta(), CancellationToken.None));
    }

    [Fact]
    public async Task LrcLib_get命中_解析同步歌词()
    {
        var handler = new FakeHandler()
            .Add("/api/get", """{"syncedLyrics":"[00:05.00]Line1","plainLyrics":"Line1"}""");
        var provider = new LrcLibProvider(Http(handler));
        var fetch = await provider.FetchAsync(Meta(), CancellationToken.None);
        Assert.NotNull(fetch);
        Assert.Equal(5, fetch!.Original[0].Time.TotalSeconds);
    }

    [Fact]
    public async Task 编排_缓存优先()
    {
        var handler = new FakeHandler()
            .Add("/api/search/get/web", """{"result":{"songs":[{"id":1,"name":"Song","duration":200000,"artists":[{"name":"Artist"}]}]}}""")
            .Add("/api/song/lyric", """{"lrc":{"lyric":"[00:01.00]A"},"tlyric":{"lyric":"[00:01.00]甲"}}""");
        var cache = new LyricCacheService(Path.Combine(Path.GetTempPath(), "beautiful-test-" + Guid.NewGuid().ToString("N")));
        var svc = new LyricService(new[] { new NetEaseProvider(Http(handler)) }, cache);
        var doc1 = await svc.GetLyricsAsync(Meta());
        Assert.NotNull(doc1);
        var calls = handler.Requests.Count;
        var doc2 = await svc.GetLyricsAsync(Meta());
        Assert.Equal(calls, handler.Requests.Count);   // 第二次未发网络请求
        Assert.Equal("甲", doc2!.Lines[0].Translation);
    }

    [Fact]
    public async Task 编排_主源失败_降级备源()
    {
        // NetEase 返回 404（无结果）→ LrcLib 命中
        var handler = new FakeHandler()
            .Add("/api/get", """{"syncedLyrics":"[00:02.00]B"}""");
        var cache = new LyricCacheService(Path.Combine(Path.GetTempPath(), "beautiful-test-" + Guid.NewGuid().ToString("N")));
        var svc = new LyricService(
            new ILyricsProvider[] { new NetEaseProvider(Http(new FakeHandler())), new LrcLibProvider(Http(handler)) },
            cache);
        var doc = await svc.GetLyricsAsync(Meta());
        Assert.NotNull(doc);
        Assert.Equal("LRCLIB", doc!.Source);
    }

    [Fact]
    public async Task 编排_全部失败_返回null()
    {
        var cache = new LyricCacheService(Path.Combine(Path.GetTempPath(), "beautiful-test-" + Guid.NewGuid().ToString("N")));
        // 注：new[] 无法在两个仅共享接口的类间推断最佳公共类型，需显式数组类型（CS0826）
        var svc = new LyricService(new ILyricsProvider[] { new NetEaseProvider(Http(new FakeHandler())), new LrcLibProvider(Http(new FakeHandler())) }, cache);
        Assert.Null(await svc.GetLyricsAsync(Meta()));
    }

    [Fact]
    public async Task 编排_传输失败重试一次后成功_返回歌词()
    {
        var cache = new LyricCacheService(Path.Combine(Path.GetTempPath(), "beautiful-test-" + Guid.NewGuid().ToString("N")));
        var provider = new TransportThenSuccessProvider();
        var svc = new LyricService(new[] { provider }, cache);
        var doc = await svc.GetLyricsAsync(Meta());
        Assert.NotNull(doc);
        Assert.Equal("Hi", doc!.Lines[0].Original);
        Assert.Equal(2, provider.Calls); // 首次失败 + 重试成功，共 2 次
    }

    [Fact]
    public async Task 编排_干净未命中_不重试()
    {
        var cache = new LyricCacheService(Path.Combine(Path.GetTempPath(), "beautiful-test-" + Guid.NewGuid().ToString("N")));
        var provider = new CleanNullProvider();
        var svc = new LyricService(new[] { provider }, cache);
        Assert.Null(await svc.GetLyricsAsync(Meta()));
        Assert.Equal(1, provider.Calls); // 返回 null（404/无结果）不触发重试
    }

    [Fact]
    public void 缓存键_含标题歌手时长()
    {
        var a = LyricService.CacheKey(Meta("Same", "Same", 100));
        var b = LyricService.CacheKey(Meta("Same", "Same", 101));
        var c = LyricService.CacheKey(Meta("Same", "Same", 100));
        Assert.Equal(a, c);
        Assert.NotEqual(a, b);
    }
}
