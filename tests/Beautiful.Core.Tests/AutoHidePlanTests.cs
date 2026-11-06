using Beautiful.Core.Layout;
using Xunit;

/// <summary>Dock 自动隐藏的可配化状态机：隐藏延迟（旧硬编码 500ms 的可配版）与唤出方式（touch/dwell/double）。</summary>
public class AutoHidePlanTests
{
    // ---------- 隐藏延迟 ----------

    [Fact]
    public void 离开满延迟才报告隐藏_与旧硬编码语义一致()
    {
        var state = new AutoHideDelayState();
        Assert.False(state.Update(inside: true, nowMilliseconds: 0));                 // 在 Dock 上：不隐藏
        Assert.False(state.Update(inside: false, nowMilliseconds: 0));                // 刚离开：开始计时
        Assert.False(state.Update(inside: false, nowMilliseconds: 499));
        Assert.True(state.Update(inside: false, nowMilliseconds: 500));               // 满 500ms（默认值）
    }

    [Fact]
    public void 回到Dock上会清零计时()
    {
        var state = new AutoHideDelayState();
        Assert.False(state.Update(inside: false, nowMilliseconds: 0));
        Assert.False(state.Update(inside: false, nowMilliseconds: 400));
        Assert.False(state.Update(inside: true, nowMilliseconds: 450));               // 回来：清零
        Assert.False(state.Update(inside: false, nowMilliseconds: 460));              // 重新计时
        Assert.False(state.Update(inside: false, nowMilliseconds: 959));
        Assert.True(state.Update(inside: false, nowMilliseconds: 960));
    }

    [Fact]
    public void 延迟为零表示离开即滑()
    {
        var state = new AutoHideDelayState();
        Assert.False(state.Update(inside: true, nowMilliseconds: 10));
        Assert.True(state.Update(inside: false, nowMilliseconds: 11, delayMs: 0));
    }

    [Fact]
    public void 自定义延迟生效()
    {
        var state = new AutoHideDelayState();
        state.Update(inside: false, nowMilliseconds: 0);
        Assert.False(state.Update(inside: false, nowMilliseconds: 999, delayMs: 1000));
        Assert.True(state.Update(inside: false, nowMilliseconds: 1000, delayMs: 1000));
    }

    // ---------- 唤出方式映射 ----------

    [Theory]
    [InlineData(null, AutoHideRevealMode.Touch)]
    [InlineData("", AutoHideRevealMode.Touch)]
    [InlineData("touch", AutoHideRevealMode.Touch)]
    [InlineData("TOUCH", AutoHideRevealMode.Touch)]
    [InlineData(" dwell ", AutoHideRevealMode.Dwell)]
    [InlineData("double", AutoHideRevealMode.Double)]
    [InlineData("nonsense", AutoHideRevealMode.Touch)]     // 未知值兜 Touch = 历史行为
    public void 唤出方式串映射(string? raw, AutoHideRevealMode expected)
        => Assert.Equal(expected, AutoHidePlan.RevealModeOf(raw));

    // ---------- Touch：触底即回（默认 = 历史行为） ----------

