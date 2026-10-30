using System.Collections.Generic;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 每个文件夹视图选项的持久化规则测试（Phase 6-b）。
/// <para>
/// 访达要求"每个文件夹记住自己的显示方式"，因此选项必须按路径存。这里钉死的是：
/// 键对大小写与尾随分隔符不敏感（Windows 语义）、非法值钳到默认而不是抛异常、
/// 字典有容量上限（否则长期使用会让 json 无限膨胀）。
/// </para>
/// </summary>
public class FinderViewOptionsTests
{
    [Fact]
    public void 默认选项是列表视图名称升序且不显示隐藏项()
    {
        var options = FinderViewOptions.Default;

        Assert.Equal(FinderViewMode.List, options.Mode);
        Assert.Equal(FinderSortKey.Name, options.SortKey);
        Assert.False(options.Descending);
        Assert.False(options.ShowHidden);
        Assert.Equal(64, options.IconSize);
    }

    [Fact]
    public void 归一化会把非法枚举回落默认并把图标尺寸吸附到档位()
    {
        var broken = new FinderViewOptions((FinderViewMode)99, (FinderSortKey)42, true, true, 73);
        var fixedOptions = FinderViewOptionRules.Normalize(broken);

        Assert.Equal(FinderViewMode.List, fixedOptions.Mode);
        Assert.Equal(FinderSortKey.Name, fixedOptions.SortKey);
        Assert.Equal(64, fixedOptions.IconSize);
        Assert.True(fixedOptions.Descending);   // 布尔字段原样保留
        Assert.True(fixedOptions.ShowHidden);
    }

    [Fact]
    public void 图标尺寸为零或负数按默认档处理()
    {
        var zero = FinderViewOptionRules.Normalize(FinderViewOptions.Default with { IconSize = 0 });
        var negative = FinderViewOptionRules.Normalize(FinderViewOptions.Default with { IconSize = -20 });

        Assert.Equal(64, zero.IconSize);
        Assert.Equal(64, negative.IconSize);
    }

    [Fact]
    public void 路径键忽略大小写与尾随分隔符()
    {
        var a = FinderViewOptionRules.KeyOf(@"C:\Users\Me\Docs");
        var b = FinderViewOptionRules.KeyOf(@"c:\users\me\docs\");

        Assert.Equal(a, b);
        Assert.Equal(@"c:\users\me\docs", a);
    }

    [Fact]
    public void 空路径的键为空串()
    {
        Assert.Equal(string.Empty, FinderViewOptionRules.KeyOf(null));
        Assert.Equal(string.Empty, FinderViewOptionRules.KeyOf("  "));
    }

    [Fact]
    public void 写入同一文件夹不会产生重复记录()
    {
        var map = new Dictionary<string, FinderViewOptions>();
        map = FinderViewOptionRules.With(map, @"C:\A", FinderViewOptions.Default);
        map = FinderViewOptionRules.With(map, @"c:\a\", FinderViewOptions.Default with { Mode = FinderViewMode.Icons });

        Assert.Single(map);
        Assert.Equal(FinderViewMode.Icons, map[@"c:\a"].Mode);
    }

    [Fact]
    public void 写入不修改入参字典()
    {
        var source = new Dictionary<string, FinderViewOptions>
        {
            [@"c:\a"] = FinderViewOptions.Default
        };

        var updated = FinderViewOptionRules.With(source, @"C:\B", FinderViewOptions.Default);

        Assert.Single(source);
        Assert.Equal(2, updated.Count);
    }

    [Fact]
    public void 空路径写入被忽略但仍返回原记录的归一化副本()
    {
        var source = new Dictionary<string, FinderViewOptions>
        {
            [@"c:\a"] = new FinderViewOptions(FinderViewMode.Icons, FinderSortKey.Size, false, false, 96)
        };

        var result = FinderViewOptionRules.With(source, "   ", FinderViewOptions.Default);

        Assert.Single(result);
        Assert.Equal(FinderViewMode.Icons, result[@"c:\a"].Mode);
    }

    [Fact]
    public void 超过容量上限时丢弃最早的记录()
    {
        var map = new Dictionary<string, FinderViewOptions>();
        for (var i = 0; i < FinderViewOptionRules.MaxFolders + 5; i++)
        {
            map = FinderViewOptionRules.With(map, $@"C:\dir{i}", FinderViewOptions.Default);
        }

        Assert.Equal(FinderViewOptionRules.MaxFolders, map.Count);
        Assert.False(map.ContainsKey(@"c:\dir0"));
        Assert.False(map.ContainsKey(@"c:\dir4"));
        Assert.True(map.ContainsKey(@"c:\dir5"));
        Assert.True(map.ContainsKey($@"c:\dir{FinderViewOptionRules.MaxFolders + 4}"));
    }

    [Fact]
    public void 查询缺失记录返回默认选项()
    {
        Assert.Equal(FinderViewOptions.Default, FinderViewOptionRules.For(null, @"C:\nowhere"));

        var map = new Dictionary<string, FinderViewOptions>
        {
            [@"c:\a"] = FinderViewOptions.Default with { Mode = FinderViewMode.Columns }
        };
        Assert.Equal(FinderViewMode.Columns, FinderViewOptionRules.For(map, @"C:\A").Mode);
        Assert.Equal(FinderViewOptions.Default, FinderViewOptionRules.For(map, @"C:\B"));
    }

    [Fact]
    public void 视图模式与排序列的字符串往返稳定()
    {
        foreach (var mode in new[]
                 {
                     FinderViewMode.List, FinderViewMode.Columns, FinderViewMode.Icons, FinderViewMode.Gallery
                 })
        {
            Assert.Equal(mode, FinderViewOptionRules.ParseMode(FinderViewOptionRules.ModeName(mode)));
        }

        foreach (var key in new[] { FinderSortKey.Name, FinderSortKey.Modified, FinderSortKey.Size, FinderSortKey.Kind })
        {
            Assert.Equal(key, FinderViewOptionRules.ParseSortKey(FinderViewOptionRules.SortKeyName(key)));
        }
    }

    [Fact]
    public void 无法识别的模式字符串回落列表视图()
    {
        Assert.Equal(FinderViewMode.List, FinderViewOptionRules.ParseMode(null));
        Assert.Equal(FinderViewMode.List, FinderViewOptionRules.ParseMode("carousel"));
        Assert.Equal(FinderViewMode.Gallery, FinderViewOptionRules.ParseMode(" Gallery "));
        Assert.Equal(FinderViewMode.Icons, FinderViewOptionRules.ParseMode(" ICONS "));
    }
}
