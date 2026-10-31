using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 动画参数 token 的测试：<b>默认值就是 v2.2 §5 参数表</b>，规范化是"配置文件写坏不拖垮动画"的闸门。
/// </summary>
public class AnimParamsTests
{
    [Fact]
    public void 默认值等于v2_2参数表()
    {
        var p = AnimParams.Default;
        Assert.Equal(500, p.GenieDurationMs);
        Assert.Equal(0.30, p.LampSplit, 6);
        Assert.Equal(450, p.RestoreDurationMs);
        Assert.Equal(240, p.MorphDurationMs);
        Assert.Equal(180, p.SettleDurationMs);
        Assert.Equal(320, p.OpenDurationMs);
        Assert.Equal(0.88, p.OpenStartScale, 6);
        Assert.Equal(300, p.FromIconDurationMs);
        Assert.Equal(0.3, p.FromIconStartScale, 6);
        Assert.Equal(220, p.CloseDurationMs);
        Assert.Equal(0.86, p.CloseEndScale, 6);
        Assert.Equal(700, p.ForegroundCacheMaxAgeMs);
        Assert.Equal(500, p.ForegroundRefreshMs);
        Assert.Equal(1500, p.ForegroundCacheDiscardMs);
        Assert.Equal(9000, p.MinimizeCacheMaxAgeMs);
        Assert.Equal(3, p.MaxConcurrentGhosts);
        Assert.Equal(200, p.StormWindowMs);
        Assert.Equal(4, p.StormCount);
        Assert.Equal(300, p.SameProcessThrottleMs);
        Assert.Equal(1.0, p.SpeedMultiplier, 6);
        Assert.Equal(3.0, p.ShiftSlowFactor, 6);
        Assert.Equal("mesh", p.Engine);
    }

    [Fact]
    public void JSON往返不丢字段()
    {
        var src = AnimParams.Default with
        {
            GenieDurationMs = 640,
            LampSplit = 0.42,
            SpeedMultiplier = 1.4,
            Engine = "strips",
            ForegroundCacheMaxAgeMs = 520,
        };
        var back = AnimParams.FromJson(src.ToJson(), out var error);
        Assert.Null(error);
        Assert.Equal(640, back.GenieDurationMs);
        Assert.Equal(0.42, back.LampSplit, 6);
        Assert.Equal(1.4, back.SpeedMultiplier, 6);
        Assert.Equal("strips", back.Engine);
        Assert.Equal(520, back.ForegroundCacheMaxAgeMs);
    }

    [Fact]
    public void 写坏的JSON回落默认值并给出原因()
    {
        var parsed = AnimParams.FromJson("{ 这不是 json", out var error);
        Assert.NotNull(error);
        Assert.Equal(AnimParams.Default.GenieDurationMs, parsed.GenieDurationMs);
    }

    [Fact]
    public void 空内容回落默认值()
    {
        var parsed = AnimParams.FromJson("   ", out var error);
        Assert.NotNull(error);
        Assert.Equal(AnimParams.Default.RestoreDurationMs, parsed.RestoreDurationMs);
    }

    [Fact]
    public void 零时长与负倍率被规范化()
    {
        var bad = new AnimParams
        {
            GenieDurationMs = 0,          // 除零风险
            MorphDurationMs = -20,
            LampSplit = 9,                // 越界
            SpeedMultiplier = 99,         // 越界
            StormCount = 0,
            Engine = "  NATIVE  ",
        }.Normalized();

        Assert.Equal(500, bad.GenieDurationMs);
        Assert.Equal(240, bad.MorphDurationMs);
        Assert.Equal(0.95, bad.LampSplit, 6);
        Assert.Equal(2.0, bad.SpeedMultiplier, 6);
        Assert.Equal(4, bad.StormCount);
        // native 是合法名字（由 App 依据 native 可用性决定是否回退），规范化只做大小写与空白收敛
        Assert.Equal("native", bad.Engine);
    }

    [Fact]
    public void 未知引擎名收敛成mesh()
    {
        var bad = new AnimParams { Engine = "opengl" }.Normalized();
        Assert.Equal("mesh", bad.Engine);
    }

    [Fact]
    public void 速度倍率折算时长()
    {
        var fast = new AnimParams { SpeedMultiplier = 2.0 };
        Assert.Equal(250, fast.Scaled(500));

        var normal = new AnimParams { SpeedMultiplier = 1.0 };
        Assert.Equal(500, normal.Scaled(500));

        var slow = new AnimParams { SpeedMultiplier = 0.5 };
        Assert.Equal(1000, slow.Scaled(500));
    }

    [Fact]
    public void Shift慢放把时长乘三倍()
    {
        var p = new AnimParams { SpeedMultiplier = 1.0, ShiftSlowFactor = 3.0 };
        Assert.Equal(1500, p.Scaled(500, slowMotion: true));
    }

    [Fact]
    public void 折算时长永不为零()
    {
        var p = new AnimParams { SpeedMultiplier = 2.0 };
        Assert.True(p.Scaled(1) >= 1);
    }

    [Fact]
    public void 诊断行含关键参数()
    {
        var line = AnimParams.Default.ToLogLine();
        Assert.Contains("genie=500ms", line);
        Assert.Contains("split=0.30", line);
        Assert.Contains("风暴=4/200ms", line);
    }

    [Fact]
    public void 非法值回退到当前默认而不是历史默认()
    {
        // v0.15 调过默认（Open 320/0.88、Close 220/0.86）后，Normalized 的 fallback 必须
        // 与属性默认同源——否则配置里写个 0/-1 会悄悄回退到旧观感（200/0.94）。
        // 注：ClampInt/Double 语义 = 非法值(≤0/NaN/∞)回退默认，正数越界钳到区间边界。
        var n = new AnimParams
        {
            OpenDurationMs = 0,
            OpenStartScale = double.NaN,
            CloseDurationMs = -5,
            CloseEndScale = 0,
            MinimizeCacheMaxAgeMs = 0,
        }.Normalized();
        Assert.Equal(320, n.OpenDurationMs);
        Assert.Equal(0.88, n.OpenStartScale, 6);
        Assert.Equal(220, n.CloseDurationMs);
        Assert.Equal(0.86, n.CloseEndScale, 6);
        Assert.Equal(9000, n.MinimizeCacheMaxAgeMs);
    }
}
