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
}
