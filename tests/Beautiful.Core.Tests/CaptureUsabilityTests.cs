using System.Collections.Generic;
using Beautiful.Core.Imaging;
using Xunit;

/// <summary>
/// CaptureUsability：防"黑卡"判定（整幅纯色 = 抓失败 = 不做动画）。
/// MinimizeAnimator / WindowCloseFx 都依赖它，误放纯色帧 = 用户看到凭空出现的黑卡，
/// 误杀真实帧 = 深色主题窗口没动画——两边的边界都要锁住（wp-borrow 收口补测，2026-11-10）。
/// </summary>
public class CaptureUsabilityTests
{
    [Fact]
    public void 空表_单点_不可用()
    {
        // 少于 2 个点无法判断，一律算不可用（宁可不动画）
        Assert.False(CaptureUsability.IsUsable(null!));
        Assert.False(CaptureUsability.IsUsable(new List<uint>()));
        Assert.False(CaptureUsability.IsUsable(new List<uint> { 0xFF000000 }));
    }

    [Fact]
    public void 全同色_含alpha位差异_判定纯色不可用()
    {
        // 抓取都是不透明的：alpha 位丢弃（&0xFFFFFF）后再比——同一画面带不同 alpha 通道
        // 的两个采样点不算"内容差异"（PrintWindow 纯色帧常带 alpha=0xFF/0x00 的差异）
        Assert.False(CaptureUsability.IsUsable(new List<uint> { 0xFF000000, 0xFF000000, 0xFF000000 }));
        Assert.False(CaptureUsability.IsUsable(new List<uint> { 0x00000000, 0xFF000000 }));
        Assert.False(CaptureUsability.IsUsable(new List<uint> { 0x12FFFFFF, 0xABFFFFFF, 0x00FFFFFF }));
    }

    [Fact]
    public void 任一点颜色位不同_判可用()
    {
        // 首点之外的任一采样点颜色位不同即真实画面
        Assert.True(CaptureUsability.IsUsable(new List<uint> { 0xFF000000, 0xFF000001 }));
        Assert.True(CaptureUsability.IsUsable(new List<uint> { 0xFF000000, 0xFF000000, 0xFF123456 }));
        // alpha 位单独不同不算差异（丢弃语义）
        Assert.False(CaptureUsability.IsUsable(new List<uint> { 0xFF123456, 0x00123456 }));
    }

    [Fact]
    public void 网格取样_含首尾点_坐标落在格子中心()
    {
        // 坐标按 (int) 截断（不是四舍五入）：px(x)=min(w-1,(int)((x+0.5)*w/c))。
        // width=6, height=4, 5×4 网格：px: 0.6→0, 1.8→1, 3.0→3, 4.2→4, 5.4→5；py: 0.5→0, 1.5→1, 2.5→2, 3.5→3
        var pts = CaptureUsability.SampleGrid(6, 4);
        Assert.Equal(20, pts.Count);                       // 5 列 × 4 行
        Assert.Equal((0, 0), pts[0]);                      // 首点 = 左上格（截断语义：0.5*6/5=0.6→0）
        Assert.Equal((5, 3), pts[^1]);                     // 尾点 = 右下格（4.5*6/5=5.4→5，3.5*4/4=3.5→3）
        Assert.Equal((3, 0), pts[2]);                      // 第 3 列第 1 行：2.5*6/5=3.0→3
        Assert.Equal((1, 1), pts[6]);                      // index6 = 行2列2：y=1,x=1 → 1.8→1, 1.5*4/4=1.5→1
    }

    [Fact]
    public void 非法尺寸_空网格_行列数钳到1()
    {
        Assert.Empty(CaptureUsability.SampleGrid(0, 100));
        Assert.Empty(CaptureUsability.SampleGrid(100, 0));
        Assert.Empty(CaptureUsability.SampleGrid(-3, -3));
        // columns/rows 传 0 或负数钳到 1：至少采一个点，不会除零
        Assert.Single(CaptureUsability.SampleGrid(10, 10, columns: 0, rows: 0));
        var one = CaptureUsability.SampleGrid(10, 10, columns: -2, rows: -2);
        Assert.Single(one);
        Assert.Equal((5, 5), one[0]);                      // 单格中心
    }
}
