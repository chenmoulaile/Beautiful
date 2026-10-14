using System;
using System.Linq;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 连续高斯放大场测试（Task 5）：scale 由连续光标坐标决定，而非离散悬停索引。
/// 布局约定：5 项、baseSize=48、spacing=8 → 中心 24/80/136/192/248（间距 56）。
/// </summary>
public class MagnificationFieldTests
{
    private static readonly double[] Centers = { 24, 80, 136, 192, 248 };
    private const double BaseSize = 48;
    private const double Spacing = 8;
    private const double MaxScale = 2.0;
    private const double SigmaDip = 32;

    private static MagnificationResult Compute(double cursorX, bool centered = false) =>
        MagnificationField.Compute(cursorX, Centers, BaseSize, Spacing, MaxScale, SigmaDip, centered);

    [Fact]
    public void 光标在某项中心时该项达到最大倍率且为全局最大()
    {
        var r = Compute(136);
        Assert.Equal(MaxScale, r.Scales[2], 10);
        Assert.All(r.Scales, v => Assert.True(v <= MaxScale + 1e-10, $"scale {v} 超过 maxScale"));
        Assert.True(r.Scales[2] > r.Scales[1] && r.Scales[2] > r.Scales[3]);
    }

    [Fact]
    public void 光标在两中心中点时两项放大几乎相等()
    {
        var r = Compute((80 + 136) / 2.0);
        Assert.True(Math.Abs(r.Scales[1] - r.Scales[2]) < 1e-6,
            $"差值 {Math.Abs(r.Scales[1] - r.Scales[2])} 应小于 1e-6");
    }

    [Fact]
    public void 光标远离所有项时全部接近原始大小()
    {
        var r = Compute(10000);
        Assert.All(r.Scales, v => Assert.True(v > 0.999, $"scale {v} 应 > 0.999"));
    }

    [Fact]
    public void 偏移量单调不减()
    {
        var r = Compute(136);
        for (var i = 1; i < r.Offsets.Length; i++)
            Assert.True(r.Offsets[i] >= r.Offsets[i - 1],
                $"offset[{i}]={r.Offsets[i]} 应不小于 offset[{i - 1}]={r.Offsets[i - 1]}");
    }

    [Fact]
    public void 总宽度恒等式()
    {
        var r = Compute(100);
        var expected = r.Scales.Sum(s => s * BaseSize) + (Centers.Length - 1) * Spacing;
        Assert.Equal(expected, r.TotalWidth, 10);
    }

    [Fact]
    public void 居中模式下首项偏移为负且未居中时为零()
    {
        var plain = Compute(136, centered: false);
        Assert.Equal(0.0, plain.Offsets[0], 10);

        var centered = Compute(136, centered: true);
        Assert.True(centered.Offsets[0] < 0, $"居中展开应向左偏移，实际 {centered.Offsets[0]}");
        Assert.Equal(centered.TotalWidth, plain.TotalWidth, 10);
    }

    [Fact]
    public void 空输入返回空数组与零总宽()
    {
        var r = MagnificationField.Compute(0, Array.Empty<double>(), BaseSize, Spacing, MaxScale, SigmaDip, false);
        Assert.Empty(r.Scales);
        Assert.Empty(r.Offsets);
        Assert.Equal(0.0, r.TotalWidth);
    }

    [Fact]
    public void 单项时偏移为零且总宽为放大后宽度()
    {
        var r = MagnificationField.Compute(24, new double[] { 24 }, BaseSize, Spacing, MaxScale, SigmaDip, false);
        Assert.Equal(MaxScale, r.Scales[0], 10);
        Assert.Equal(0.0, r.Offsets[0]);
        Assert.Equal(MaxScale * BaseSize, r.TotalWidth, 10);
    }

    [Fact]
    public void 西格玛非正时抛出参数异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MagnificationField.Compute(0, Centers, BaseSize, Spacing, MaxScale, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MagnificationField.Compute(0, Centers, BaseSize, Spacing, MaxScale, -1, true));
    }

    [Fact]
    public void 最大倍率为一时等价于无放大()
    {
        var r = MagnificationField.Compute(136, Centers, BaseSize, Spacing, 1.0, SigmaDip, true);
        Assert.All(r.Scales, v => Assert.Equal(1.0, v, 10));
        Assert.All(r.Offsets, v => Assert.Equal(0.0, v, 10));
        Assert.Equal(Centers.Length * BaseSize + (Centers.Length - 1) * Spacing, r.TotalWidth, 10);
    }

    [Fact]
    public void 居中模式仅整体平移不改变相对间距()
    {
        var plain = Compute(150, centered: false);
        var centered = Compute(150, centered: true);
        var shift = plain.Offsets[0] - centered.Offsets[0];
        for (var i = 1; i < Centers.Length; i++)
            Assert.Equal(plain.Offsets[i] - centered.Offsets[i], shift, 10);
        Assert.Equal(plain.TotalWidth, centered.TotalWidth, 10);
    }

    // ---------- 调用方缓冲区重载（Task 6 评审发现 #4：动画循环每帧零分配） ----------

    [Fact]
    public void 缓冲区重载与主入口的scale逐位一致()
    {
        var buffer = new double[Centers.Length];
        MagnificationField.ComputeScalesInto(136, Centers, MaxScale, SigmaDip, buffer);
        var expected = Compute(136);
        Assert.Equal(expected.Scales.Length, buffer.Length);
        for (var i = 0; i < buffer.Length; i++) Assert.Equal(expected.Scales[i], buffer[i], 12);
    }

    [Fact]
    public void 缓冲区可复用且超长部分不被触碰()
    {
        var buffer = new double[Centers.Length + 2];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = -1.0;

        MagnificationField.ComputeScalesInto(136, Centers, MaxScale, SigmaDip, buffer);
        MagnificationField.ComputeScalesInto(24, Centers, MaxScale, SigmaDip, buffer);   // 第二帧复用同一块

        Assert.Equal(MaxScale, buffer[0], 10);                                            // 光标在首项中心
        Assert.True(buffer[Centers.Length] < 0 && buffer[Centers.Length + 1] < 0, "越界尾部不应被写入");
    }

    [Fact]
    public void 缓冲区重载对非法输入抛出异常()
    {
        var buffer = new double[Centers.Length];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MagnificationField.ComputeScalesInto(0, Centers, MaxScale, 0, buffer));
        Assert.Throws<ArgumentException>(() =>
            MagnificationField.ComputeScalesInto(0, Centers, MaxScale, SigmaDip, new double[Centers.Length - 1]));
        Assert.Throws<ArgumentNullException>(() =>
            MagnificationField.ComputeScalesInto(0, null!, MaxScale, SigmaDip, buffer));
        Assert.Throws<ArgumentNullException>(() =>
            MagnificationField.ComputeScalesInto(0, Centers, MaxScale, SigmaDip, null!));
    }

    [Fact]
    public void 缓冲区重载空输入不写不抛()
    {
        var buffer = new double[1];
        buffer[0] = 7;
        MagnificationField.ComputeScalesInto(0, Array.Empty<double>(), MaxScale, SigmaDip, buffer);
        Assert.Equal(7.0, buffer[0]);
    }
}
