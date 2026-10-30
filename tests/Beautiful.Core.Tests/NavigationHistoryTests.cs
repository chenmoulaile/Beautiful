using System;
using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Finder 前进/后退历史测试（Phase 6-a）。
/// <para>
/// 导航栈是纯状态机：首次访问不产生后退项、重复访问当前目录不入栈、后退后再访问会清空前进栈、
/// 后退栈按容量裁掉最旧项。App 侧只负责把 <see cref="NavigationHistory.Back"/>/
/// <see cref="NavigationHistory.Forward"/> 的返回值落地为当前目录，因此这些边界必须在这里锁住。
/// </para>
/// </summary>
public class NavigationHistoryTests
{
    [Fact]
    public void 初始状态_无当前目录且不能前进后退()
    {
        var h = new NavigationHistory();
        Assert.Null(h.Current);
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void 首次访问_只设置当前目录_不产生后退项()
    {
        var h = new NavigationHistory();
        h.Visit(@"C:\Users");
        Assert.Equal(@"C:\Users", h.Current);
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void 重复访问当前目录_不入栈也不能后退()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("A");
        h.Visit("A");
        Assert.Equal("A", h.Current);
        Assert.False(h.CanGoBack);

        h.Visit("B");
        Assert.Equal("A", h.Back());
        Assert.Null(h.Back()); // 若重复访问入栈，这里还会返回 A
    }

    [Fact]
    public void 后退前进往返_当前目录与两端能力同步()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("B");
        h.Visit("C");

        Assert.Equal("C", h.Current);
        Assert.True(h.CanGoBack);
        Assert.False(h.CanGoForward);

        Assert.Equal("B", h.Back());
        Assert.Equal("B", h.Current);
        Assert.True(h.CanGoBack);
        Assert.True(h.CanGoForward);

        Assert.Equal("A", h.Back());
        Assert.Equal("A", h.Current);
        Assert.False(h.CanGoBack);
        Assert.True(h.CanGoForward);

        Assert.Equal("B", h.Forward());
        Assert.Equal("B", h.Current);
        Assert.True(h.CanGoBack);
        Assert.True(h.CanGoForward);

        Assert.Equal("C", h.Forward());
        Assert.Equal("C", h.Current);
        Assert.True(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void 后退后访问新目录_清空前进栈()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("B");
        h.Visit("C");

        Assert.Equal("B", h.Back());
        Assert.True(h.CanGoForward);

        h.Visit("D");
        Assert.Equal("D", h.Current);
        Assert.False(h.CanGoForward);
        Assert.Null(h.Forward());
        Assert.Equal("B", h.Back()); // D 的前一项是被后退到的 B
    }

    [Fact]
    public void 后退栈按容量裁掉最旧项()
    {
        var h = new NavigationHistory(capacity: 2);
        h.Visit("A");
        h.Visit("B");
        h.Visit("C");
        h.Visit("D");

        Assert.Equal("D", h.Current);
        Assert.Equal("C", h.Back());
        Assert.Equal("B", h.Back());
        Assert.Null(h.Back()); // A 已被裁掉
        Assert.Equal("B", h.Current);
    }

    [Fact]
    public void 默认容量五十_超出后只能后退五十步()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < 60; i++)
        {
            h.Visit($"P{i}");
        }

        var steps = 0;
        while (h.CanGoBack)
        {
            h.Back();
            steps++;
        }

        Assert.Equal(50, steps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void 容量小于一_钳为一(int capacity)
    {
        var h = new NavigationHistory(capacity);
        h.Visit("A");
        h.Visit("B");
        Assert.Equal("A", h.Back());
        Assert.Null(h.Back());
        Assert.Equal("A", h.Current);
    }

    [Fact]
    public void 无法后退时_返回null且状态不变()
    {
        var h = new NavigationHistory();
        h.Visit("A");

        Assert.Null(h.Back());
        Assert.Equal("A", h.Current);
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void 无法前进时_返回null且状态不变()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("B");
        Assert.Equal("A", h.Back());

        Assert.Equal("B", h.Forward()); // 有前进项，先走完
        Assert.Equal("B", h.Current);
        Assert.Null(h.Forward());       // 已经到最前，再前进返回 null
        Assert.Equal("B", h.Current);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void 重复访问当前目录_保留已有的前进栈()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("B");
        Assert.Equal("A", h.Back());

        h.Visit("A"); // 与 Current 相同 → no-op，不应清空 forward
        Assert.True(h.CanGoForward);
        Assert.Equal("B", h.Forward());
    }

    [Fact]
    public void 路径按序数比较_大小写不同视为不同目录()
    {
        var h = new NavigationHistory();
        h.Visit(@"C:\A");
        h.Visit(@"c:\a");

        Assert.True(h.CanGoBack);
        Assert.Equal(@"C:\A", h.Back());
    }

    [Fact]
    public void Clear重置全部状态()
    {
        var h = new NavigationHistory();
        h.Visit("A");
        h.Visit("B");
        Assert.Equal("A", h.Back());
        Assert.True(h.CanGoForward);

        h.Clear();

        Assert.Null(h.Current);
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
        Assert.Null(h.Back());
        Assert.Null(h.Forward());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 非法路径_抛ArgumentException且不改变状态(string? path)
    {
        var h = new NavigationHistory();
        Assert.Throws<ArgumentException>(() => h.Visit(path!));

        Assert.Null(h.Current);
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }
}
