using Beautiful.Core.Lyrics;
using Xunit;

public class BilingualAlignerTests
{
    private static LrcLine L(int ms, string t) => new(TimeSpan.FromMilliseconds(ms), t);

    [Fact]
    public void 对齐_基本双语()
    {
        var orig = new[] { L(1000, "Hello"), L(5000, "World") };
        var tran = new[] { L(1010, "你好"), L(4990, "世界") };
        var doc = BilingualAligner.Align(orig, tran, "netease");
        Assert.All(doc.Lines, l => Assert.NotNull(l.Translation));
        Assert.Equal("你好", doc.Lines[0].Translation);
        Assert.True(doc.HasTranslation);
        Assert.Equal("netease", doc.Source);
    }

    [Fact]
    public void 对齐_超出容差的译文_丢弃()
    {
        var orig = new[] { L(1000, "A") };
        var tran = new[] { L(5000, "B") };
        var doc = BilingualAligner.Align(orig, tran, "s");
        Assert.Null(doc.Lines[0].Translation);
        Assert.False(doc.HasTranslation);
    }

    [Fact]
    public void 对齐_译文不重复占用()
    {
        var orig = new[] { L(1000, "A"), L(1200, "B") };   // 两条都在译文 1100 的容差内
        var tran = new[] { L(1100, "译文") };
        var doc = BilingualAligner.Align(orig, tran, "s");
        Assert.Equal(1, doc.Lines.Count(l => l.Translation != null));
    }

    [Fact]
    public void 对齐_空译文_无翻译()
    {
        var orig = new[] { L(1000, "A") };
        var doc = BilingualAligner.Align(orig, Array.Empty<LrcLine>(), "s");
        Assert.False(doc.HasTranslation);
    }

    [Fact]
    public void 对齐_重复原文行_去重()
    {
        var orig = new[] { L(1000, "A"), L(1000, "A") };
        var doc = BilingualAligner.Align(orig, null, "s");
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void 偏移_正数提前_不越零()
    {
        var orig = new[] { L(1000, "A"), L(5000, "B") };
        var doc = BilingualAligner.WithOffset(BilingualAligner.Align(orig, null, "s"), 1500);
        Assert.Equal(0, doc.Lines[0].Time.TotalMilliseconds);
        Assert.Equal(3500, doc.Lines[1].Time.TotalMilliseconds);
    }
}
