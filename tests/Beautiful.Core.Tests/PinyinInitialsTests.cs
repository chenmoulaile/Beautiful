using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Launcher;
using Xunit;

/// <summary>
/// 拼音首字母表取证（v3 T7 启动器中文搜索的地基）。
/// 这里钉两件事：① 常用应用名的折算结果（表错一个字母，用户就搜不到那个应用，而且没人会知道为什么）；
/// ② 表外汉字与分隔符的行为（它们决定了"未知汉字会不会把整条序列带偏"）。
/// </summary>
public class PinyinInitialsTests
{
    /// <summary>把 Positions 的候选拼成一串（单位=每位置一个字母；多音字位置会多一个字母）。</summary>
    private static string Joined(string name) => string.Join("", PinyinInitials.Positions(name));

    // ---------- 常用应用名 ----------

    [Theory]
    [InlineData("计算器", "jsq")]           // jì suàn qì
    [InlineData("记事本", "jsb")]           // jì shì běn
    [InlineData("画图", "ht")]
    [InlineData("资源管理器", "zyglq")]
    [InlineData("控制面板", "kzmb")]
    [InlineData("任务管理器", "rwglq")]
    [InlineData("命令提示符", "mltsf")]
    [InlineData("微信", "wx")]
    [InlineData("钉钉", "dd")]
    [InlineData("腾讯会议", "txhy")]
    [InlineData("百度网盘", "bdwp")]
    [InlineData("爱奇艺", "aqy")]
    [InlineData("抖音", "dy")]
    [InlineData("快手", "ks")]
    [InlineData("淘宝", "tb")]
    [InlineData("支付宝", "zfb")]
    [InlineData("小红书", "xhs")]
    [InlineData("知乎", "zh")]
    [InlineData("搜狗输入法", "sgsrf")]
    [InlineData("剪贴板", "jtb")]
    [InlineData("截图", "jt")]
    [InlineData("录音机", "lyj")]
    [InlineData("备忘录", "bwl")]
    [InlineData("远程桌面", "yczm")]
    [InlineData("安全中心", "aqzx")]
    [InlineData("微软商店", "wrsd")]
    [InlineData("照片", "zp")]
    [InlineData("日历", "rl")]
    [InlineData("时钟", "sz")]
    [InlineData("天气", "tq")]
    [InlineData("地图", "dt")]
    [InlineData("浏览器", "llq")]
    [InlineData("视频", "sp")]
    [InlineData("邮件", "yj")]
    [InlineData("设置", "sz")]
    [InlineData("写字板", "xzb")]
    [InlineData("启动", "qd")]
    public void 常用应用名折算成预期首字母(string name, string expected)
        => Assert.Equal(expected, Joined(name));

    [Fact]
    public void 拉丁词只贡献词首字母()
    {
        // 完整拼写走文本命中（LauncherIndex 的前缀/子串档），缩写位只负责"词首速记"
        Assert.Equal("e", Joined("Edge"));
        Assert.Equal("vc", Joined("vs code"));
        Assert.Equal("7z", Joined("7-Zip"));
    }

    [Fact]
    public void 中英混排两侧都能缩写()
        => Assert.Equal("wre", Joined("微软Edge"));     // 微软 → w,r；Edge 是一个拉丁词 → e

    [Fact]
    public void 分隔符不占首字母位置()
    {
        // 首字母缩写的通行口径：Task Manager = tm（空格不是键，而是词界）
        Assert.Equal("tm", Joined("Task Manager"));
        Assert.Equal("tm", Joined("task-manager"));
    }

    // ---------- 多音字 ----------

    [Fact]
    public void 音乐的两个读音都收得到()
    {
        var positions = PinyinInitials.Positions("音乐");
        Assert.True(PinyinInitials.Matches(positions, "yy", prefixOnly: true));   // yīn yuè
        Assert.True(PinyinInitials.Matches(positions, "yl", prefixOnly: true));   // yīn lè
        Assert.False(PinyinInitials.Matches(positions, "yz", prefixOnly: true));
    }

    [Fact]
    public void 常见的多音字给两个候选而不是三个()
    {
        Assert.Equal("ly", PinyinInitials.CandidatesOf('乐'));
        Assert.Equal("cz", PinyinInitials.CandidatesOf('重'));
        Assert.Equal("dt", PinyinInitials.CandidatesOf('调'));
    }

    // ---------- 表外汉字与健壮性 ----------

    [Fact]
    public void 表外汉字保留自身于是任何字母查询都对不上()
    {
        // 鑫/淼/燚 这类字不在表里（开始菜单不会出现），必须"匹配不到"而不是"猜一个字母"
        Assert.Equal("鑫", Joined("鑫"));
        Assert.False(PinyinInitials.Matches(PinyinInitials.Positions("鑫"), "x", prefixOnly: true));
        Assert.False(PinyinInitials.Matches(PinyinInitials.Positions("鑫"), "xin", prefixOnly: false));
    }

    [Fact]
    public void 未知字不会让同名的已知字错位()
    {
        // "计算" 两位都在表里，即便后面跟个生僻字，前缀匹配仍然成立；
        // 反过来第三位是生僻字时，"jsq" 必须落空——绝不能把未知字当成某个字母凑上。
        var positions = PinyinInitials.Positions("计算鑫");
        Assert.True(PinyinInitials.Matches(positions, "js", prefixOnly: true));
        Assert.False(PinyinInitials.Matches(positions, "jsq", prefixOnly: true));
    }

    [Fact]
    public void 空前缀与空白查询一律不命中()
    {
        var positions = PinyinInitials.Positions("计算器");
        Assert.False(PinyinInitials.Matches(positions, "", prefixOnly: true));
        Assert.False(PinyinInitials.Matches(positions, "   ", prefixOnly: true));
        Assert.False(PinyinInitials.Matches(Array.Empty<string>(), "jsq", prefixOnly: true));
    }

    [Fact]
    public void 带空格的查询与不带同解()
    {
        var positions = PinyinInitials.Positions("资源管理器");
        Assert.True(PinyinInitials.Matches(positions, "zy gl", prefixOnly: true));
        Assert.True(PinyinInitials.Matches(positions, "zygl", prefixOnly: true));
    }

    [Fact]
    public void 子串匹配能命中词中首字母()
    {
        var positions = PinyinInitials.Positions("任务管理器");     // rwglq
        Assert.True(PinyinInitials.Matches(positions, "gl", prefixOnly: false));
        Assert.False(PinyinInitials.Matches(positions, "gl", prefixOnly: true));
    }

    [Fact]
    public void 同一输入恒得同一输出()
    {
        // 排序契约的地基：折算若是随哈希序/字典序变化的，同分并列的次序就会飘
        var first = PinyinInitials.Positions("资源管理器控制面板");
        for (var i = 0; i < 50; i++)
            Assert.Equal(string.Join("", first), string.Join("", PinyinInitials.Positions("资源管理器控制面板")));
    }

    [Fact]
    public void 表规模足以覆盖常用应用名()
        => Assert.True(PinyinInitials.TableSize > 500, $"字表过小（{PinyinInitials.TableSize}）——改表时请一并核对折算用例");

    [Fact]
    public void null与空串给空序列而不是抛()
    {
        Assert.Empty(PinyinInitials.Positions(null));
        Assert.Empty(PinyinInitials.Positions(""));
    }
}
