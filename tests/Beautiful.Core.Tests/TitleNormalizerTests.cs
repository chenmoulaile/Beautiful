using Beautiful.Core.Lyrics;
using Xunit;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("晴天", "晴天")]
    [InlineData("Shape of You (Official MV)", "Shape of You")]
    [InlineData("海阔天空【Live】", "海阔天空")]
    [InlineData("Lemon (Lyrics)", "Lemon")]
    [InlineData("稻香 (Live Version)", "稻香")]
    [InlineData("See You Again (feat. Charlie Puth)", "See You Again")]
    [InlineData("漠河舞厅 - Live", "漠河舞厅")]
    [InlineData("孤勇者 (Live)", "孤勇者")]
    [InlineData("  Foo   Bar  ", "Foo Bar")]
    public void 清洗_常见修饰(string input, string expected)
        => Assert.Equal(expected, TitleNormalizer.Clean(input));

    [Fact]
    public void Key_大小写不敏感()
        => Assert.Equal(TitleNormalizer.Key("Shape of You"), TitleNormalizer.Key("shape of you"));

    [Fact]
    public void 清洗_普通括号内容_保留()
        => Assert.Equal("Everglow (Single Version)", TitleNormalizer.Clean("Everglow (Single Version)"));
}
