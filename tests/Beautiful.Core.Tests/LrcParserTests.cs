using Beautiful.Core.Lyrics;
using Xunit;

public class LrcParserTests
{
    [Fact]
    public void 解析_基本行()
    {
        var lines = LrcParser.Parse("[00:12.50]Hello world");
        var l = Assert.Single(lines);
        Assert.Equal(TimeSpan.FromMilliseconds(12500), l.Time);
        Assert.Equal("Hello world", l.Text);
    }

    [Fact]
    public void 解析_一行多时间标签()
    {
        var lines = LrcParser.Parse("[00:01.00][01:02.500]副歌");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(62.5), lines[1].Time);
        Assert.All(lines, l => Assert.Equal("副歌", l.Text));
    }

    [Fact]
    public void 解析_按时间排序_元数据与空行跳过()
    {
        var lrc = "[ti:歌名]\n[ar:歌手]\n[02:00.00]B\n[00:30.00]A\n\n[by:x]\n[00:00]C";
        var lines = LrcParser.Parse(lrc);
        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "C", "A", "B" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void 解析_offset标签_正偏移提前()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.000]X");
        Assert.Equal(TimeSpan.FromMilliseconds(9500), Assert.Single(lines).Time);
    }

    [Fact]
    public void 解析_两位与三位毫秒()
    {
        var a = Assert.Single(LrcParser.Parse("[00:01.5]A"));   // 500ms
        var b = Assert.Single(LrcParser.Parse("[00:01.05]B"));  // 50ms
        Assert.Equal(1500, a.Time.TotalMilliseconds);
        Assert.Equal(1050, b.Time.TotalMilliseconds);
    }

    [Fact]
    public void 解析_无标签行_忽略()
    {
        // 无标签行被跳过，且不影响后续带标签行的解析
        var lines = LrcParser.Parse("just text\n[00:01.00]ok");
        var l = Assert.Single(lines);
        Assert.Equal(TimeSpan.FromSeconds(1), l.Time);
        Assert.Equal("ok", l.Text);
    }

    [Fact]
    public void 解析_空输入_返回空()
    {
        Assert.Empty(LrcParser.Parse(""));
        Assert.Empty(LrcParser.Parse("   \n  "));
    }
}
