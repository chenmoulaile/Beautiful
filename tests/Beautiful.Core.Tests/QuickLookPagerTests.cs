using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Quick Look 多选翻页测试（Phase 6-b）。
/// <para>
/// 钉死循环语义与边界：末尾按 → 回到第一项、开头按 ← 跳到末项、单项不产生位移、
/// 越界索引先钳进合法区间再走一步（避免"预览第 99 项"这种来自 UI 的脏输入把窗口打崩）。
/// </para>
/// </summary>
public class QuickLookPagerTests
{
    [Fact]
    public void 中间位置前后各走一步()
    {
        Assert.Equal(2, QuickLookPager.Step(1, 3, 1));
        Assert.Equal(0, QuickLookPager.Step(1, 3, -1));
    }

    [Fact]
    public void 末尾向后循环到首项_开头向前循环到末项()
    {
        Assert.Equal(0, QuickLookPager.Step(4, 5, 1));
        Assert.Equal(4, QuickLookPager.Step(0, 5, -1));
    }

    [Fact]
    public void 单项与空集不产生位移()
    {
        Assert.Equal(0, QuickLookPager.Step(0, 1, 1));
        Assert.Equal(0, QuickLookPager.Step(0, 1, -1));
        Assert.Equal(-1, QuickLookPager.Step(0, 0, 1));
    }

    [Fact]
    public void 越界索引先钳进合法区间()
    {
        Assert.Equal(0, QuickLookPager.Step(99, 3, 1));
        Assert.Equal(2, QuickLookPager.Step(-5, 3, -1));
    }

    [Fact]
    public void 只有多项时才显示翻页计数()
    {
        Assert.False(QuickLookPager.CanPage(0));
        Assert.False(QuickLookPager.CanPage(1));
        Assert.True(QuickLookPager.CanPage(2));
    }
}
