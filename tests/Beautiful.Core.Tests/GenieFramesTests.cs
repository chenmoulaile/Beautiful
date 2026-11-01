using Beautiful.Core.Animation;
using Xunit;

namespace Beautiful.Core.Tests;

/// <summary>
/// 替身帧实心化数学的公式锁（GenieFrameSource 在 App 侧只做位图搬运，判定/合成都在这里）。
/// </summary>
public class GenieFramesTests
{
    [Fact]
    public void 全实心帧不判半透明()
    {
        var px = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
        Assert.False(GenieFrames.HasTranslucency(px));
    }

    [Fact]
    public void 任一alpha小于255即判半透明()
    {
        var px = new byte[] { 1, 2, 3, 255, 4, 5, 6, 128 };
        Assert.True(GenieFrames.HasTranslucency(px));
        Assert.True(GenieFrames.HasTranslucency(new byte[] { 0, 0, 0, 0 }));
        Assert.False(GenieFrames.HasTranslucency(Array.Empty<byte>()));
        Assert.False(GenieFrames.HasTranslucency(null!));
    }

    [Fact]
    public void 合成后全帧不透明且预乘到黑底()
    {
        // 半透明蓝 (B=100,A=128)：B→100*128/255=50，A→255
        var px = new byte[] { 100, 0, 0, 128 };
        GenieFrames.CompositeOverOpaque(px, 1, 1);
        Assert.Equal(50, px[0]);
        Assert.Equal(0, px[1]);
        Assert.Equal(0, px[2]);
        Assert.Equal(255, px[3]);
    }

    [Fact]
    public void 全透明像素合成后等于纯黑底()
    {
        var px = new byte[] { 9, 8, 7, 0 };
        GenieFrames.CompositeOverOpaque(px, 1, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, px);
    }

    [Fact]
    public void 实心像素合成原样保留()
    {
        var px = new byte[] { 1, 2, 3, 255 };
        GenieFrames.CompositeOverOpaque(px, 1, 1);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, px);
    }
}
