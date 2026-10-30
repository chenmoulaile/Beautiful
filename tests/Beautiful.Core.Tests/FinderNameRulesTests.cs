using System;
using System.Collections.Generic;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 文件名校验与命名冲突测试（Phase 6-b 纯函数）。
/// <para>
/// 命名规则是后续复制/移动/重命名的唯一入口：非法字符、Windows 保留名、尾随空格/句点
/// 与同名序号都由这里决定，App 侧不重复实现。所有用例只处理字符串，不碰文件系统。
/// </para>
/// </summary>
public class FinderNameRulesTests
{
    // ---------- Validate ----------

    [Theory]
    [InlineData("report.docx")]
    [InlineData("我的报告.txt")]
    [InlineData(".gitignore")]
    [InlineData("a b")]
    [InlineData("COM0")]
    [InlineData("CONSOLE")]
    [InlineData("LPT10")]
    public void 合法名称_返回null(string name)
    {
        Assert.Null(FinderNameRules.Validate(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空名称_报不能为空(string? name)
    {
        Assert.Equal("名称不能为空", FinderNameRules.Validate(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void 点与双点_单独报错(string name)
    {
        Assert.Equal("名称不能是 . 或 ..", FinderNameRules.Validate(name));
    }

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\u0001b")]
    [InlineData("a\nb")]
    public void 非法字符与控制字符_报非法字符(string name)
    {
        Assert.Equal("名称包含非法字符", FinderNameRules.Validate(name));
    }

    [Theory]
    [InlineData("report ")]
    [InlineData("report  ")]
    [InlineData("report.")]
    [InlineData("report. ")]
    public void 尾随空格或句点_报结尾非法(string name)
    {
        Assert.Equal("名称不能以空格或句点结尾", FinderNameRules.Validate(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("PRN.log")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9.tar.gz")]
    public void 保留名_报Windows保留名(string name)
    {
        Assert.Equal("名称是 Windows 保留名", FinderNameRules.Validate(name));
    }

    [Fact]
    public void 名称最长255_允许()
    {
        Assert.Null(FinderNameRules.Validate(new string('a', 255)));
    }

    [Fact]
    public void 名称超过255_报过长()
    {
        Assert.Equal("名称过长", FinderNameRules.Validate(new string('a', 256)));
    }

    // ---------- Unique ----------

    [Fact]
    public void 唯一名_无冲突时原样返回()
    {
        Assert.Equal("report.docx", FinderNameRules.Unique("report.docx", new[] { "other.docx", "Report" }));
    }

    [Fact]
    public void 唯一名_冲突时在扩展名前插入序号()
    {
        Assert.Equal("report (1).docx", FinderNameRules.Unique("report.docx", new[] { "REPORT.DOCX" }));
    }

    [Fact]
    public void 唯一名_无扩展名时追加序号()
    {
        Assert.Equal("report (1)", FinderNameRules.Unique("report", new[] { "report" }));
    }

    [Fact]
    public void 唯一名_点文件整体作为文件名()
    {
        Assert.Equal(".gitignore (1)", FinderNameRules.Unique(".gitignore", new[] { ".gitignore" }));
    }

    [Fact]
    public void 唯一名_多个点时只在最后一个点前插入()
    {
        Assert.Equal("a.b (1).c", FinderNameRules.Unique("a.b.c", new[] { "a.b.c" }));
    }

    [Fact]
    public void 唯一名_已有序号时继续递增且不区分大小写()
    {
        Assert.Equal(
            "report (3).docx",
            FinderNameRules.Unique("report.docx", new[] { "report.docx", "report (1).docx", "REPORT (2).DOCX" }));
    }

    [Fact]
    public void 唯一名_支持自定义后缀格式()
    {
        Assert.Equal("report-1.docx", FinderNameRules.Unique("report.docx", new[] { "report.docx" }, "{0}-{1}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 唯一名_空名称抛ArgumentException(string? desired)
    {
        Assert.Throws<ArgumentException>(() => FinderNameRules.Unique(desired!, Array.Empty<string>()));
    }

    [Fact]
    public void 唯一名_序号用尽时抛InvalidOperationException()
    {
        var existing = new List<string> { "a" };
        for (var i = 1; i <= 9999; i++)
        {
            existing.Add($"a ({i})");
        }

        Assert.Throws<InvalidOperationException>(() => FinderNameRules.Unique("a", existing));
    }

    // ---------- CopyName ----------

    [Theory]
    [InlineData("report.docx", "report copy.docx")]
    [InlineData("report", "report copy")]
    [InlineData(".gitignore", ".gitignore copy")]
    [InlineData("a.b.c", "a.b copy.c")]
    public void 复制名_在扩展名前插入copy(string name, string expected)
    {
        Assert.Equal(expected, FinderNameRules.CopyName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 复制名_空名称抛ArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => FinderNameRules.CopyName(name!));
    }

    // ---------- IsReservedName ----------

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("aux.log")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9.txt")]
    public void 保留名判定_基础名不区分大小写(string name)
    {
        Assert.True(FinderNameRules.IsReservedName(name));
    }

    [Theory]
    [InlineData("CON0")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("LPT10")]
    [InlineData("CONSOLE")]
    [InlineData("xCON")]
    [InlineData("CON ")]
    [InlineData("")]
    [InlineData(null)]
    public void 保留名判定_相近名称不算(string? name)
    {
        Assert.False(FinderNameRules.IsReservedName(name!));
    }
}