    [Fact]
    public void Touch_触底立即唤出()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Touch);
        Assert.False(state.Update(hit: false, nowMilliseconds: 0));
        Assert.True(state.Update(hit: true, nowMilliseconds: 1));     // 一拍命中即触发
    }

    [Fact]
    public void Touch_触发后须离开才重新武装()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Touch);
        Assert.True(state.Update(hit: true, nowMilliseconds: 0));
        Assert.False(state.Update(hit: true, nowMilliseconds: 100));  // 已触发，不重复
        Assert.False(state.Update(hit: false, nowMilliseconds: 200)); // 离开
        Assert.True(state.Update(hit: true, nowMilliseconds: 300));   // 重新武装后可再触发
    }

    // ---------- Dwell：触底停留 300ms 才唤出 ----------

    [Fact]
    public void Dwell_停留满时长才唤出()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Dwell);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));    // 刚触底
        Assert.False(state.Update(hit: true, nowMilliseconds: 299));
        Assert.True(state.Update(hit: true, nowMilliseconds: 300));   // 满 RevealDwellMs
    }

    [Fact]
    public void Dwell_路过底边不弹()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Dwell);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));
        Assert.False(state.Update(hit: false, nowMilliseconds: 100)); // 没停够就走
        Assert.False(state.Update(hit: true, nowMilliseconds: 150));  // 再回来重新计时
        Assert.False(state.Update(hit: false, nowMilliseconds: 400));
        Assert.False(state.Update(hit: true, nowMilliseconds: 450));
        Assert.False(state.Update(hit: true, nowMilliseconds: 749));
        Assert.True(state.Update(hit: true, nowMilliseconds: 750));
    }

    // ---------- Double：触底 → 离开 → 2s 内再触底才唤出 ----------

    [Fact]
    public void Double_首次触底不唤出_二次触底才唤出()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Double);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));    // 第一次触底：仅武装
        Assert.False(state.Update(hit: false, nowMilliseconds: 200)); // 离开：窗口开始计时
        Assert.True(state.Update(hit: true, nowMilliseconds: 600));   // 400ms 后再触底：触发
    }

    [Fact]
    public void Double_窗口超时后重新武装()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Double);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));
        Assert.False(state.Update(hit: false, nowMilliseconds: 100)); // 离开，窗口到 2100 为止
        Assert.False(state.Update(hit: true, nowMilliseconds: 3000)); // 超过 RevealReentryWindowMs：重新武装
        Assert.False(state.Update(hit: false, nowMilliseconds: 3200));
        Assert.True(state.Update(hit: true, nowMilliseconds: 3400));  // 窗口内二次触底：触发
    }

    [Fact]
    public void Double_持续离开不会把窗口起点一直刷新()
    {
        // 回归（本状态机的原始版本踩过）：离开期间持续刷新窗口起点会让 2s 窗口永不过期，
        // 二次触发退化成"随便哪两次触底都行"。窗口必须从离开的跳变沿起算。
        var state = new AutoHideRevealState(AutoHideRevealMode.Double);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));
        Assert.False(state.Update(hit: false, nowMilliseconds: 100));  // 跳变沿：窗口 [100, 2100]
        Assert.False(state.Update(hit: false, nowMilliseconds: 5000)); // 持续离开不得刷新起点
        Assert.False(state.Update(hit: true, nowMilliseconds: 6000));  // 早已超窗：重新武装而非触发
    }

    [Fact]
    public void Double_触发后须Reset才再次武装()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Double);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));
        Assert.False(state.Update(hit: false, nowMilliseconds: 100));
        Assert.True(state.Update(hit: true, nowMilliseconds: 300));   // 触发（Dock 滑回）
        Assert.False(state.Update(hit: false, nowMilliseconds: 500));
        Assert.False(state.Update(hit: true, nowMilliseconds: 700));  // 已 fired：不再触发
        Assert.False(state.Update(hit: false, nowMilliseconds: 900));
        state.Reset();                                                 // DockWindow 滑回时调用
        Assert.False(state.Update(hit: true, nowMilliseconds: 1000));
        Assert.False(state.Update(hit: false, nowMilliseconds: 1200));
        Assert.True(state.Update(hit: true, nowMilliseconds: 1500));  // Reset 后可再次触发
    }

    [Fact]
    public void 换模式会清会话()
    {
        var state = new AutoHideRevealState(AutoHideRevealMode.Double);
        Assert.False(state.Update(hit: true, nowMilliseconds: 0));    // double 已武装
        state.SetMode(AutoHideRevealMode.Dwell);                       // 换模式：清会话
        Assert.False(state.Update(hit: true, nowMilliseconds: 100));   // dwell 重新计时
        Assert.False(state.Update(hit: true, nowMilliseconds: 399));
        Assert.True(state.Update(hit: true, nowMilliseconds: 400));
    }
}
