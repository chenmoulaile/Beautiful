using System;
using System.Collections.Generic;
using Beautiful.Core.Tray;
using Xunit;

/// <summary>
/// 顶栏托盘条「用 Windows 自己存的 IconSnapshot PNG 回填」的匹配规则。
/// <para>为什么必须是纯函数：注册表存单是历史清单（应用退出后记录还在），UIA 给的 Name 又常带
/// 动态后缀（"音量 本机扬声器: 已静音"）。匹配错一格就是把 A 应用的图标画到 B 应用头上——
/// 比画中性占位更糟。所以「同分多候选一律不命中」必须被单测钉死。</para>
/// </summary>
public class TraySnapshotMatchTests
{
    private static TraySnapshotCandidate C(string tooltip, string exe = "")
        => new(tooltip, exe);

    // ---------- Normalize ----------

    [Theory]
    [InlineData("  华为电脑管家  ", "华为电脑管家")]
    [InlineData("ＱＱ", "qq")]
    [InlineData("Wi-Fi", "wi-fi")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalize_去空白全半角与大小写(string? input, string expected)
        => Assert.Equal(expected, TraySnapshotMatch.Normalize(input));

    // ---------- Score 三档 ----------

    [Fact]
    public void Tooltip_完全相等_给最高档()
        => Assert.Equal(TraySnapshotMatch.ExactTooltip,
            TraySnapshotMatch.Score("华为电脑管家", C("华为电脑管家", @"D:\Huawei\PCManager.exe")));

    [Fact]
    public void Exe_文件名相等_给第二档()
        => Assert.Equal(TraySnapshotMatch.ExactExe,
            TraySnapshotMatch.Score("PCManager", C("", @"D:\Huawei\PCManager.exe")));

    [Fact]
    public void 带动态后缀的名字_走互含档()
        => Assert.Equal(TraySnapshotMatch.Contains,
            TraySnapshotMatch.Score("华为电脑管家 (已连接)", C("华为电脑管家", @"D:\Huawei\PCManager.exe")));

    [Fact]
    public void 互含但短于四字_不命中()
        => Assert.Equal(0, TraySnapshotMatch.Score("QQ", C("QQ音乐", @"D:\QQMusic.exe")));

    [Fact]
    public void 毫不相干_不命中()
        => Assert.Equal(0, TraySnapshotMatch.Score("微信", C("华为电脑管家", @"D:\Huawei\PCManager.exe")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空名字_永远不命中(string? label)
        => Assert.Equal(0, TraySnapshotMatch.Score(label, C("华为电脑管家", @"D:\Huawei\PCManager.exe")));

    [Fact]
    public void 候选也空_不命中()
        => Assert.Equal(0, TraySnapshotMatch.Score("华为电脑管家", C("", "")));

    // ---------- MatchIndex ----------

    [Fact]
    public void 唯一最佳候选_返回它的下标()
    {
        var list = new List<TraySnapshotCandidate>
        {
            C("微信", @"D:\WeChat.exe"),
            C("华为电脑管家", @"D:\Huawei\PCManager.exe"),
            C("安全中心", @"D:\Defender.exe"),
        };
        Assert.Equal(1, TraySnapshotMatch.MatchIndex(list, "华为电脑管家"));
    }

    [Fact]
    public void 同分多候选_歧义一律不命中()
    {
        var list = new List<TraySnapshotCandidate>
        {
            C("华为电脑管家", @"D:\old\PCManager.exe"),
            C("华为电脑管家", @"D:\new\PCManager.exe"),
        };
        Assert.Equal(-1, TraySnapshotMatch.MatchIndex(list, "华为电脑管家"));
    }

    [Fact]
    public void 没有候选得分_返回负一()
        => Assert.Equal(-1, TraySnapshotMatch.MatchIndex(
            new[] { C("微信", @"D:\WeChat.exe") }, "华为电脑管家"));

    [Fact]
    public void 空清单或空引用_返回负一()
    {
        Assert.Equal(-1, TraySnapshotMatch.MatchIndex(Array.Empty<TraySnapshotCandidate>(), "华为电脑管家"));
        Assert.Equal(-1, TraySnapshotMatch.MatchIndex(null!, "华为电脑管家"));
    }
}
