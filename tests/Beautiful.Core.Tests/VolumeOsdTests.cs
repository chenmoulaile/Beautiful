using System;
using Beautiful.Core.Controls;
using Xunit;

/// <summary>
/// 音量 HUD 纯算术测试（Phase 4-T7）。
/// <para>
/// 这里钉的是"按键步进必须吸附 16 格"，不是"约等于"：连按 20 次后格数与真实音量仍要一一对应，
/// 否则真机上只能靠数格子才发现偏差。淡出与静音边界同样在这里定死。
/// </para>
/// </summary>
public class VolumeOsdTests
{
    [Fact]
    public void 每次按键正好走一格并吸附到格点()
    {
        Assert.Equal(6, VolumeOsd.Step(0, up: true));
        Assert.Equal(13, VolumeOsd.Step(6, up: true));    // 6.25 → 12.5
        Assert.Equal(19, VolumeOsd.Step(13, up: true));   // 18.75
        Assert.Equal(6, VolumeOsd.Step(13, up: false));   // 回到 6.25 附近
    }

    [Fact]
    public void 两端不越界且满格正好一百()
    {
        Assert.Equal(100, VolumeOsd.Step(100, up: true));
        Assert.Equal(94, VolumeOsd.Step(100, up: false));
        Assert.Equal(0, VolumeOsd.Step(0, up: false));
        Assert.Equal(0, VolumeOsd.Step(3, up: false));
    }

    [Fact]
    public void 连按十六次从零到满()
    {
        var percent = 0;
        for (var i = 0; i < VolumeOsd.Segments; i++)
        {
            percent = VolumeOsd.Step(percent, up: true);
        }

        Assert.Equal(100, percent);
    }

    [Fact]
    public void 亮格数按六点二五向上取整且静音为零()
    {
        Assert.Equal(0, VolumeOsd.LitSegments(0, muted: false));
        Assert.Equal(1, VolumeOsd.LitSegments(1, muted: false));
        Assert.Equal(8, VolumeOsd.LitSegments(50, muted: false));
        Assert.Equal(16, VolumeOsd.LitSegments(100, muted: false));
        Assert.Equal(0, VolumeOsd.LitSegments(100, muted: true));
    }

    [Fact]
    public void 喇叭图标按四档与静音优先()
    {
        Assert.Equal(VolumeGlyph.Mute, VolumeOsd.GlyphFor(0, muted: false));
        Assert.Equal(VolumeGlyph.Low, VolumeOsd.GlyphFor(20, muted: false));
        Assert.Equal(VolumeGlyph.Medium, VolumeOsd.GlyphFor(50, muted: false));
        Assert.Equal(VolumeGlyph.High, VolumeOsd.GlyphFor(90, muted: false));
        Assert.Equal(VolumeGlyph.Mute, VolumeOsd.GlyphFor(90, muted: true));
    }

    [Fact]
    public void 读数静音时写静音不写零()
    {
        Assert.Equal("静音", VolumeOsd.Describe(0, muted: true));
        Assert.Equal("0%", VolumeOsd.Describe(0, muted: false));
        Assert.Equal("45%", VolumeOsd.Describe(45, muted: false));
    }

    [Fact]
    public void 淡出先保持再线性归零且不为负()
    {
        var hold = TimeSpan.FromMilliseconds(900);
        var fade = TimeSpan.FromMilliseconds(250);

        Assert.Equal(1.0, VolumeOsd.OpacityAfter(TimeSpan.Zero, hold, fade));
        Assert.Equal(1.0, VolumeOsd.OpacityAfter(hold, hold, fade));
        Assert.Equal(0.5, VolumeOsd.OpacityAfter(hold + TimeSpan.FromMilliseconds(125), hold, fade),
            precision: 3);
        Assert.Equal(0.0, VolumeOsd.OpacityAfter(hold + fade, hold, fade));
        Assert.Equal(0.0, VolumeOsd.OpacityAfter(TimeSpan.FromSeconds(10), hold, fade));
    }

    [Fact]
    public void 百分比钳制与非法值()
    {
        Assert.Equal(0, VolumeOsd.Clamp(-5));
        Assert.Equal(100, VolumeOsd.Clamp(150));
        Assert.Equal(42, VolumeOsd.Clamp(42));
    }
}
