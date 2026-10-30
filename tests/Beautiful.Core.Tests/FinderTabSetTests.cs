using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// 标签页集合测试（Phase 6-c）。
/// <para>
/// 钉死三件事：新标签插在当前标签右侧并激活、关闭后激活右邻居（没有则左邻居）、
/// **每个标签的历史栈互相独立**——最后一条是 macOS 标签页的灵魂，也是最容易被
/// "共享一个 NavigationHistory" 的实现悄悄破坏的地方。
/// </para>
/// </summary>
public class FinderTabSetTests
{
    [Fact]
    public void 初始只有一个标签且激活()
    {
        var tabs = new FinderTabSet(@"C:\Users\me");

        Assert.Equal(1, tabs.Count);
        Assert.Equal(0, tabs.ActiveIndex);
        Assert.Equal(@"C:\Users\me", tabs.ActivePath);
        Assert.False(tabs.CanClose);
    }

    [Fact]
    public void 空路径构造抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FinderTabSet("  "));
    }

    [Fact]
    public void 新标签插在当前标签右侧并激活()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");

        Assert.Equal(2, tabs.Count);
        Assert.Equal(1, tabs.ActiveIndex);
        Assert.Equal(@"C:\B", tabs.ActivePath);
        Assert.Equal(new[] { @"C:\A", @"C:\B" }, tabs.Paths);
    }

    [Fact]
    public void 在中间标签上新建会插在其右侧()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");
        tabs.Add(@"C:\C");            // [A, B, C]，激活 C
        tabs.Activate(0);             // 激活 A
        var index = tabs.Add(@"C:\D"); // 应插在 A 右侧

        Assert.Equal(1, index);
        Assert.Equal(new[] { @"C:\A", @"C:\D", @"C:\B", @"C:\C" }, tabs.Paths);
        Assert.Equal(@"C:\D", tabs.ActivePath);
    }

    [Fact]
    public void 关闭激活标签后激活右邻居()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");
        tabs.Add(@"C:\C");
        tabs.Activate(1);

        Assert.True(tabs.Close(1));
        Assert.Equal(new[] { @"C:\A", @"C:\C" }, tabs.Paths);
        Assert.Equal(1, tabs.ActiveIndex);
        Assert.Equal(@"C:\C", tabs.ActivePath);
    }

    [Fact]
    public void 关闭最右标签后激活左邻居()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");
        Assert.True(tabs.Close(1));

        Assert.Equal(0, tabs.ActiveIndex);
        Assert.Equal(@"C:\A", tabs.ActivePath);
    }

    [Fact]
    public void 关闭非激活标签不改变当前激活项()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");
        tabs.Add(@"C:\C");   // 激活 C（索引 2）
        Assert.True(tabs.Close(0));

        Assert.Equal(2, tabs.Count);
        Assert.Equal(1, tabs.ActiveIndex);
        Assert.Equal(@"C:\C", tabs.ActivePath);
    }

    [Fact]
    public void 最后一个标签不可关闭()
    {
        var tabs = new FinderTabSet(@"C:\A");

        Assert.False(tabs.Close(0));
        Assert.Equal(1, tabs.Count);
        Assert.False(tabs.CanClose);
    }

    [Fact]
    public void 越界索引的操作返回false且不改状态()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Add(@"C:\B");

        Assert.False(tabs.Activate(5));
        Assert.False(tabs.Activate(-1));
        Assert.False(tabs.Close(5));
        Assert.Equal(2, tabs.Count);
        Assert.Equal(1, tabs.ActiveIndex);
    }

    [Fact]
    public void 每个标签的历史栈互相独立()
    {
        var tabs = new FinderTabSet(@"C:\A");
        tabs.Visit(@"C:\A\sub");        // A 的历史：A → A\sub
        tabs.Add(@"C:\B");              // 新标签 B
        tabs.Visit(@"C:\B\x");          // B 的历史：B → B\x

        // 回到 A：它的后退栈必须还在
        tabs.Activate(0);
        Assert.Equal(@"C:\A\sub", tabs.ActivePath);
        Assert.True(tabs.ActiveHistory.CanGoBack);
        Assert.Equal(@"C:\A", tabs.ActiveHistory.Back());

        // B 的历史没被 A 的 Back 影响
        tabs.Activate(1);
        Assert.Equal(@"C:\B\x", tabs.ActivePath);
        Assert.True(tabs.ActiveHistory.CanGoBack);
    }

    [Fact]
    public void 超过上限时拒绝新建()
    {
        var tabs = new FinderTabSet(@"C:\0", maxTabs: 2);
        Assert.Equal(1, tabs.Add(@"C:\1"));
        Assert.Equal(-1, tabs.Add(@"C:\2"));
        Assert.Equal(2, tabs.Count);
    }

    [Fact]
    public void 空路径新建返回负一()
    {
        var tabs = new FinderTabSet(@"C:\A");

        Assert.Equal(-1, tabs.Add("   "));
        Assert.Equal(1, tabs.Count);
    }

    [Fact]
    public void 后退前进会同步标签路径()
    {
        var tabs = new FinderTabSet(@"C://A");
        tabs.Visit(@"C://A//sub");

        Assert.Equal(@"C://A", tabs.Back());
        Assert.Equal(@"C://A", tabs.ActivePath);
        Assert.Equal(@"C://A//sub", tabs.Forward());
        Assert.Equal(@"C://A//sub", tabs.ActivePath);
        Assert.Null(tabs.Forward());
    }

    [Fact]
    public void 标签标题取路径末段()
    {
        var tabs = new FinderTabSet(@"C:\Users\me\Docs");

        Assert.Equal("Docs", tabs.TitleAt(0));
        Assert.Equal(string.Empty, tabs.TitleAt(3));
    }
}
