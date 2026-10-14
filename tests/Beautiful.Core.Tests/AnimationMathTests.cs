using System;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>帧率无关动画数学测试（Task 5）。</summary>
public class AnimationMathTests
{
    private const double Lambda = 14;

    [Fact]
    public void Damp_长时间后收敛到目标值()
    {
        Assert.Equal(10.0, AnimationMath.Damp(0, 10, Lambda, 100), 6);
        Assert.Equal(-3.0, AnimationMath.Damp(7, -3, Lambda, 50), 6);
    }

    [Fact]
    public void Damp_零时长时保持不变()
    {
        Assert.Equal(3.5, AnimationMath.Damp(3.5, 10, Lambda, 0));
    }

    [Fact]
    public void Damp_半衰期等于ln2除以lambda()
    {
        var half = AnimationMath.Damp(0, 1, Lambda, Math.Log(2) / Lambda);
        Assert.Equal(0.5, half, 9);
        var twoHalves = AnimationMath.Damp(0, 1, Lambda, 2 * Math.Log(2) / Lambda);
        Assert.Equal(0.75, twoHalves, 9);
    }

    [Fact]
    public void Damp_结果与步长细分无关_帧率无关性()
    {
        // 两个 8ms 步长应等价于一个 16ms 步长（exp 的半群性质）
        var oneBigStep = AnimationMath.Damp(0, 1, Lambda, 0.016);
        var afterSmall = AnimationMath.Damp(0, 1, Lambda, 0.008);
        var twoSmallSteps = AnimationMath.Damp(afterSmall, 1, Lambda, 0.008);
        Assert.Equal(oneBigStep, twoSmallSteps, 10);
    }

    [Fact]
    public void Damp_单向逼近目标不过冲()
    {
        var current = 0.0;
        for (var i = 0; i < 20; i++)
        {
            var next = AnimationMath.Damp(current, 1, Lambda, 0.01);
            Assert.InRange(next, current, 1.0);
            current = next;
        }
    }

    [Fact]
    public void ElasticOut_端点精确()
    {
        Assert.Equal(0.0, AnimationMath.ElasticOut(0));
        Assert.Equal(1.0, AnimationMath.ElasticOut(1));
        // 越界钳制到端点
        Assert.Equal(0.0, AnimationMath.ElasticOut(-0.5));
        Assert.Equal(1.0, AnimationMath.ElasticOut(1.5));
    }

    [Fact]
    public void ElasticOut_尾段带弹性过冲且保持有界()
    {
        Assert.True(AnimationMath.ElasticOut(0.5) > 1.0, "弹性缓出应在中段过冲超过 1");
        var sawBelowOne = false;
        for (var t = 0.0; t <= 1.0; t += 0.01)
        {
            var v = AnimationMath.ElasticOut(t);
            // 经典 elasticOut：首个波峰约 1.372（t≈0.13），无负向下探
            Assert.InRange(v, 0.0, 1.4);
            if (v < 1.0 && t > 0.6) sawBelowOne = true;
        }
        Assert.True(sawBelowOne, "过冲后应回落（振荡特性）");
    }

    [Fact]
    public void CloseEnough_默认epsilon为万分之一()
    {
        Assert.True(AnimationMath.CloseEnough(1.0, 1.0 + 5e-5));
        Assert.False(AnimationMath.CloseEnough(1.0, 1.0 + 5e-4));
        Assert.True(AnimationMath.CloseEnough(2.0, 1.0, eps: 1.0));
        Assert.False(AnimationMath.CloseEnough(2.0, 1.0, eps: 0.5));
    }

    [Fact]
    public void BounceOffset_端点精确归零且幅值线性()
    {
        // 起于静止、终于静止：t<=0 与 t>=1 恒 0（动画循环可直接摘钩，无需淡出收尾）
        Assert.Equal(0.0, AnimationMath.BounceOffset(0, 20));
        Assert.Equal(0.0, AnimationMath.BounceOffset(1, 20));
        Assert.Equal(0.0, AnimationMath.BounceOffset(-0.3, 20));
        Assert.Equal(0.0, AnimationMath.BounceOffset(1.4, 20));
        // 幅度对 A 线性：同一进度下 A=10 的偏移恰为 A=20 的一半
        Assert.Equal(AnimationMath.BounceOffset(0.2, 10) * 2,
                     AnimationMath.BounceOffset(0.2, 20), 10);
    }

    [Fact]
    public void BounceOffset_全程只上跳不坠底且峰值前部出现()
    {
        // 偏移约定负=上移：全程 (0,1) 内必须 <0（图标绝不向下扎进条带底缘之下）
        var peak = 0.0;
        var peakT = 0.0;
        for (var t = 0.001; t < 1.0; t += 0.001)
        {
            var v = AnimationMath.BounceOffset(t, 1.0);
            Assert.True(v < 0.0, $"t={t:F3} 处偏移应严格为负（只上跳），实际 {v}");
            if (v < peak) { peak = v; peakT = t; }
        }
        // 峰值 ≈1.194A、出现在前 15% 时长内（一次干脆的 hop，随后弹性余振被 (1-t) 压平归零）
        Assert.InRange(peak, -1.25, -1.15);
        Assert.InRange(peakT, 0.05, 0.15);
        // 尾段收敛：最后 10% 的偏移绝对值不再超过峰值的一半（波纹足够细密，肉眼即静止）
        for (var t = 0.9; t < 1.0; t += 0.01)
            Assert.InRange(AnimationMath.BounceOffset(t, 1.0), -0.6, 0.0);
    }
}
