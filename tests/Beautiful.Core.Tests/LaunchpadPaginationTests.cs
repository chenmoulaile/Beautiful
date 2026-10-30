using Beautiful.Core.Launchpad;
using Xunit;

/// <summary>
/// Launchpad 分页纯函数测试：页数、页内槽位映射、跨页边界、空列表退化。
/// </summary>
public class LaunchpadPaginationTests
{
    // ---------- PageCount ----------

    [Fact]
    public void 空图标_返回_1_页_占位()
    {
        Assert.Equal(1, LaunchpadPagination.PageCount(0, 30));
        Assert.Equal(1, LaunchpadPagination.PageCount(-5, 30));
    }

    [Fact]
    public void 刚好整页_页数_恰好_除尽()
    {
        Assert.Equal(3, LaunchpadPagination.PageCount(90, 30));
        Assert.Equal(1, LaunchpadPagination.PageCount(30, 30));
    }

    [Fact]
    public void 多一图标_多一页()
    {
        Assert.Equal(4, LaunchpadPagination.PageCount(91, 30));
        Assert.Equal(2, LaunchpadPagination.PageCount(31, 30));
    }

    [Theory]
    [InlineData(5, 30)]   // 少于一页
    [InlineData(1, 30)]
    [InlineData(30, 30)]  // 恰好一页
    public void 少于一页_返回_1_页(int count, int capacity)
        => Assert.Equal(1, LaunchpadPagination.PageCount(count, capacity));

    [Fact]
    public void 容量_0_钳到_1_避免除零()
    {
        // capacity=0 → 当作 1 → 90 图标 = 90 页
        Assert.Equal(90, LaunchpadPagination.PageCount(90, 0));
        // capacity 负的也钳到 1
        Assert.Equal(90, LaunchpadPagination.PageCount(90, -1));
    }

    // ---------- PageOf ----------

    [Fact]
    public void 索引_0_是第_0_页第_0_槽()
    {
        var (page, slot) = LaunchpadPagination.PageOf(0, 30);
        Assert.Equal(0, page);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void 索引_30_是第_1_页第_0_槽()
    {
        var (page, slot) = LaunchpadPagination.PageOf(30, 30);
        Assert.Equal(1, page);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void 索引_29_是第_0_页第_29_槽()
    {
        var (page, slot) = LaunchpadPagination.PageOf(29, 30);
        Assert.Equal(0, page);
        Assert.Equal(29, slot);
    }

    [Fact]
    public void 索引_65_是第_2_页第_5_槽()
    {
        var (page, slot) = LaunchpadPagination.PageOf(65, 30);
        Assert.Equal(2, page);
        Assert.Equal(5, slot);
    }

    [Fact]
    public void 容量_1_时_每图标一页()
    {
        for (var i = 0; i < 5; i++)
        {
            var (page, slot) = LaunchpadPagination.PageOf(i, 1);
            Assert.Equal(i, page);
            Assert.Equal(0, slot);
        }
    }

    [Fact]
    public void 负索引_抛出()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchpadPagination.PageOf(-1, 30));
    }

    // ---------- SpanOf ----------

    [Fact]
    public void 第_0_页_从第_0_个_有_30_个()
    {
        var (first, count) = LaunchpadPagination.SpanOf(0, 91, 30);
        Assert.Equal(0, first);
        Assert.Equal(30, count);
    }

    [Fact]
    public void 末页_可能不满()
    {
        var (first, count) = LaunchpadPagination.SpanOf(3, 91, 30);
        Assert.Equal(90, first);
        Assert.Equal(1, count);
    }

    [Fact]
    public void 页码超出尾页_返回_0_个图标()
    {
        var (first, count) = LaunchpadPagination.SpanOf(5, 91, 30);
        Assert.Equal(150, first); // 5 * 30
        Assert.Equal(0, count);
    }

    [Fact]
    public void 负页码_抛出()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchpadPagination.SpanOf(-1, 91, 30));
    }

    [Fact]
    public void 空列表第0页_返回_0_0()
    {
        var (first, count) = LaunchpadPagination.SpanOf(0, 0, 30);
        Assert.Equal(0, first);
        Assert.Equal(0, count);
    }

    // ---------- Plan ----------

    [Fact]
    public void Plan_空列表_返回_空数组()
        => Assert.Empty(LaunchpadPagination.Plan(0, 30));

    [Fact]
    public void Plan_生成的映射_与_PageOf_逐一一致()
    {
        var total = 91;
        var capacity = 30;
        var plan = LaunchpadPagination.Plan(total, capacity);
        Assert.Equal(total, plan.Length);
        for (var i = 0; i < total; i++)
        {
            var expected = LaunchpadPagination.PageOf(i, capacity);
            Assert.Equal(expected.Page, plan[i].Page);
            Assert.Equal(expected.SlotInPage, plan[i].SlotInPage);
        }
    }

    [Fact]
    public void Plan_跨越多页_页号单调非减()
    {
        var plan = LaunchpadPagination.Plan(95, 30);
        var lastPage = -1;
        foreach (var (page, slot) in plan)
        {
            Assert.True(page >= lastPage);
            lastPage = page;
        }
    }

    [Theory]
    [InlineData(90, 30)]
    [InlineData(91, 30)]
    [InlineData(1, 30)]
    [InlineData(0, 30)]
    public void Plan_长度恰好等于图标总数(int total, int capacity)
        => Assert.Equal(Math.Max(0, total), LaunchpadPagination.Plan(total, capacity).Length);
}
