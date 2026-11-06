using Beautiful.Core.Icons;
using Xunit;

namespace Beautiful.Core.Tests;

/// <summary>
/// macOS 图标集的命名解析（v4.0-⑧）：这张别名表决定"哪些应用能换到 macOS 图标"，
/// 编错一条的表现是"某个应用看起来还是 Windows 图标"，没有任何异常可看——所以必须由测试锁住。
/// </summary>
public sealed class MacIconNamesTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Tencent\WeChat\Weixin.exe", "wechat")]
    [InlineData(@"D:\QQ\QQ.exe", "qq")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", "steam")]
    [InlineData(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "msedge")]
    [InlineData(@"C:\Program Files\Netease\CloudMusic\cloudmusic.exe", "cloudmusic")]
    public void 常见应用解析到图标集键(string exePath, string expectedKey)
    {
        var keys = MacIconNames.CandidateKeys(exePath);
        Assert.NotEmpty(keys);
        Assert.Equal(expectedKey, keys[0]);
    }

    [Fact]
    public void 别名优先于原名_微信走wechat而不是weixin()
    {
        var keys = MacIconNames.CandidateKeys("weixin.exe");
        Assert.Equal(new[] { "wechat", "weixin" }, keys);
    }

    [Fact]
    public void 别名与原名相同则不重复出键()
    {
        // qq → qq：第二项与第一项同值时必须去掉，否则每次取图都多试一次资源查找
        var keys = MacIconNames.CandidateKeys("qq.exe");
        Assert.Equal(new[] { "qq" }, keys);
    }

    [Fact]
    public void 无别名时退回exe原名()
    {
        var keys = MacIconNames.CandidateKeys(@"C:\Tools\MyApp.exe");
        Assert.Equal(new[] { "myapp" }, keys);
    }

    [Fact]
    public void 明确标记缺失的键不产出候选()
    {
        // taskmgr → activity_monitor_absent：图标集里没有活动监视器，表达"知道是谁但没这张图"
        var keys = MacIconNames.CandidateKeys("taskmgr.exe");
        Assert.DoesNotContain("activity_monitor_absent", keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空输入返回空候选(string? input)
    {
        Assert.Empty(MacIconNames.CandidateKeys(input));
    }

    [Fact]
    public void 路径与裸名解析结果一致()
    {
        var viaPath = MacIconNames.CandidateKeys(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\wt.exe");
        var viaName = MacIconNames.CandidateKeys("wt");
        Assert.Equal(viaName, viaPath);
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.Equal(MacIconNames.CandidateKeys("WEIXIN.EXE"), MacIconNames.CandidateKeys("weixin.exe"));
    }

    [Fact]
    public void 中文应用名也能命中()
    {
        var keys = MacIconNames.CandidateKeys("哔哩哔哩.exe");
        Assert.Equal("bilibili", keys[0]);
    }

    [Fact]
    public void 候选键全部是小写()
    {
        foreach (var name in new[] { "Weixin.EXE", @"C:\A\B\Code.exe", "Notepad++.exe" })
            foreach (var key in MacIconNames.CandidateKeys(name))
                Assert.Equal(key.ToLowerInvariant(), key);
    }
}
