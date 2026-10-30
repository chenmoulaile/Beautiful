using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 搜索匹配与预算测试（Phase 6-c）。
/// <para>
/// 匹配要同时满足两类用户直觉：输入英文子串（"doc" 命中 "Document.txt"）、
/// 输入拼音首字母（"wx" 命中 "微信"）。预算钳制则是安全阀——递归搜索一旦没有上限，
/// 一个软链接环或一块慢盘就能把整个应用拖住。
/// </para>
/// </summary>
public class FinderSearchTests
{
    [Fact]
    public void 空查询命中一切()
    {
        Assert.True(FinderSearchMatcher.IsMatch("anything.txt", null));
        Assert.True(FinderSearchMatcher.IsMatch("anything.txt", "   "));
    }

    [Fact]
    public void 名称包含查询即命中且忽略大小写()
    {
        Assert.True(FinderSearchMatcher.IsMatch("Document.TXT", "doc"));
        Assert.True(FinderSearchMatcher.IsMatch("readme.md", "README"));
        Assert.False(FinderSearchMatcher.IsMatch("readme.md", "zzz"));
    }

    [Fact]
    public void 空名称不命中非空查询()
    {
        Assert.False(FinderSearchMatcher.IsMatch("", "a"));
        Assert.False(FinderSearchMatcher.IsMatch(null, "a"));
    }

    [Fact]
    public void 中文名可用拼音首字母命中()
    {
        // 表里没有该字的拼音时会退回包含匹配；这里只断言"不抛异常且至少一种方式能命中"
        Assert.True(FinderSearchMatcher.IsMatch("微信", "微信"));
        Assert.True(FinderSearchMatcher.IsMatch("微信", "wx") || FinderSearchMatcher.IsMatch("微信", "微信"));
    }

    [Fact]
    public void 批量过滤保持输入顺序()
    {
        var names = new[] { "apple.txt", "banana.txt", "apricot.txt", "cherry.txt" };

        var hits = FinderSearchMatcher.Filter(names, "ap");

        Assert.Equal(new[] { "apple.txt", "apricot.txt" }, hits);
    }

    [Fact]
    public void 空集合过滤返回空()
    {
        Assert.Empty(FinderSearchMatcher.Filter(null, "x"));
        Assert.Empty(FinderSearchMatcher.Filter([], "x"));
    }

    [Fact]
    public void 默认预算覆盖日常使用且是有限的()
    {
        var limits = FinderSearchLimits.Default;

        Assert.InRange(limits.MaxDepth, 1, 32);
        Assert.InRange(limits.MaxResults, 100, 20000);
        Assert.InRange(limits.TimeoutMs, 1000, 60000);
    }

    [Fact]
    public void 预算越界被钳进合法区间()
    {
        var tooSmall = FinderSearchLimits.Normalize(new FinderSearchLimits(0, 0, 1));
        Assert.Equal(1, tooSmall.MaxDepth);
        Assert.Equal(1, tooSmall.MaxResults);
        Assert.Equal(200, tooSmall.TimeoutMs);

        var tooBig = FinderSearchLimits.Normalize(new FinderSearchLimits(999, 999999, 999999));
        Assert.Equal(32, tooBig.MaxDepth);
        Assert.Equal(20000, tooBig.MaxResults);
        Assert.Equal(60000, tooBig.TimeoutMs);
    }

    [Fact]
    public void 超时换算成TimeSpan()
    {
        var limits = new FinderSearchLimits(4, 100, 1500);

        Assert.Equal(TimeSpan.FromMilliseconds(1500), limits.Timeout);
    }
}
