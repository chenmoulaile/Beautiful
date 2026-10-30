using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 拖放计划测试（Phase 6-b 纯函数）。
/// <para>
/// 计划层只决定“每个源会落到目标目录的哪个名字”，不执行复制/移动：
/// 同名冲突按来源顺序分配序号，源按完整路径去重（不区分大小写），
/// 复制与移动共用同一套命名规则。移动到源目录本身也照常返回计划项，是否空操作由调用方决定。
/// </para>
/// </summary>
public class FinderDropPlannerTests
{
    private static readonly string[] NoNames = Array.Empty<string>();

    [Fact]
    public void 无冲突_保持原名并组合目标路径()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\Src\report.docx" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        AssertItem(item, @"C:\Src\report.docx", @"C:\Target\report.docx", "report.docx", false);
    }

    [Fact]
    public void 已有同名_生成序号并标记改名()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\Src\report.docx" },
            @"C:\Target",
            new[] { "REPORT.DOCX" },
            FinderDropAction.Move);

        var item = Assert.Single(plan);
        AssertItem(item, @"C:\Src\report.docx", @"C:\Target\report (1).docx", "report (1).docx", true);
    }

    [Fact]
    public void 多个同名源_按来源顺序递增序号()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\file.txt", @"C:\B\file.txt", @"C:\C\file.txt" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Copy);

        Assert.Collection(
            plan,
            item => AssertItem(item, @"C:\A\file.txt", @"C:\Target\file.txt", "file.txt", false),
            item => AssertItem(item, @"C:\B\file.txt", @"C:\Target\file (1).txt", "file (1).txt", true),
            item => AssertItem(item, @"C:\C\file.txt", @"C:\Target\file (2).txt", "file (2).txt", true));
    }

    [Fact]
    public void 计划内名称冲突不区分大小写()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\File.txt", @"C:\B\file.txt" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Copy);

        Assert.Collection(
            plan,
            item => AssertItem(item, @"C:\A\File.txt", @"C:\Target\File.txt", "File.txt", false),
            item => AssertItem(item, @"C:\B\file.txt", @"C:\Target\file (1).txt", "file (1).txt", true));
    }

    [Fact]
    public void 已有名称与计划名称共同参与避让()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\a.txt", @"C:\B\a.txt" },
            @"C:\Target",
            new[] { "a (1).txt" },
            FinderDropAction.Copy);

        Assert.Collection(
            plan,
            item => AssertItem(item, @"C:\A\a.txt", @"C:\Target\a.txt", "a.txt", false),
            item => AssertItem(item, @"C:\B\a.txt", @"C:\Target\a (2).txt", "a (2).txt", true));
    }

    [Fact]
    public void 源按完整路径去重_保留首次顺序()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\file.txt", @"c:\a\FILE.TXT", @"C:\B\other.txt" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Copy);

        Assert.Collection(
            plan,
            item => AssertItem(item, @"C:\A\file.txt", @"C:\Target\file.txt", "file.txt", false),
            item => AssertItem(item, @"C:\B\other.txt", @"C:\Target\other.txt", "other.txt", false));
    }

    [Fact]
    public void 源按完整路径去重_忽略尾随分隔符()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\Folder\", @"C:\A\Folder" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Move);

        var item = Assert.Single(plan);
        AssertItem(item, @"C:\A\Folder\", @"C:\Target\Folder", "Folder", false);
    }

    [Fact]
    public void 空源与空名称_跳过()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { null!, "", "   ", @"C:\Src\ok.txt" },
            @"C:\Target",
            NoNames,
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        Assert.Equal(@"C:\Src\ok.txt", item.SourcePath);
    }

    [Fact]
    public void null源集合_返回空计划()
    {
        Assert.Empty(FinderDropPlanner.Plan(null!, @"C:\Target", NoNames, FinderDropAction.Copy));
    }

    [Fact]
    public void null已有名称集合_按无冲突处理()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\a.txt" },
            @"C:\Target",
            null!,
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        Assert.Equal("a.txt", item.FinalName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空目标目录_抛ArgumentException(string? target)
    {
        Assert.Throws<ArgumentException>(() => FinderDropPlanner.Plan(
            new[] { @"C:\A\a.txt" },
            target!,
            NoNames,
            FinderDropAction.Copy));
    }

    [Fact]
    public void 复制与移动_计划完全一致()
    {
        var sources = new[] { @"C:\A\a.txt", @"C:\B\a.txt" };

        var copy = FinderDropPlanner.Plan(sources, @"C:\Target", NoNames, FinderDropAction.Copy);
        var move = FinderDropPlanner.Plan(sources, @"C:\Target", NoNames, FinderDropAction.Move);

        Assert.Equal(copy.ToArray(), move.ToArray());
    }

    [Fact]
    public void 移动到源目录本身_仍返回计划项_由调用方决定空操作()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\A\file.txt" },
            @"C:\A",
            NoNames,
            FinderDropAction.Move);

        var item = Assert.Single(plan);
        AssertItem(item, @"C:\A\file.txt", @"C:\A\file.txt", "file.txt", false);
    }

    [Fact]
    public void 目标目录为根_正确组合()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\Src\a.txt" },
            @"C:\",
            NoNames,
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        Assert.Equal(@"C:\a.txt", item.TargetPath);
    }

    [Fact]
    public void 目标目录带尾随分隔符_不产生双分隔符()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\Src\a.txt" },
            @"C:\Target\",
            NoNames,
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        Assert.Equal(@"C:\Target\a.txt", item.TargetPath);
    }

    [Fact]
    public void 点文件冲突_整体追加序号()
    {
        var plan = FinderDropPlanner.Plan(
            new[] { @"C:\Src\.gitignore" },
            @"C:\Target",
            new[] { ".gitignore" },
            FinderDropAction.Copy);

        var item = Assert.Single(plan);
        AssertItem(item, @"C:\Src\.gitignore", @"C:\Target\.gitignore (1)", ".gitignore (1)", true);
    }

    private static void AssertItem(
        FinderDropItem item,
        string source,
        string target,
        string finalName,
        bool nameChanged)
    {
        Assert.Equal(source, item.SourcePath);
        Assert.Equal(target, item.TargetPath);
        Assert.Equal(finalName, item.FinalName);
        Assert.Equal(nameChanged, item.NameChanged);
    }
}
