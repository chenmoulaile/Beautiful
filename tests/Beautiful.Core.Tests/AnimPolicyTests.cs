using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 动画门禁的测试：这些规则决定"该不该播"。它们全部是纯函数 + 注入时间戳，
/// 所以"200ms 内 4 个窗口被最小化"这种批次可以精确摆出来，不用 sleep、不靠真机。
/// </summary>
public class AnimPolicyTests
{
    private static AnimRequest Req(AnimKind kind, uint pid, long nowMs, string name = "notepad",
        bool fullscreen = false)
        => new(kind, pid, name, nowMs, fullscreen);

    [Fact]
    public void 全屏期间一律不播()
    {
        var p = new AnimPolicy();
        var d = p.Evaluate(Req(AnimKind.Minimize, 1, 0, fullscreen: true));
        Assert.False(d.Allowed);
        Assert.Contains("全屏", d.Reason);
    }

    [Fact]
    public void 排除清单按进程名忽略后缀与大小写()
    {
        var p = new AnimPolicy();
        p.SetExclusions(["Chrome.EXE", "obs64"]);
        Assert.Equal("排除清单", p.Evaluate(Req(AnimKind.Minimize, 1, 0, "chrome")).Reason);
        Assert.Equal("排除清单", p.Evaluate(Req(AnimKind.Minimize, 2, 500, "OBS64")).Reason);
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 3, 1000, "notepad")).Allowed);
    }

    [Fact]
    public void 并发上限三条()
    {
        // 用 Restore 而非 Minimize：四次最小化挤在 200ms 内会先命中风暴抑制，
        // 那条规则排在并发之前（真实系统里"一次收四个窗"就是风暴）。
        var p = new AnimPolicy(new AnimParams { MaxConcurrentGhosts = 3, SameProcessThrottleMs = 0 });
        Assert.True(p.Evaluate(Req(AnimKind.Restore, 1, 0)).Allowed);
        Assert.True(p.Evaluate(Req(AnimKind.Restore, 2, 10)).Allowed);
        Assert.True(p.Evaluate(Req(AnimKind.Restore, 3, 20)).Allowed);
        var fourth = p.Evaluate(Req(AnimKind.Restore, 4, 30));
        Assert.False(fourth.Allowed);
        Assert.Equal("并发上限", fourth.Reason);
        Assert.Equal(3, p.ActiveGhosts);
    }

    [Fact]
    public void 收尾释放并发槽()
    {
        var p = new AnimPolicy(new AnimParams { MaxConcurrentGhosts = 1, SameProcessThrottleMs = 0 });
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 1, 0)).Allowed);
        Assert.False(p.Evaluate(Req(AnimKind.Minimize, 2, 10)).Allowed);
        p.Complete(AnimKind.Minimize);
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 3, 20)).Allowed);
    }

    [Fact]
    public void 同进程节流三百毫秒()
    {
        var p = new AnimPolicy(new AnimParams { SameProcessThrottleMs = 300 });
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 7, 0)).Allowed);
        var blocked = p.Evaluate(Req(AnimKind.Restore, 7, 100));
        Assert.False(blocked.Allowed);
        Assert.Equal("同进程节流", blocked.Reason);
        Assert.True(p.Evaluate(Req(AnimKind.Restore, 7, 300)).Allowed);
    }

    [Fact]
    public void 风暴抑制二百毫秒内四个最小化()
    {
        var p = new AnimPolicy(new AnimParams { StormWindowMs = 200, StormCount = 4, SameProcessThrottleMs = 0 });
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 1, 0)).Allowed);
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 2, 50)).Allowed);
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 3, 100)).Allowed);
        var fourth = p.Evaluate(Req(AnimKind.Minimize, 4, 150));
        Assert.False(fourth.Allowed);
        Assert.Equal("风暴抑制", fourth.Reason);
    }

    [Fact]
    public void 风暴压制持续到窗口结束()
    {
        var p = new AnimPolicy(new AnimParams { StormWindowMs = 200, StormCount = 4, SameProcessThrottleMs = 0 });
        for (var i = 0; i < 4; i++) p.Evaluate(Req(AnimKind.Minimize, (uint)(i + 1), i * 50));
        p.Complete(AnimKind.Minimize);
        var within = p.Evaluate(Req(AnimKind.Minimize, 9, 300));   // 风暴窗口 350 之前
        Assert.False(within.Allowed);
        Assert.Equal("风暴抑制", within.Reason);
        var after = p.Evaluate(Req(AnimKind.Minimize, 10, 400));
        Assert.True(after.Allowed);
    }

    [Fact]
    public void 打开动画同进程一秒内超过三窗只播首个批次()
    {
        var p = new AnimPolicy(new AnimParams
        {
            SameProcessThrottleMs = 300, OpenBurstWindowMs = 1000, OpenBurstCount = 3,
        });
        Assert.True(p.Evaluate(Req(AnimKind.Open, 5, 0)).Allowed);
        p.Complete(AnimKind.Open);
        Assert.True(p.Evaluate(Req(AnimKind.Open, 5, 300)).Allowed);
        p.Complete(AnimKind.Open);
        Assert.True(p.Evaluate(Req(AnimKind.Open, 5, 600)).Allowed);
        p.Complete(AnimKind.Open);
        var fourth = p.Evaluate(Req(AnimKind.Open, 5, 900));
        Assert.False(fourth.Allowed);
        Assert.Contains("打开批量", fourth.Reason);
    }

    [Theory]
    [InlineData(600, CacheAge.Fresh)]
    [InlineData(700, CacheAge.Fresh)]
    [InlineData(1000, CacheAge.Stale)]
    [InlineData(1500, CacheAge.Stale)]
    [InlineData(1501, CacheAge.Expired)]
    public void 缓存龄三档判定(long ageMs, CacheAge expected)
    {
        var p = new AnimPolicy();
        Assert.Equal(expected, p.Usability(ageMs));
    }

    [Fact]
    public void 负龄判废()
    {
        var p = new AnimPolicy();
        Assert.Equal(CacheAge.Expired, p.Usability(-1));
    }

    [Fact]
    public void 时长按速度倍率折算()
    {
        var p = new AnimPolicy(new AnimParams { SpeedMultiplier = 2.0, SameProcessThrottleMs = 0 });
        var d = p.Evaluate(Req(AnimKind.Minimize, 1, 0));
        Assert.True(d.Allowed);
        Assert.Equal(250, d.DurationMs);
    }

    [Fact]
    public void 慢放与倍率叠加()
    {
        var p = new AnimPolicy(new AnimParams { SpeedMultiplier = 1.0, ShiftSlowFactor = 3.0 });
        Assert.Equal(1.0 / 3.0, p.EffectiveSpeed(slowMotion: true), 6);
    }

    [Fact]
    public void 会话重置清掉风暴与节流()
    {
        var p = new AnimPolicy(new AnimParams { SameProcessThrottleMs = 300, StormWindowMs = 200, StormCount = 4 });
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 1, 0)).Allowed);
        p.Complete(AnimKind.Minimize);
        Assert.False(p.Evaluate(Req(AnimKind.Minimize, 1, 100)).Allowed);
        p.ResetSession();
        Assert.True(p.Evaluate(Req(AnimKind.Minimize, 1, 120)).Allowed);
    }
}
